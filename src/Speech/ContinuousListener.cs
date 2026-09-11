using NAudio.Wave;
using ClaudioAi.Diagnostics;

namespace ClaudioAi.Speech;

/// <summary>
/// Escucha el micrófono en continuo. Un VAD por energía trocea la voz en frases
/// (silencio prolongado = fin de frase) y cada frase se transcribe con Whisper.
/// Emite <see cref="UtteranceReady"/> con el texto de cada frase.
///
/// Se usa porque Windows 11 ya no incluye el reconocedor de <c>System.Speech</c>:
/// la palabra de activación "Claudio" se detecta sobre el texto que devuelve
/// Whisper, sin dependencias extra.
/// </summary>
public sealed class ContinuousListener : IDisposable
{
    readonly ClaudioConfig _cfg;
    readonly SpeechToText _stt;
    readonly WaveFormat _format = new(16000, 16, 1);
    readonly WaveInEvent? _waveIn;

    readonly List<byte[]> _utterance = [];
    readonly Queue<byte[]> _preRoll = new();
    const int PreRollBuffers = 6;              // 6 × 50 ms ≈ 300 ms

    bool _inSpeech;
    int _utteranceBytes;
    DateTime _lastVoice;
    DateTime _speechStart;

    readonly SemaphoreSlim _transcribeLock = new(1, 1);
    CancellationTokenSource? _cts;
    volatile bool _running;
    volatile bool _disposed;

    /// <summary>Texto de una frase reconocida (nunca vacío). Se dispara en un hilo de fondo.</summary>
    public event Action<string>? UtteranceReady;

    /// <summary>Mientras es <c>true</c> se descarta todo el audio (p. ej. cuando Claudio habla).</summary>
    public volatile bool Muted;

    public bool Available => _waveIn is not null;

    public ContinuousListener(ClaudioConfig cfg, SpeechToText stt)
    {
        _cfg = cfg;
        _stt = stt;
        try
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                Log.Error("No hay micrófono: Claudio no podrá escuchar.");
                return;
            }
            _waveIn = new WaveInEvent { WaveFormat = _format, BufferMilliseconds = 50 };
            _waveIn.DataAvailable += OnData;
        }
        catch (Exception ex)
        {
            Log.Error("No pude abrir el micrófono", ex);
            _waveIn = null;
        }
    }

    public void Start()
    {
        if (_waveIn is null || _disposed || _running) return;
        _cts = new CancellationTokenSource();
        ResetUtterance();
        _running = true;
        try
        {
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            _running = false;
            Log.Error("No pude iniciar la escucha continua", ex);
        }
    }

    public void Stop()
    {
        if (_waveIn is null || !_running) return;
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _waveIn.StopRecording(); } catch { }
        ResetUtterance();
    }

    void ResetUtterance()
    {
        _inSpeech = false;
        _utteranceBytes = 0;
        _utterance.Clear();
        _preRoll.Clear();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (!_running || Muted) return;

        var now = DateTime.UtcNow;
        var chunk = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        var voice = Rms(e.Buffer, e.BytesRecorded) >= _cfg.SilenceThreshold;

        if (!_inSpeech)
        {
            _preRoll.Enqueue(chunk);
            while (_preRoll.Count > PreRollBuffers) _preRoll.Dequeue();
            if (!voice) return;

            _inSpeech = true;
            _speechStart = now;
            _lastVoice = now;
            while (_preRoll.Count > 0)
            {
                var b = _preRoll.Dequeue();
                _utterance.Add(b);
                _utteranceBytes += b.Length;
            }
            return;
        }

        _utterance.Add(chunk);
        _utteranceBytes += chunk.Length;
        if (voice) _lastVoice = now;

        var endedBySilence = now - _lastVoice >= TimeSpan.FromMilliseconds(_cfg.SilenceMs);
        var tooLong = now - _speechStart >= TimeSpan.FromSeconds(_cfg.MaxCommandSeconds);
        if (!endedBySilence && !tooLong) return;

        var pcm = new byte[_utteranceBytes];
        var offset = 0;
        foreach (var b in _utterance)
        {
            Buffer.BlockCopy(b, 0, pcm, offset, b.Length);
            offset += b.Length;
        }
        ResetUtterance();

        _ = Task.Run(() => TranscribeAsync(pcm));
    }

    async Task TranscribeAsync(byte[] pcm)
    {
        if (_disposed) return;
        if (pcm.Length < _format.AverageBytesPerSecond / 3) return;   // < ~330 ms: ruido
        if (!await _transcribeLock.WaitAsync(0)) return;              // otra transcripción en curso: descartamos

        var wav = Path.Combine(Path.GetTempPath(), $"claudio-hear-{Guid.NewGuid():N}.wav");
        try
        {
            await using (var writer = new WaveFileWriter(wav, _format))
                writer.Write(pcm, 0, pcm.Length);

            var ct = _cts?.Token ?? CancellationToken.None;
            var text = (await _stt.TranscribeAsync(wav, ct)).Trim();

            if (text.Length > 0 && _running && !Muted)
            {
                Log.Info($"Oído: {text}");
                UtteranceReady?.Invoke(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Fallo al transcribir una frase", ex);
        }
        finally
        {
            try { File.Delete(wav); } catch { /* da igual */ }
            _transcribeLock.Release();
        }
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnData;
            _waveIn.Dispose();
        }
        _cts?.Dispose();
        _transcribeLock.Dispose();
    }
}
