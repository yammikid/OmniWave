using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland.Services;

public sealed record UpdateCheckResult(
    bool CheckedOk,
    bool IsUpdateAvailable,
    string LocalSha,
    string RemoteSha,
    string CommitShortSha,
    string CommitMessage,
    string CommitDateText,
    long RemoteSize,
    string StatusMessage);

public static class AppUpdateService
{
    private const string RepoOwner = "yammikid";
    private const string RepoName = "OmniWave";
    private const string Branch = "main";
    private const string ExePathInRepo = "DynamicIsland.exe";
    private const string YtDlpPathInRepo = "yt-dlp.exe";

    private static readonly string TreeApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/git/trees/{Branch}";
    private static readonly string CommitsApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits?path={ExePathInRepo}&sha={Branch}&per_page=1";
    private static readonly string RawExePrimaryUrl = $"https://github.com/{RepoOwner}/{RepoName}/raw/{Branch}/{ExePathInRepo}";
    private static readonly string RawExeMirrorUrl = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{Branch}/{ExePathInRepo}";
    private static readonly string RawYtDlpPrimaryUrl = $"https://github.com/{RepoOwner}/{RepoName}/raw/{Branch}/{YtDlpPathInRepo}";
    private static readonly string RawYtDlpMirrorUrl = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{Branch}/{YtDlpPathInRepo}";

    private static readonly SemaphoreSlim UpdateGate = new(1, 1);

    public static string GetCurrentExecutablePath()
    {
        var procPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(procPath) &&
            procPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(procPath))
        {
            return procPath;
        }

        return Path.Combine(AppContext.BaseDirectory, "DynamicIsland.exe");
    }

    public static void CleanupOldUpdateArtifacts()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AppContext.BaseDirectory
        };
        var exeDir = Path.GetDirectoryName(GetCurrentExecutablePath());
        if (!string.IsNullOrWhiteSpace(exeDir))
            dirs.Add(exeDir);

        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (var name in new[] { "DynamicIsland.exe.old", "DynamicIsland.exe.update", "yt-dlp.exe.update" })
            {
                try
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { }
            }
        }

        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "omniwave_updater_*.cmd"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromMinutes(1))
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    public static string ComputeGitBlobSha1(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return "";

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var header = Encoding.ASCII.GetBytes($"blob {fs.Length.ToString(CultureInfo.InvariantCulture)}\0");
        hasher.AppendData(header);

        var buffer = new byte[81920];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            hasher.AppendData(buffer, 0, read);

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave-Updater/1.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var exePath = GetCurrentExecutablePath();
        var localSha = ComputeGitBlobSha1(exePath);
        var localShort = localSha.Length >= 7 ? localSha[..7] : "unknown";

        try
        {
            using var http = CreateClient(TimeSpan.FromSeconds(15));
            using var treeResp = await http.GetAsync($"{TreeApiUrl}?_ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", ct);
            if (!treeResp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    CheckedOk: false,
                    IsUpdateAvailable: false,
                    LocalSha: localSha,
                    RemoteSha: "",
                    CommitShortSha: "",
                    CommitMessage: "",
                    CommitDateText: "",
                    RemoteSize: 0,
                    StatusMessage: $"Не удалось проверить GitHub (код {(int)treeResp.StatusCode}).");
            }

            string treeJson = await treeResp.Content.ReadAsStringAsync(ct);
            using var treeDoc = JsonDocument.Parse(treeJson);

            string remoteSha = "";
            long remoteSize = 0;
            if (treeDoc.RootElement.TryGetProperty("tree", out var treeArr) && treeArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in treeArr.EnumerateArray())
                {
                    if (entry.TryGetProperty("path", out var pEl) &&
                        string.Equals(pEl.GetString(), ExePathInRepo, StringComparison.OrdinalIgnoreCase))
                    {
                        remoteSha = entry.TryGetProperty("sha", out var sEl) ? (sEl.GetString() ?? "") : "";
                        remoteSize = entry.TryGetProperty("size", out var szEl) && szEl.ValueKind == JsonValueKind.Number
                            ? szEl.GetInt64()
                            : 0;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(remoteSha))
            {
                return new UpdateCheckResult(
                    CheckedOk: false,
                    IsUpdateAvailable: false,
                    LocalSha: localSha,
                    RemoteSha: "",
                    CommitShortSha: "",
                    CommitMessage: "",
                    CommitDateText: "",
                    RemoteSize: 0,
                    StatusMessage: "Файл DynamicIsland.exe не найден в ветке main на GitHub.");
            }

            string commitShort = remoteSha.Length >= 7 ? remoteSha[..7] : remoteSha;
            string commitMsg = "";
            string commitDateText = "";

            try
            {
                using var commitResp = await http.GetAsync($"{CommitsApiUrl}&_ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", ct);
                if (commitResp.IsSuccessStatusCode)
                {
                    string cJson = await commitResp.Content.ReadAsStringAsync(ct);
                    using var cDoc = JsonDocument.Parse(cJson);
                    if (cDoc.RootElement.ValueKind == JsonValueKind.Array && cDoc.RootElement.GetArrayLength() > 0)
                    {
                        var first = cDoc.RootElement[0];
                        if (first.TryGetProperty("sha", out var cShaEl))
                        {
                            var full = cShaEl.GetString() ?? "";
                            if (full.Length >= 7) commitShort = full[..7];
                        }
                        if (first.TryGetProperty("commit", out var commitObj))
                        {
                            if (commitObj.TryGetProperty("message", out var msgEl))
                            {
                                var raw = msgEl.GetString() ?? "";
                                commitMsg = raw.Split('\n', '\r')[0].Trim();
                            }
                            if (commitObj.TryGetProperty("committer", out var committer) &&
                                committer.TryGetProperty("date", out var dEl) &&
                                DateTimeOffset.TryParse(dEl.GetString(), out var dto))
                            {
                                commitDateText = dto.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                            }
                        }
                    }
                }
            }
            catch { }

            bool available = !string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase);
            string sizeText = remoteSize > 0 ? $"{remoteSize / (1024.0 * 1024.0):F1} МБ" : "";
            string msg = available
                ? $"Доступна новая версия ({commitShort}{(string.IsNullOrEmpty(commitDateText) ? "" : $", {commitDateText}")}{(string.IsNullOrEmpty(sizeText) ? "" : $", {sizeText}")})."
                : $"Установлена актуальная версия ({localShort}).";

            return new UpdateCheckResult(
                CheckedOk: true,
                IsUpdateAvailable: available,
                LocalSha: localSha,
                RemoteSha: remoteSha,
                CommitShortSha: commitShort,
                CommitMessage: commitMsg,
                CommitDateText: commitDateText,
                RemoteSize: remoteSize,
                StatusMessage: msg);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, false, localSha, "", "", "", "", 0, "Проверка обновлений отменена.");
        }
        catch
        {
            return new UpdateCheckResult(false, false, localSha, "", "", "", "", 0, "Нет соединения с GitHub для проверки обновлений.");
        }
    }

    public static async Task<(bool Ok, string ErrorMessage)> DownloadAndApplyUpdateAsync(
        Action<int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!await UpdateGate.WaitAsync(0, ct))
            return (false, "Обновление уже выполняется.");

        try
        {
            var exePath = GetCurrentExecutablePath();
            var exeDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(exeDir) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return (false, "Не удалось определить путь к исполняемому файлу.");

            Directory.CreateDirectory(exeDir);
            var updatePath = exePath + ".update";
            var oldPath = exePath + ".old";

            onProgress?.Invoke(0, "Подключение к GitHub...");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool downloaded = await TryDownloadBinaryWithProgressAsync(
                new[] { $"{RawExePrimaryUrl}?_ts={ts}", $"{RawExeMirrorUrl}?_ts={ts}" },
                updatePath,
                onProgress,
                ct);

            if (!downloaded || !IsValidPeExecutable(updatePath, minBytes: 5_000_000))
            {
                try { if (File.Exists(updatePath)) File.Delete(updatePath); } catch { }
                return (false, "Не удалось скачать корректный файл DynamicIsland.exe с GitHub.");
            }

            // Гарантируем наличие yt-dlp.exe рядом с программой
            var ytDlpPath = Path.Combine(exeDir, YtDlpPathInRepo);
            if (!File.Exists(ytDlpPath))
            {
                onProgress?.Invoke(98, "Загрузка yt-dlp.exe...");
                var ytTmp = ytDlpPath + ".update";
                bool ytOk = await TryDownloadBinaryWithProgressAsync(
                    new[] { $"{RawYtDlpPrimaryUrl}?_ts={ts}", $"{RawYtDlpMirrorUrl}?_ts={ts}" },
                    ytTmp,
                    null,
                    ct);
                if (ytOk && IsValidPeExecutable(ytTmp, minBytes: 1_000_000))
                {
                    try { File.Move(ytTmp, ytDlpPath, overwrite: true); } catch { }
                }
                else
                {
                    try { if (File.Exists(ytTmp)) File.Delete(ytTmp); } catch { }
                }
            }

            onProgress?.Invoke(99, "Установка обновления и перезапуск...");

            bool swappedLive = false;
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                if (File.Exists(exePath))
                    File.Move(exePath, oldPath, overwrite: true);
                File.Move(updatePath, exePath, overwrite: true);
                swappedLive = true;
            }
            catch
            {
                swappedLive = false;
            }

            var cmdPath = Path.Combine(Path.GetTempPath(), $"omniwave_updater_{Environment.ProcessId}.cmd");
            var script = BuildRestartScript(exePath, oldPath, updatePath, swappedLive);
            await File.WriteAllTextAsync(cmdPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{cmdPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            });

            onProgress?.Invoke(100, "Перезапуск OmniWave...");

            if (Application.Current != null)
                await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());

            return (true, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "Обновление отменено.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка обновления: {ex.Message}");
        }
        finally
        {
            UpdateGate.Release();
        }
    }

    private static string BuildRestartScript(string exePath, string oldPath, string updatePath, bool swappedLive)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        if (!swappedLive)
        {
            sb.AppendLine("for /l %%i in (1,1,15) do (");
            sb.AppendLine($"  move /y \"{updatePath}\" \"{exePath}\" >nul 2>nul && goto :run");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine(")");
            sb.AppendLine(":run");
        }
        sb.AppendLine("for /l %%i in (1,1,6) do (");
        sb.AppendLine($"  if exist \"{oldPath}\" del /f /q \"{oldPath}\" >nul 2>nul");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine(")");
        sb.AppendLine($"start \"\" \"{exePath}\"");
        sb.AppendLine("del \"%~f0\" >nul 2>nul");
        return sb.ToString();
    }

    private static async Task<bool> TryDownloadBinaryWithProgressAsync(
        string[] urls,
        string destPath,
        Action<int, string>? onProgress,
        CancellationToken ct)
    {
        using var http = CreateClient(TimeSpan.FromMinutes(6));
        foreach (var url in urls)
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;

                long total = resp.Content.Headers.ContentLength ?? 0;
                long received = 0;
                long lastUiTick = 0;

                await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct))
                await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await remoteStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                        received += read;

                        if (onProgress != null)
                        {
                            long now = Environment.TickCount64;
                            if (now - lastUiTick >= 140 || (total > 0 && received >= total))
                            {
                                lastUiTick = now;
                                if (total > 0)
                                {
                                    int pct = Math.Clamp((int)Math.Round(received * 97.0 / total), 1, 97);
                                    onProgress(pct, $"Скачивание: {received / (1024.0 * 1024.0):F1} / {total / (1024.0 * 1024.0):F1} МБ ({pct}%)");
                                }
                                else
                                {
                                    onProgress(40, $"Скачивание: {received / (1024.0 * 1024.0):F1} МБ...");
                                }
                            }
                        }
                    }
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }
        }
        return false;
    }

    private static bool IsValidPeExecutable(string path, long minBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < minBytes) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> mz = stackalloc byte[2];
            return fs.Read(mz) == 2 && mz[0] == (byte)'M' && mz[1] == (byte)'Z';
        }
        catch
        {
            return false;
        }
    }

    public static void ShowStartupUpdateDialog(UpdateCheckResult info)
    {
        var win = new Window
        {
            Title = "Доступно обновление OmniWave",
            Width = 408,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            Background = new SolidColorBrush(Color.FromRgb(17, 18, 22)),
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = false
        };
        win.SourceInitialized += (_, _) => WindowBlurHelper.ApplyChromaWindowStyle(win, 14);
        win.SizeChanged += (_, _) => WindowBlurHelper.UpdateWin10RoundedRegion(win, 14);

        var rootBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(35, 38, 47)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(WindowBlurHelper.IsWindows11OrLater ? 8 : 14),
            Padding = new Thickness(20, 18, 20, 18)
        };

        var stack = new StackPanel();
        var hdrGrid = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12) };
        hdrGrid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                try { win.DragMove(); } catch { }
            }
        };
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hdrLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        hdrLeft.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(38, 255, 204, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, 255, 204, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = "ОБНОВЛЕНИЕ",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0))
            }
        });
        hdrLeft.Children.Add(new TextBlock
        {
            Text = "Найдена новая версия",
            FontSize = 15.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        hdrGrid.Children.Add(hdrLeft);

        var btnClose = CreateDialogButton("✕", primary: false);
        btnClose.Width = 28;
        btnClose.Height = 28;
        btnClose.Padding = new Thickness(0);
        Grid.SetColumn(btnClose, 1);
        btnClose.Click += (_, _) => win.Close();
        hdrGrid.Children.Add(btnClose);
        stack.Children.Add(hdrGrid);

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.CommitShortSha)) metaParts.Add($"сборка {info.CommitShortSha}");
        if (!string.IsNullOrWhiteSpace(info.CommitDateText)) metaParts.Add(info.CommitDateText);
        if (info.RemoteSize > 0) metaParts.Add($"{info.RemoteSize / (1024.0 * 1024.0):F1} МБ");

        var txtDesc = new TextBlock
        {
            Text = metaParts.Count > 0
                ? $"На GitHub опубликована новая версия OmniWave ({string.Join(" • ", metaParts)})."
                : "На GitHub опубликована новая версия OmniWave.",
            Foreground = new SolidColorBrush(Color.FromRgb(235, 236, 244)),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(txtDesc);

        if (!string.IsNullOrWhiteSpace(info.CommitMessage))
        {
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock
                {
                    Text = info.CommitMessage,
                    Foreground = new SolidColorBrush(Color.FromRgb(185, 188, 205)),
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        var txtStatus = new TextBlock
        {
            Text = "Обновить и перезапустить приложение сейчас?",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 152, 175)),
            FontSize = 11.5,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(txtStatus);

        var progressBar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0)),
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0)
        };
        stack.Children.Add(progressBar);

        var btnRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });

        var btnLater = CreateDialogButton("Позже", primary: false);
        btnLater.Click += (_, _) => win.Close();
        Grid.SetColumn(btnLater, 0);

        var btnUpdate = CreateDialogButton("Обновить сейчас", primary: true);
        Grid.SetColumn(btnUpdate, 2);

        btnUpdate.Click += async (_, _) =>
        {
            btnUpdate.IsEnabled = false;
            btnLater.IsEnabled = false;
            btnClose.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;

            var (ok, err) = await DownloadAndApplyUpdateAsync((pct, msg) =>
            {
                win.Dispatcher.BeginInvoke(() =>
                {
                    progressBar.Value = pct;
                    txtStatus.Text = msg;
                });
            });

            if (!ok)
            {
                btnUpdate.IsEnabled = true;
                btnLater.IsEnabled = true;
                btnClose.IsEnabled = true;
                txtStatus.Text = err;
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 110, 110));
            }
        };

        btnRow.Children.Add(btnLater);
        btnRow.Children.Add(btnUpdate);
        stack.Children.Add(btnRow);

        rootBorder.Child = stack;
        win.Content = rootBorder;
        win.Show();
        win.Activate();
    }

    private static Button CreateDialogButton(string text, bool primary)
    {
        var bg = primary
            ? new SolidColorBrush(Color.FromRgb(255, 204, 0))
            : new SolidColorBrush(Color.FromArgb(32, 255, 255, 255));
        var hoverBg = primary
            ? new SolidColorBrush(Color.FromRgb(255, 217, 64))
            : new SolidColorBrush(Color.FromArgb(54, 255, 255, 255));
        var fg = primary
            ? new SolidColorBrush(Color.FromRgb(17, 17, 22))
            : Brushes.White;

        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(presenter);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = borderFactory };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Bd"));
        tpl.Triggers.Add(hoverTrigger);

        return new Button
        {
            Content = text,
            Background = bg,
            Foreground = fg,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 9, 14, 9),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = tpl
        };
    }
}
