using NAudio.Wave;

namespace ClaudioAi.Audio;

/// <summary>
/// Graba el micrófono por defecto a un WAV de 16 kHz mono 16-bit, que es justo
/// lo que espera Whisper. Usa NAudio (WASAPI/WaveIn) para no depender de
/// utilidades externas en Windows.
/// </summary>
public sealed class AudioRecorder
{
    public async Task<string> RecordAsync(int seconds, CancellationToken ct = default)
    {
        if (WaveInEvent.DeviceCount == 0)
            throw new InvalidOperationException(
                "No se detectó ningún micrófono. Conecta uno y comprueba los permisos de micrófono en Windows.");

        var wav = Path.Combine(Path.GetTempPath(), $"claudio-{Guid.NewGuid():N}.wav");

        using var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50,
        };

        var writer = new WaveFileWriter(wav, waveIn.WaveFormat);
        var stopped = new TaskCompletionSource();
        Exception? failure = null;

        waveIn.DataAvailable += (_, e) =>
        {
            try { writer.Write(e.Buffer, 0, e.BytesRecorded); }
            catch (Exception ex) { failure ??= ex; }
        };
        waveIn.RecordingStopped += (_, e) =>
        {
            failure ??= e.Exception;
            stopped.TrySetResult();
        };

        try
        {
            waveIn.StartRecording();

            using var window = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, window.Token);
            try { await Task.Delay(Timeout.Infinite, linked.Token); }
            catch (OperationCanceledException) { /* fin de la ventana o Ctrl+C */ }

            waveIn.StopRecording();
            await stopped.Task;
        }
        finally
        {
            writer.Dispose();
        }

        if (failure is not null)
        {
            TryDelete(wav);
            throw new InvalidOperationException($"Fallo al grabar: {failure.Message}", failure);
        }

        if (ct.IsCancellationRequested)
        {
            TryDelete(wav);
            throw new OperationCanceledException(ct);
        }

        return wav;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* no pasa nada */ }
    }
}
