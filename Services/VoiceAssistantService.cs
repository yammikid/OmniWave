using System.IO;

namespace DynamicIsland.Services;

public enum VoiceAssistantState
{
    NotInstalled,
    Downloading,
    Idle,
    WakeListening,
    CommandListening,
    ProcessingWhisper,
    Speaking
}

public sealed record VoiceAssistantStatus(
    VoiceAssistantState State,
    string StateKey,
    string StatusText,
    string LiveTranscript,
    string LastCommand,
    string LastReply,
    float AudioLevel,
    bool VoskReady,
    bool WhisperReady,
    bool WhisperRunning,
    bool NeuralTtsReady,
    bool VadReady,
    bool DownloadBusy,
    int DownloadPercent,
    string DownloadStage,
    double DownloadMb,
    double DownloadTotalMb,
    double DiskUsageMb,
    bool WakeWordEnabled,
    string WakeWords,
    string ActiveEngineLabel);

/// <summary>
/// Гибридный офлайн голосовый помощник OmniWave:
/// - Потоковый Silero VAD v5 (ONNX) + Vosk (RU) для непрерывного распознавания ключевого слова
/// - Локальный Whisper Large v3 Turbo (RU+EN) + Silero VAD GGML
/// - Нейросетевая женская озвучка Supertonic 3 (F1–F5), автовыгрузка из RAM при простое
/// </summary>
public sealed class VoiceAssistantService : IDisposable
{
    private const int PreRollMaxChunks = 18; // ~1.44 с при 80 мс чанках
    private const int MaxCommandMs = 9500;
    private const int InitialSilenceTimeoutMs = 5500;
    private const int TrailingSilenceMs = 1300;
    private const int TrailingSilenceVadMs = 1150;
    private const int FinalQuietConfirmMs = 480;
    private const float SpeechRmsThreshold = 0.045f;
    private const float VadSpeechThreshold = 0.35f;
    private const int IdleUnloadMs = 75_000;

    private readonly VoiceModelManager _models;
    private readonly VoiceFeedbackService _feedback;
    private readonly Func<string, Task<AliceCommandResult>> _commandExecutor;
    private readonly Func<string>? _contextPromptProvider;
    private readonly MicrophoneCaptureService _mic = new();
    private readonly VoskNativeEngine _vosk = new();
    private readonly SileroVadEngine _vad = new();
    private readonly WhisperLocalClient _whisper;
    private readonly SemaphoreSlim _engineStateLock = new(1, 1);
    private readonly System.Threading.Timer _idleUnloadTimer;

    private readonly object _syncLock = new();
    private readonly Queue<byte[]> _preRoll = new(PreRollMaxChunks + 2);
    private readonly MemoryStream _utterancePcm = new(256 * 1024);

    private VoiceAssistantState _state = VoiceAssistantState.NotInstalled;
    private string _statusText = "Установите офлайн-модель (Vosk / Whisper Turbo)";
    private string _liveTranscript = "";
    private string _lastCommand = "";
    private string _lastReply = "";
    private float _audioLevel;

    private bool _wakeWordEnabled;
    private string _wakeWordsCsv = "омни, волна";
    private IReadOnlyList<string> _wakeAliases = AliceCommandParser.ExpandWakeAliases("омни, волна");
    private bool _whisperEnabled = true;
    private string _whisperModel = "turbo-q5";
    private bool _ttsEnabled = true;
    private string _ttsVoiceName = "supertonic:F1";
    private bool _duckMusic = true;
    private int _inputDeviceIndex = -1;

    private bool _speechDetected;
    private string _voskCandidateText = "";
    private long _commandStartTick;
    private long _lastSpeechTick;
    private long _lastFinalTick;
    private long _muteAudioUntilTick;
    private long _lastLevelPushTick;
    private long _lastPartialStatusTick;
    private long _lastVoiceActivityTick;
    private CancellationTokenSource? _processingCts;
    private bool _disposed;

    public event Action<VoiceAssistantStatus>? StatusChanged;
    public event Action<float, string>? AudioLevelChanged;
    public event Action<bool>? DuckingRequested;

    public VoiceAssistantService(
        VoiceModelManager models,
        VoiceFeedbackService feedback,
        Func<string, Task<AliceCommandResult>> commandExecutor,
        Func<string>? contextPromptProvider = null)
    {
        _models = models;
        _feedback = feedback;
        _commandExecutor = commandExecutor;
        _contextPromptProvider = contextPromptProvider;
        _whisper = new WhisperLocalClient(models);
        _idleUnloadTimer = new System.Threading.Timer(OnIdleUnloadTimer, null, Timeout.Infinite, Timeout.Infinite);

        _mic.AudioChunkCaptured += OnAudioChunkCaptured;
        _models.ProgressChanged += OnModelDownloadProgress;
    }

    public VoiceAssistantStatus GetStatusSnapshot()
    {
        lock (_syncLock)
            return BuildStatusLocked();
    }

    public void ApplySettings(AppSettings settings)
    {
        if (_disposed) return;

        bool prevWake;
        int prevDev;
        lock (_syncLock)
        {
            prevWake = _wakeWordEnabled;
            prevDev = _inputDeviceIndex;

            _wakeWordEnabled = settings.VoiceWakeWordEnabled;
            _wakeWordsCsv = settings.VoiceWakeWords;
            _wakeAliases = AliceCommandParser.ExpandWakeAliases(settings.VoiceWakeWords);
            _whisperEnabled = settings.VoiceWhisperEnabled;
            _whisperModel = settings.VoiceWhisperModel;
            _ttsEnabled = settings.VoiceTtsEnabled;
            _ttsVoiceName = settings.VoiceTtsVoiceName;
            _duckMusic = settings.VoiceDuckMusic;
            _inputDeviceIndex = settings.VoiceInputDeviceIndex;

            if (!_whisperEnabled && _whisper.IsRunning)
                _ = Task.Run(() => _whisper.Stop());
        }

        _ = RefreshEngineStateAsync(restartMic: prevWake != _wakeWordEnabled || prevDev != _inputDeviceIndex);
    }

    public async Task<(bool Ok, string Message)> InstallModelsAsync(string mode)
    {
        if (_disposed) return (false, "Сервис остановлен");

        CancelActiveListening(silent: true);
        _mic.Stop();
        _whisper.Stop();

        lock (_syncLock)
        {
            _state = VoiceAssistantState.Downloading;
            _statusText = "Подготовка загрузки моделей...";
            _liveTranscript = "";
        }
        EmitStatus();

        string variant;
        lock (_syncLock) variant = _whisperModel;

        var res = await _models.InstallAsync(mode, variant);
        await RefreshEngineStateAsync(restartMic: true);
        return res;
    }

    public async Task<bool> DeleteModelsAsync()
    {
        CancelActiveListening(silent: true);
        _mic.Stop();
        _whisper.Stop();
        _vosk.Dispose();
        _feedback.UnloadNeuralModels();

        bool ok = _models.DeleteAllModels();
        await RefreshEngineStateAsync(restartMic: false);
        return ok;
    }

    public async Task ToggleManualListeningAsync()
    {
        if (_disposed) return;

        VoiceAssistantState cur;
        bool hasAnyModel;
        bool wakeOn;
        lock (_syncLock)
        {
            cur = _state;
            hasAnyModel = _models.IsVoskReady() || _models.IsWhisperReady(_whisperModel);
            wakeOn = _wakeWordEnabled;
        }

        if (cur == VoiceAssistantState.Downloading)
            return;

        if (!hasAnyModel)
        {
            lock (_syncLock)
            {
                _state = VoiceAssistantState.NotInstalled;
                _statusText = "Сначала установите модели Vosk / Whisper в настройках";
            }
            EmitStatus();
            return;
        }

        if (cur == VoiceAssistantState.CommandListening)
        {
            bool hasSpeech;
            lock (_syncLock) hasSpeech = _speechDetected && _utterancePcm.Length > 3200;
            if (hasSpeech)
                TriggerFinalizeUtterance();
            else
                CancelActiveListening(silent: false);
            return;
        }

        if (cur == VoiceAssistantState.Speaking)
            _feedback.StopSpeaking();

        int devIdx;
        bool needDuck;
        bool warmWhisper;
        string variant;
        lock (_syncLock)
        {
            devIdx = _inputDeviceIndex;
            needDuck = _duckMusic;
            variant = _whisperModel;
            warmWhisper = _whisperEnabled && _models.IsWhisperReady(variant);
        }

        if (_models.IsSileroVadReady() && !_vad.IsReady)
            await Task.Run(() => _vad.Initialize(_models.OnnxRuntimeDir, _models.SileroVadOnnxPath));

        if (!warmWhisper && _models.IsVoskReady() && !_vosk.IsReady)
        {
            string? voskPath = _models.GetResolvedVoskModelPath();
            if (!string.IsNullOrWhiteSpace(voskPath))
                await Task.Run(() => _vosk.Initialize(_models.VoskBinDir, voskPath));
        }

        if (!_mic.Start(devIdx))
        {
            lock (_syncLock)
            {
                _state = VoiceAssistantState.Idle;
                _statusText = "Микрофон недоступен или занят другим приложением";
            }
            EmitStatus();
            return;
        }

        if (warmWhisper)
            _ = Task.Run(() => _whisper.EnsureStartedAsync(variant));

        if (needDuck)
            DuckingRequested?.Invoke(true);

        long now = Environment.TickCount64;
        _vad.ResetState();
        lock (_syncLock)
        {
            _lastVoiceActivityTick = now;
            _muteAudioUntilTick = now + 220;
            _vosk.Reset();
            _utterancePcm.SetLength(0);
            _speechDetected = false;
            _voskCandidateText = "";
            _liveTranscript = "";
            _commandStartTick = now;
            _lastSpeechTick = now;
            _lastFinalTick = 0;
            _state = VoiceAssistantState.CommandListening;
            _statusText = "Слушаю команду...";
        }

        EmitStatus();
        _ = Task.Run(() => _feedback.PlayWakeChimeAsync());
    }

    public void CancelActiveListening(bool silent = false)
    {
        try { _processingCts?.Cancel(); } catch { }
        _feedback.StopSpeaking();
        DuckingRequested?.Invoke(false);

        bool wakeOn;
        bool hasAnyModel;
        lock (_syncLock)
        {
            wakeOn = _wakeWordEnabled;
            hasAnyModel = _models.IsVoskReady() || _models.IsWhisperReady(_whisperModel);
            _utterancePcm.SetLength(0);
            _liveTranscript = "";
            _audioLevel = 0;
            _speechDetected = false;
            _voskCandidateText = "";

            if (_models.CurrentProgress.Busy)
            {
                _state = VoiceAssistantState.Downloading;
            }
            else if (!hasAnyModel)
            {
                _state = VoiceAssistantState.NotInstalled;
                _statusText = "Установите офлайн-модель (Vosk / Whisper)";
            }
            else if (wakeOn && _mic.IsCapturing)
            {
                _muteAudioUntilTick = Environment.TickCount64 + 220;
                _vosk.Reset();
                _state = VoiceAssistantState.WakeListening;
                _statusText = BuildWakeListeningLabelLocked();
            }
            else
            {
                _state = VoiceAssistantState.Idle;
                _statusText = BuildIdleLabelLocked();
            }
        }

        if (!wakeOn)
        {
            _mic.Stop();
            lock (_syncLock) _lastVoiceActivityTick = Environment.TickCount64;
            ScheduleIdleUnload();
        }

        if (!silent)
            _ = Task.Run(() => _feedback.PlayCancelChimeAsync());

        EmitStatus();
    }

    private async Task RefreshEngineStateAsync(bool restartMic)
    {
        if (_disposed) return;

        await _engineStateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            bool voskReady = _models.IsVoskReady();
            string variant;
            bool wakeOn;
            int devIdx;
            lock (_syncLock)
            {
                if (_models.CurrentProgress.Busy || _state == VoiceAssistantState.Downloading)
                    return;
                variant = _whisperModel;
                wakeOn = _wakeWordEnabled;
                devIdx = _inputDeviceIndex;
            }

            bool whisperReady = _models.IsWhisperReady(variant);

            if (wakeOn && voskReady)
            {
                if (!_vosk.IsReady)
                {
                    string? modelPath = _models.GetResolvedVoskModelPath();
                    if (!string.IsNullOrWhiteSpace(modelPath))
                        await Task.Run(() => _vosk.Initialize(_models.VoskBinDir, modelPath));
                }
                if (_models.IsSileroVadReady() && !_vad.IsReady)
                    await Task.Run(() => _vad.Initialize(_models.OnnxRuntimeDir, _models.SileroVadOnnxPath));
            }

            if (!voskReady && !whisperReady)
            {
                _mic.Stop();
                _vosk.Unload();
                _vad.Unload();
                lock (_syncLock)
                {
                    _state = VoiceAssistantState.NotInstalled;
                    _statusText = "Нажмите «Установить», чтобы скачать русскую модель";
                }
                EmitStatus();
                return;
            }

            if (wakeOn && _vosk.IsReady)
            {
                if (restartMic || !_mic.IsCapturing)
                {
                    if (restartMic) _mic.Stop();
                    if (!_mic.Start(devIdx))
                    {
                        lock (_syncLock)
                        {
                            _state = VoiceAssistantState.Idle;
                            _statusText = "Не удалось открыть микрофон для прослушивания";
                        }
                        EmitStatus();
                        return;
                    }
                }

                lock (_syncLock)
                {
                    if (_state is not (VoiceAssistantState.CommandListening or VoiceAssistantState.ProcessingWhisper or VoiceAssistantState.Speaking))
                    {
                        _vosk.Reset();
                        _vad.ResetState();
                        _state = VoiceAssistantState.WakeListening;
                        _statusText = BuildWakeListeningLabelLocked();
                    }
                }
            }
            else
            {
                bool activeCmd;
                lock (_syncLock)
                {
                    activeCmd = _state is VoiceAssistantState.CommandListening or VoiceAssistantState.ProcessingWhisper or VoiceAssistantState.Speaking;
                    if (!activeCmd)
                    {
                        _state = VoiceAssistantState.Idle;
                        _statusText = wakeOn && !voskReady
                            ? "Для фонового ключевого слова требуется модель Vosk"
                            : BuildIdleLabelLocked();
                    }
                }
                if (!activeCmd)
                {
                    _mic.Stop();
                    _vosk.Unload();
                    _vad.Unload();
                    WindowBlurHelper.TrimWorkingSet();
                }
            }

            EmitStatus();
        }
        finally
        {
            try { _engineStateLock.Release(); } catch { }
        }
    }

    private void OnAudioChunkCaptured(byte[] pcm, int length, float rms)
    {
        if (_disposed) return;

        long now = Environment.TickCount64;
        VoiceAssistantState preState;
        lock (_syncLock)
        {
            preState = _state;
            if (preState != VoiceAssistantState.WakeListening && preState != VoiceAssistantState.CommandListening)
                return;

            // Сохраняем pre-roll в режиме ожидания wake word
            if (preState == VoiceAssistantState.WakeListening)
            {
                _preRoll.Enqueue(pcm);
                while (_preRoll.Count > PreRollMaxChunks)
                    _preRoll.Dequeue();
            }

            if (now < _muteAudioUntilTick)
                return;
        }

        // Нейросетевой Silero VAD: в WakeListening Vosk получает НЕПРЕРЫВНЫЙ поток чанков
        // (иначе обрезается первый слог wake-слова и сбивается Kaldi CMVN).
        float vadProb = (preState == VoiceAssistantState.CommandListening && _vad.IsReady)
            ? _vad.ProcessPcm16kChunk(pcm, length)
            : -1f;
        bool isSpeechFrame = vadProb >= 0f ? (vadProb >= VadSpeechThreshold) : (rms >= SpeechRmsThreshold);

        bool shouldFinalize = false;
        bool shouldCancelTimeout = false;
        bool shouldPushLevel = false;
        string liveTextCopy = "";

        lock (_syncLock)
        {
            if (_state != VoiceAssistantState.WakeListening && _state != VoiceAssistantState.CommandListening)
                return;

            if (now < _muteAudioUntilTick)
                return;

            _audioLevel = rms;
            VoiceAssistantState curState = _state;

            if (curState == VoiceAssistantState.CommandListening)
            {
                if (_utterancePcm.Length + length <= 320 * 1024)
                    _utterancePcm.Write(pcm, 0, length);

                if (isSpeechFrame)
                {
                    _speechDetected = true;
                    _lastSpeechTick = now;
                }
            }

            (bool isFinal, string finalText, string partialText) voskRes = _vosk.IsReady
                ? _vosk.FeedAudio(pcm, length)
                : (false, "", "");

            if (curState == VoiceAssistantState.WakeListening)
            {
                string checkStr = !string.IsNullOrWhiteSpace(voskRes.partialText) ? voskRes.partialText : voskRes.finalText;
                if (!string.IsNullOrWhiteSpace(checkStr) && TryDetectWakeWord(checkStr, out string trailing))
                {
                    bool hasInlineCommand = !string.IsNullOrWhiteSpace(trailing);
                    bool warmWhisper = _whisperEnabled && _models.IsWhisperReady(_whisperModel);
                    string variant = _whisperModel;
                    bool needDuck = _duckMusic;

                    if (hasInlineCommand)
                    {
                        // Команда произнесена слитно («Ирина включи Кино») — сохраняем pre-roll, не сбрасываем буфер
                        _utterancePcm.SetLength(0);
                        foreach (var chunk in _preRoll)
                            _utterancePcm.Write(chunk, 0, chunk.Length);
                        _preRoll.Clear();

                        _speechDetected = true;
                        _voskCandidateText = voskRes.isFinal ? trailing : "";
                        _liveTranscript = trailing;
                        _lastFinalTick = voskRes.isFinal ? now : 0;
                        _muteAudioUntilTick = 0;
                    }
                    else
                    {
                        // Отдельное слово активации («Ирина») — ждём следующую фразу с чистого буфера
                        _utterancePcm.SetLength(0);
                        _preRoll.Clear();
                        _vosk.Reset();
                        _vad.ResetState();

                        _speechDetected = false;
                        _voskCandidateText = "";
                        _liveTranscript = "";
                        _lastFinalTick = 0;
                        _muteAudioUntilTick = now + 100;
                    }

                    _lastVoiceActivityTick = now;
                    _commandStartTick = now;
                    _lastSpeechTick = now;
                    _state = VoiceAssistantState.CommandListening;
                    _statusText = "Слушаю команду...";

                    if (warmWhisper)
                        _ = Task.Run(() => _whisper.EnsureStartedAsync(variant));
                    if (needDuck)
                        DuckingRequested?.Invoke(true);
                    if (!hasInlineCommand)
                        _ = Task.Run(() => _feedback.PlayWakeChimeAsync());
                    _ = Task.Run(EmitStatus);
                    return;
                }
                return;
            }

            // CommandListening
            if (voskRes.isFinal && !string.IsNullOrWhiteSpace(voskRes.finalText))
            {
                string stripped = AliceCommandParser.StripWakeWords(voskRes.finalText, _wakeAliases);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    _voskCandidateText = string.IsNullOrWhiteSpace(_voskCandidateText)
                        ? stripped
                        : $"{_voskCandidateText} {stripped}".Trim();
                    _speechDetected = true;
                    _lastSpeechTick = now;
                    _lastFinalTick = now;
                }
            }
            else if (!string.IsNullOrWhiteSpace(voskRes.partialText))
            {
                string strippedPartial = AliceCommandParser.StripWakeWords(voskRes.partialText, _wakeAliases);
                if (!string.IsNullOrWhiteSpace(strippedPartial))
                {
                    _liveTranscript = string.IsNullOrWhiteSpace(_voskCandidateText)
                        ? strippedPartial
                        : $"{_voskCandidateText} {strippedPartial}".Trim();
                    _speechDetected = true;
                    _lastSpeechTick = now;
                }
            }
            else if (!string.IsNullOrWhiteSpace(_voskCandidateText))
            {
                _liveTranscript = _voskCandidateText;
            }

            liveTextCopy = _liveTranscript;

            if (now - _lastLevelPushTick >= 75)
            {
                _lastLevelPushTick = now;
                shouldPushLevel = true;
            }

            long elapsed = now - _commandStartTick;
            long silence = now - _lastSpeechTick;
            int trailingLimit = _vad.IsReady ? TrailingSilenceVadMs : TrailingSilenceMs;

            if (!_speechDetected && elapsed >= InitialSilenceTimeoutMs)
            {
                shouldCancelTimeout = true;
            }
            else if (_speechDetected &&
                     (elapsed >= MaxCommandMs ||
                      silence >= trailingLimit ||
                      (_lastFinalTick > 0 && now - _lastFinalTick >= FinalQuietConfirmMs && silence >= FinalQuietConfirmMs)))
            {
                shouldFinalize = true;
            }
        }

        if (shouldPushLevel)
        {
            AudioLevelChanged?.Invoke(rms, liveTextCopy);
            if (now - _lastPartialStatusTick >= 260)
            {
                _lastPartialStatusTick = now;
                EmitStatus();
            }
        }

        if (shouldCancelTimeout)
            CancelActiveListening(silent: false);
        else if (shouldFinalize)
            TriggerFinalizeUtterance();
    }

    public bool TryDetectWakeWord(string rawTranscript, out string trailingCommand)
    {
        trailingCommand = "";
        string norm = rawTranscript.Trim().ToLowerInvariant().Replace('ё', 'е');
        norm = System.Text.RegularExpressions.Regex.Replace(norm, @"[^\p{L}\p{N}\s%-]+", " ");
        norm = System.Text.RegularExpressions.Regex.Replace(norm, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(norm))
            return false;

        foreach (var alias in _wakeAliases)
        {
            int idx = FindWordBoundary(norm, alias);
            if (idx >= 0)
            {
                int after = idx + alias.Length;
                trailingCommand = after < norm.Length ? norm[after..].Trim() : "";
                trailingCommand = AliceCommandParser.StripWakeWords(trailingCommand, _wakeAliases);
                return true;
            }
        }

        // Нечёткое совпадение отдельных слов для опечаток Vosk («ирине», «арина», «омне» и т.п.)
        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            foreach (var alias in _wakeAliases)
            {
                if (alias.Contains(' ', StringComparison.Ordinal) || alias.Length < 4)
                    continue;
                if (AliceCommandParser.IsCloseSingleWord(tok, alias))
                {
                    trailingCommand = i + 1 < tokens.Length ? string.Join(' ', tokens.Skip(i + 1)) : "";
                    trailingCommand = AliceCommandParser.StripWakeWords(trailingCommand, _wakeAliases);
                    return true;
                }
            }
        }
        return false;
    }

    private static int FindWordBoundary(string haystack, string word)
    {
        int idx = haystack.IndexOf(word, StringComparison.Ordinal);
        while (idx >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            int after = idx + word.Length;
            bool rightOk = after >= haystack.Length || !char.IsLetterOrDigit(haystack[after]);
            if (leftOk && rightOk)
                return idx;
            idx = haystack.IndexOf(word, idx + 1, StringComparison.Ordinal);
        }
        return -1;
    }

    private void TriggerFinalizeUtterance()
    {
        byte[] pcmSnapshot;
        string voskText;
        IReadOnlyList<string> aliases;
        bool useWhisper;
        string whisperVariant;
        bool useTts;
        string ttsVoice;
        bool wakeOn;
        bool needDuck;

        lock (_syncLock)
        {
            if (_state != VoiceAssistantState.CommandListening)
                return;

            string tail = _vosk.IsReady ? _vosk.FinalizeUtterance() : "";
            string cleanTail = AliceCommandParser.StripWakeWords(tail, _wakeAliases);
            voskText = !string.IsNullOrWhiteSpace(cleanTail)
                ? cleanTail
                : (!string.IsNullOrWhiteSpace(_voskCandidateText) ? _voskCandidateText : _liveTranscript);
            voskText = AliceCommandParser.StripWakeWords(voskText, _wakeAliases);

            pcmSnapshot = _utterancePcm.ToArray();
            _utterancePcm.SetLength(0);
            _audioLevel = 0;

            aliases = _wakeAliases;
            useWhisper = _whisperEnabled && _models.IsWhisperReady(_whisperModel);
            whisperVariant = _whisperModel;
            useTts = _ttsEnabled;
            ttsVoice = _ttsVoiceName;
            wakeOn = _wakeWordEnabled;
            needDuck = _duckMusic;

            _state = VoiceAssistantState.ProcessingWhisper;
            _statusText = "Обработка команды...";
        }

        if (!wakeOn)
        {
            _mic.Stop();
            lock (_syncLock) _lastVoiceActivityTick = Environment.TickCount64;
            ScheduleIdleUnload();
        }

        EmitStatus();

        _processingCts?.Cancel();
        _processingCts?.Dispose();
        var cts = new CancellationTokenSource();
        _processingCts = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                string finalCommand = voskText;
                bool useWhisperRefine = useWhisper && pcmSnapshot.Length >= 3200;
                bool fastVoskHit = false;

                if (!string.IsNullOrWhiteSpace(voskText))
                {
                    var fastParsed = AliceCommandParser.Parse(voskText, aliases);
                    fastVoskHit = AliceCommandParser.IsFastControlCommand(fastParsed.Type);
                }

                // Гибридный маршрутизатор: быстрые команды — сразу через Vosk (~80 мс),
                // названия треков / исполнителей / плейлистов / пустой результат Vosk — уточняем через Silero VAD + Whisper Turbo v3 (RU+EN).
                if (!fastVoskHit && useWhisperRefine)
                {
                    byte[] whisperPcm = pcmSnapshot;
                    if (_vad.IsReady && whisperPcm.Length > 3200)
                    {
                        var trimmed = _vad.TrimNonSpeechPcm16k(whisperPcm, threshold: 0.35f);
                        if (trimmed.Length >= 2400)
                            whisperPcm = trimmed;
                    }

                    if (ComputeRms16k(whisperPcm) >= 0.0018f || _speechDetected)
                    {
                        lock (_syncLock)
                        {
                            _statusText = "Распознаю (Whisper Turbo)...";
                        }
                        EmitStatus();

                        string? dynPrompt = _contextPromptProvider?.Invoke();
                        string wText = await _whisper.TranscribePcm16kAsync(whisperPcm, whisperVariant, dynPrompt, ct);
                        string wCleaned = AliceCommandParser.StripWakeWords(wText, aliases);
                        if (!string.IsNullOrWhiteSpace(wCleaned))
                            finalCommand = wCleaned;
                    }
                }

                if (ct.IsCancellationRequested)
                    return;

                if (string.IsNullOrWhiteSpace(finalCommand))
                {
                    lock (_syncLock)
                    {
                        _lastCommand = "";
                        _lastReply = "Не расслышал команду";
                        _liveTranscript = "";
                    }
                    await _feedback.PlayCancelChimeAsync();
                    RestorePostCommandState(wakeOn);
                    if (needDuck) DuckingRequested?.Invoke(false);
                    return;
                }

                lock (_syncLock)
                {
                    _lastCommand = finalCommand;
                    _liveTranscript = finalCommand;
                    _statusText = $"Команда: «{finalCommand}»";
                }
                EmitStatus();

                var result = await _commandExecutor(finalCommand);
                if (ct.IsCancellationRequested)
                    return;

                lock (_syncLock)
                {
                    _lastReply = result.Reply;
                }

                if (useTts && !string.IsNullOrWhiteSpace(result.Reply))
                {
                    lock (_syncLock)
                    {
                        _state = VoiceAssistantState.Speaking;
                        _statusText = result.Reply;
                        _muteAudioUntilTick = long.MaxValue;
                    }
                    EmitStatus();

                    await _feedback.SpeakAsync(result.Reply, ttsVoice, ct);

                    lock (_syncLock)
                    {
                        _muteAudioUntilTick = Environment.TickCount64 + 320;
                        _vosk.Reset();
                    }
                }
                else
                {
                    await _feedback.PlayConfirmChimeAsync();
                    lock (_syncLock)
                    {
                        _muteAudioUntilTick = Environment.TickCount64 + 220;
                    }
                }

                RestorePostCommandState(wakeOn);
                if (needDuck)
                    DuckingRequested?.Invoke(false);
            }
            catch (OperationCanceledException)
            {
                if (needDuck) DuckingRequested?.Invoke(false);
            }
            catch
            {
                RestorePostCommandState(wakeOn);
                if (needDuck) DuckingRequested?.Invoke(false);
            }
        }, ct);
    }

    private void RestorePostCommandState(bool wakeOn)
    {
        bool scheduleIdle = false;
        lock (_syncLock)
        {
            _lastVoiceActivityTick = Environment.TickCount64;
            if (_models.CurrentProgress.Busy || _state == VoiceAssistantState.Downloading)
                return;

            _liveTranscript = "";
            _audioLevel = 0;
            _speechDetected = false;
            _voskCandidateText = "";

            if (wakeOn && _vosk.IsReady && _mic.IsCapturing)
            {
                _vosk.Reset();
                _vad.ResetState();
                _state = VoiceAssistantState.WakeListening;
                _statusText = BuildWakeListeningLabelLocked();
            }
            else if (!_models.IsVoskReady() && !_models.IsWhisperReady(_whisperModel))
            {
                _state = VoiceAssistantState.NotInstalled;
                _statusText = "Установите офлайн-модель (Vosk / Whisper)";
            }
            else
            {
                _state = VoiceAssistantState.Idle;
                _statusText = BuildIdleLabelLocked();
                scheduleIdle = true;
            }
        }
        EmitStatus();
        if (scheduleIdle)
            ScheduleIdleUnload();
    }

    public void ReleaseIdleMemory()
    {
        if (_disposed) return;
        bool wakeOn;
        lock (_syncLock)
        {
            if (_state is VoiceAssistantState.CommandListening or VoiceAssistantState.ProcessingWhisper or VoiceAssistantState.Speaking or VoiceAssistantState.Downloading)
                return;
            wakeOn = _wakeWordEnabled;
        }

        try { _whisper.Stop(); } catch { }
        try { _feedback.UnloadNeuralModels(); } catch { }
        if (!wakeOn && !_mic.IsCapturing)
        {
            try { _vad.Unload(); } catch { }
            try { _vosk.Unload(); } catch { }
        }
    }

    private void ScheduleIdleUnload()
    {
        if (_disposed) return;
        try { _idleUnloadTimer.Change(IdleUnloadMs, Timeout.Infinite); } catch { }
    }

    private void OnIdleUnloadTimer(object? _)
    {
        if (_disposed) return;
        bool wakeOn;
        lock (_syncLock)
        {
            if (_state != VoiceAssistantState.Idle || _mic.IsCapturing)
                return;
            if (Environment.TickCount64 - _lastVoiceActivityTick < IdleUnloadMs - 500)
                return;
            wakeOn = _wakeWordEnabled;
        }

        if (!wakeOn)
        {
            try { _vad.Unload(); } catch { }
            try { _vosk.Unload(); } catch { }
            WindowBlurHelper.TrimWorkingSet();
        }
    }

    private static float ComputeRms16k(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        if (samples == 0) return 0f;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            double v = s / 32768.0;
            sum += v * v;
        }
        return (float)Math.Sqrt(sum / samples);
    }

    private void OnModelDownloadProgress(VoiceDownloadProgress p)
    {
        lock (_syncLock)
        {
            if (p.Busy)
            {
                _state = VoiceAssistantState.Downloading;
                _statusText = p.Stage;
            }
        }
        EmitStatus();
    }

    private string BuildWakeListeningLabelLocked()
    {
        string firstWake = _wakeWordsCsv.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Омни";
        return $"Жду «{firstWake}» или Alt+V";
    }

    private string BuildIdleLabelLocked()
    {
        bool v = _models.IsVoskReady();
        bool w = _models.IsWhisperReady(_whisperModel);
        bool tts = _models.IsNeuralTtsReady();

        if (v && w && _whisperEnabled) return tts ? "Готов (Turbo v3 + Нейро-голос · Alt+V)" : "Готов (Vosk + Whisper Turbo v3 · Alt+V)";
        if (v) return tts ? "Готов (Vosk RU + Нейро-голос · Alt+V)" : "Готов (Vosk RU · Alt+V)";
        if (w) return tts ? "Готов (Whisper Turbo v3 + Нейро · Alt+V)" : "Готов (Whisper Turbo v3 · Alt+V)";
        return tts ? "Нейро-голос готов · установите Vosk / Whisper Turbo" : "Установите модели распознавания";
    }

    private VoiceAssistantStatus BuildStatusLocked()
    {
        bool v = _models.IsVoskReady();
        bool w = _models.IsWhisperReady(_whisperModel);
        bool tts = _models.IsNeuralTtsReady();
        bool vad = _models.IsSileroVadReady() || _models.IsWhisperVadReady();
        var dl = _models.CurrentProgress;

        string baseEngine = (v && w && _whisperEnabled)
            ? (vad ? "VAD + Whisper Turbo" : "Vosk + Whisper Turbo")
            : (w ? (vad ? "VAD + Whisper Turbo" : "Whisper Turbo v3") : (v ? "Vosk RU" : ""));
        string engineLabel = string.IsNullOrEmpty(baseEngine)
            ? (tts ? "Нейро-голос (нет STT)" : "Модели не установлены")
            : (tts ? $"{baseEngine} + Нейро" : baseEngine);

        string stateKey = _state switch
        {
            VoiceAssistantState.NotInstalled => "notInstalled",
            VoiceAssistantState.Downloading => "downloading",
            VoiceAssistantState.WakeListening => "wakeListening",
            VoiceAssistantState.CommandListening => "commandListening",
            VoiceAssistantState.ProcessingWhisper => "processingWhisper",
            VoiceAssistantState.Speaking => "speaking",
            _ => "idle"
        };

        return new VoiceAssistantStatus(
            _state,
            stateKey,
            _statusText,
            _liveTranscript,
            _lastCommand,
            _lastReply,
            _audioLevel,
            v,
            w,
            _whisper.IsRunning,
            tts,
            vad,
            dl.Busy,
            dl.Percent,
            dl.Stage,
            dl.DownloadedMb,
            dl.TotalMb,
            _models.GetTotalDiskUsageMb(),
            _wakeWordEnabled && v,
            _wakeWordsCsv,
            engineLabel);
    }

    private void EmitStatus()
    {
        VoiceAssistantStatus snap;
        lock (_syncLock) snap = BuildStatusLocked();
        StatusChanged?.Invoke(snap);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _idleUnloadTimer.Dispose(); } catch { }
        try { _engineStateLock.Dispose(); } catch { }
        try { _models.ProgressChanged -= OnModelDownloadProgress; } catch { }
        try { _mic.AudioChunkCaptured -= OnAudioChunkCaptured; } catch { }
        try { _processingCts?.Cancel(); _processingCts?.Dispose(); } catch { }
        try { _mic.Dispose(); } catch { }
        try { _whisper.Dispose(); } catch { }
        try { _vad.Dispose(); } catch { }
        try { _vosk.Dispose(); } catch { }
    }
}
