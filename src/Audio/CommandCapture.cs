using NAudio.Wave;
using ClaudioAi.Diagnostics;

namespace ClaudioAi.Audio;

/// <summary>
/// Graba la orden que va después de la palabra de activación, con fin automático:
/// empieza a escribir en cuanto detecta voz (con ~200 ms de pre-roll para no
/// cortar la primera sílaba) y para tras <see cref="ClaudioConfig.SilenceMs"/> de
/// silencio o al llegar a <see cref="ClaudioConfig.MaxCommandSeconds"/>.
/// Devuelve la ruta del WAV (16 kHz mono 16-bit) o <c>null</c> si nadie habló.
/// </summary>
public sealed class CommandCapture
{
    readonly ClaudioConfig _cfg;
    const int PreRollBuffers = 4;   // 4 × 50 ms ≈ 200 ms

    public CommandCapture(ClaudioConfig cfg) => _cfg = cfg;

    public async Task<string?> CaptureAsync(CancellationToken ct = default)
    {
        if (WaveInEvent.DeviceCount == 0)
        {
            Log.Error("No hay micrófono disponible para capturar la orden.");
            return null;
        }

        var wav = Path.Combine(Path.GetTempPath(), $"claudio-cmd-{Guid.NewGuid():N}.wav");
        var format = new WaveFormat(16000, 16, 1);

        using var waveIn = new WaveInEvent { WaveFormat = format, BufferMilliseconds = 50 };
        var writer = new WaveFileWriter(wav, format);

        var silenceNeeded = TimeSpan.FromMilliseconds(_cfg.SilenceMs);
        var maxDuration = TimeSpan.FromSeconds(_cfg.MaxCommandSeconds);
        var noSpeechTimeout = TimeSpan.FromSeconds(_cfg.NoSpeechTimeoutSeconds);

        var started = false;
        var sawSpeech = false;
        var beganAt = DateTime.UtcNow;
        var lastVoice = beganAt;
        var preRoll = new Queue<byte[]>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;

        waveIn.DataAvailable += (_, e) =>
        {
            try
            {
                var now = DateTime.UtcNow;
                var voice = Rms(e.Buffer, e.BytesRecorded) >= _cfg.SilenceThreshold;

                if (!started)
                {
                    preRoll.Enqueue(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
                    while (preRoll.Count > PreRollBuffers) preRoll.Dequeue();

                    if (voice)
                    {
                        started = true;
                        sawSpeech = true;
                        lastVoice = now;
                        while (preRoll.Count > 0)
                        {
                            var b = preRoll.Dequeue();
                            writer.Write(b, 0, b.Length);
                        }
                    }
                    else if (now - beganAt > noSpeechTimeout)
                    {
                        done.TrySetResult(false);   // nadie habló
                    }
                    return;
                }

                writer.Write(e.Buffer, 0, e.BytesRecorded);
                if (voice) lastVoice = now;

                if (now - lastVoice >= silenceNeeded || now - beganAt >= maxDuration)
                    done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                failure = ex;
                done.TrySetResult(false);
            }
        };
        waveIn.RecordingStopped += (_, e) => failure ??= e.Exception;

        await using var reg = ct.Register(() => done.TrySetResult(false));

        bool ok;
        waveIn.StartRecording();
        try
        {
            ok = await done.Task;
        }
        finally
        {
            try { waveIn.StopRecording(); } catch { /* da igual */ }
            writer.Dispose();
        }

        if (failure is not null)
        {
            Log.Error("Fallo al capturar la orden", failure);
            TryDelete(wav);
            return null;
        }

        if (!ok || !sawSpeech)
        {
            TryDelete(wav);
            return null;
        }

        return wav;
    }

    /// <summary>Energía RMS del buffer PCM 16-bit, normalizada a 0..1.</summary>
    static double Rms(byte[] buffer, int count)
    {
        if (count < 2) return 0;
        double sum = 0;
        var samples = count / 2;
        for (var i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(buffer[i] | (buffer[i + 1] << 8));
            var v = s / 32768.0;
            sum += v * v;
        }
        return Math.Sqrt(sum / samples);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* no pasa nada */ }
    }
}
