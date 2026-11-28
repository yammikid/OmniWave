namespace DynamicIsland.Services;

/// <summary>
/// Общая 16 кГц DSP-предобработка речи перед локальными ASR-движками (GigaAM v3 и Whisper Turbo):
/// удаление DC-смещения, ФВЧ ~85 Гц, мягкое адаптивное подавление стационарного шума, AGC и tanh-лимитер.
/// </summary>
public static class VoiceAudioDsp
{
    public static float ComputeRms16k(byte[] pcm)
    {
        if (pcm == null || pcm.Length < 2)
            return 0f;

        int samples = pcm.Length / 2;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            double v = s / 32768.0;
            sum += v * v;
        }
        return (float)Math.Sqrt(sum / samples);
    }

    public static float[] PreprocessPcm16kToFloats(byte[] pcm)
    {
        if (pcm == null || pcm.Length < 2)
            return Array.Empty<float>();

        int sampleCount = pcm.Length / 2;
        var floats = new float[sampleCount];
        double mean = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            float v = s / 32768f;
            floats[i] = v;
            mean += v;
        }

        if (sampleCount < 160)
            return floats;

        float dc = (float)(mean / sampleCount);

        // Высокочастотный фильтр 1-го порядка ~85 Гц (убирает гул вентиляторов/стола и DC-смещение)
        const float hpfAlpha = 0.9677f;
        float prevIn = 0f;
        float prevOut = 0f;
        for (int i = 0; i < sampleCount; i++)
        {
            float x = floats[i] - dc;
            float y = hpfAlpha * (prevOut + x - prevIn);
            prevIn = x;
            prevOut = y;
            floats[i] = y;
        }

        // Мягкое адаптивное ослабление стационарного фонового шума по 20 мс окнам
        const int winSamples = 320;
        int winCount = sampleCount / winSamples;
        if (winCount >= 12)
        {
            var winRms = new float[winCount];
            var sortedRms = new float[winCount];
            for (int w = 0; w < winCount; w++)
            {
                int start = w * winSamples;
                double wSq = 0;
                for (int i = 0; i < winSamples; i++)
                {
                    float v = floats[start + i];
                    wSq += (double)v * v;
                }
                float r = (float)Math.Sqrt(wSq / winSamples);
                winRms[w] = r;
                sortedRms[w] = r;
            }

            Array.Sort(sortedRms);
            float noiseFloor = sortedRms[Math.Min(winCount - 1, (int)(winCount * 0.15))];
            float speechPeak = sortedRms[Math.Max(0, (int)(winCount * 0.85))];

            if (noiseFloor > 0.0005f && speechPeak > noiseFloor * 2.5f)
            {
                float gateCutoff = noiseFloor * 1.35f;
                float smoothGain = 1.0f;
                for (int i = 0; i < sampleCount; i++)
                {
                    int w = Math.Min(winCount - 1, i / winSamples);
                    float target = winRms[w] <= gateCutoff ? 0.35f : 1.0f;
                    smoothGain = smoothGain * 0.985f + target * 0.015f;
                    floats[i] *= smoothGain;
                }
            }
        }

        // Мягкая автоматическая регулировка усиления (AGC) по активной речи
        double activeSqSum = 0;
        int activeCount = 0;
        double allSqSum = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            float y = floats[i];
            double sq = (double)y * y;
            allSqSum += sq;
            if (MathF.Abs(y) > 0.008f)
            {
                activeSqSum += sq;
                activeCount++;
            }
        }

        float rms = activeCount >= 80
            ? (float)Math.Sqrt(activeSqSum / activeCount)
            : (float)Math.Sqrt(allSqSum / sampleCount);

        float gain = (rms >= 0.0025f && rms < 0.14f)
            ? Math.Clamp(0.12f / rms, 1.0f, 6.0f)
            : 1.0f;

        for (int i = 0; i < sampleCount; i++)
        {
            float v = floats[i] * gain;
            float abs = MathF.Abs(v);
            if (abs > 0.92f)
                v = MathF.Sign(v) * (0.92f + 0.08f * MathF.Tanh((abs - 0.92f) / 0.08f));
            floats[i] = Math.Clamp(v, -1.0f, 1.0f);
        }

        return floats;
    }

    public static byte[] PreprocessPcm16k(byte[] pcm)
    {
        if (pcm == null || pcm.Length < 320)
            return pcm ?? Array.Empty<byte>();

        float[] floats = PreprocessPcm16kToFloats(pcm);
        byte[] outBytes = new byte[floats.Length * 2];
        for (int i = 0; i < floats.Length; i++)
        {
            short s = (short)Math.Clamp((int)MathF.Round(floats[i] * 32767f), -32768, 32767);
            outBytes[i * 2] = (byte)(s & 0xFF);
            outBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return outBytes;
    }
}
