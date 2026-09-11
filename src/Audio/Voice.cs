using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Speech.Synthesis;
using NAudio.Wave;

namespace ClaudioAi.Audio;

/// <summary>
/// Texto a voz para Windows. Por defecto usa SAPI (<c>System.Speech</c>), que
/// siempre está disponible; si hay un <c>piper.exe</c> en el PATH (o en
/// <c>piperPath</c>) y un modelo <c>.onnx</c> configurado, se puede forzar Piper
/// para una voz de más calidad. Con Piper, <see cref="_speedFactor"/> permite
/// bajar el tono y la velocidad para una voz más grave y pausada, sin depender
/// de ninguna librería de pitch-shift: se reescribe la frecuencia de muestreo
/// declarada en el WAV (mismo truco que un "audio ralentizado"). Con
/// <c>ttsEngine=elevenlabs</c> (de pago, requiere API key) se usa una voz
/// neuronal en la nube, mucho más natural que Piper o SAPI.
/// Si nada funciona, Claudio sigue vivo y solo escribe la respuesta por consola.
/// </summary>
public sealed class Voice : IDisposable
{
    static readonly HttpClient ElevenLabsHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    string _engine;
    readonly string? _piperModel;
    readonly string _piperPath;
    readonly double _speedFactor;
    readonly string? _elevenLabsApiKey;
    readonly string? _elevenLabsVoiceId;
    readonly string _elevenLabsModel;
    readonly SpeechSynthesizer? _sapi;

    public Voice(ClaudioConfig cfg)
    {
        _piperModel = cfg.PiperModel;
        _piperPath = string.IsNullOrWhiteSpace(cfg.PiperPath) ? "piper" : cfg.PiperPath;
        _speedFactor = cfg.PiperSpeedFactor;
        _elevenLabsApiKey = cfg.ElevenLabsApiKey;
        _elevenLabsVoiceId = cfg.ElevenLabsVoiceId;
        _elevenLabsModel = cfg.ElevenLabsModel;
        // "auto" nunca elige ElevenLabs (es de pago): hay que pedirlo explícitamente.
        _engine = cfg.TtsEngine is "auto" or "" ? Detect() : cfg.TtsEngine;

        // Aceptamos el nombre antiguo de Linux por compatibilidad con appsettings viejos.
        if (_engine is "espeak-ng" or "spd-say")
            _engine = "sapi";

        if (_engine == "sapi")
        {
            try
            {
                _sapi = new SpeechSynthesizer { Rate = 1, Volume = 100 };
                var spanish = _sapi.GetInstalledVoices()
                    .Where(v => v.Enabled &&
                                v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals(
                                    "es", StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.VoiceInfo.Name)
                    .FirstOrDefault();
                if (spanish is not null)
                    _sapi.SelectVoice(spanish);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tts] SAPI no disponible: {ex.Message}");
                _sapi?.Dispose();
                _sapi = null;
                _engine = "none";
            }
        }
    }

    public string Engine => _engine;
    public bool Available => _engine is "sapi" or "piper" or "elevenlabs";

    string Detect() => Which(_piperPath) ? "piper" : "sapi";

    static bool Which(string bin)
    {
        // Una ruta explícita (con carpeta o extensión) se comprueba directamente;
        // un nombre suelto se busca en el PATH con "where".
        if (bin.Contains(Path.DirectorySeparatorChar) || bin.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(bin);

        try
        {
            using var p = Process.Start(new ProcessStartInfo("where", bin)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Console.WriteLine($"\n[36m🔊 Claudio:[0m {text}\n");

        try
        {
            switch (_engine)
            {
                case "sapi":
                    await SpeakSapiAsync(text, ct);
                    break;
                case "piper":
                    await SpeakPiperAsync(text, ct);
                    break;
                case "elevenlabs":
                    await SpeakElevenLabsAsync(text, ct);
                    break;
                // "none": ya se ha impreso arriba, no hay nada más que hacer.
            }
        }
        catch (OperationCanceledException) { /* corte pedido por el usuario */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tts] {ex.Message}");
        }
    }

    async Task SpeakSapiAsync(string text, CancellationToken ct)
    {
        if (_sapi is null) return;

        var done = new TaskCompletionSource();
        void OnCompleted(object? s, SpeakCompletedEventArgs e) => done.TrySetResult();

        _sapi.SpeakCompleted += OnCompleted;
        await using var reg = ct.Register(() =>
        {
            try { _sapi.SpeakAsyncCancelAll(); } catch { /* da igual */ }
        });

        try
        {
            _sapi.SpeakAsync(text);
            await done.Task;
        }
        finally
        {
            _sapi.SpeakCompleted -= OnCompleted;
        }
    }

    async Task SpeakPiperAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_piperModel) || !File.Exists(_piperModel))
        {
            Console.Error.WriteLine("[tts] piper necesita 'piperModel' en appsettings.json (ruta a un .onnx).");
            return;
        }

        var wav = Path.Combine(Path.GetTempPath(), $"claudio-tts-{Guid.NewGuid():N}.wav");

        var piper = new ProcessStartInfo(_piperPath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        piper.ArgumentList.Add("--model"); piper.ArgumentList.Add(_piperModel);
        piper.ArgumentList.Add("--output_file"); piper.ArgumentList.Add(wav);

        using (var p = Process.Start(piper)
            ?? throw new InvalidOperationException($"no pude lanzar '{_piperPath}'"))
        {
            await p.StandardInput.WriteAsync(text.AsMemory(), ct);
            p.StandardInput.Close();
            await p.WaitForExitAsync(ct);
        }

        string? deepened = null;
        try
        {
            var toPlay = wav;
            if (_speedFactor is < 0.999 or > 1.001)
            {
                deepened = MakeDeeper(wav, _speedFactor);
                toPlay = deepened;
            }
            await PlayWavAsync(toPlay, ct);
        }
        finally
        {
            TryDelete(wav);
            if (deepened is not null) TryDelete(deepened);
        }
    }

    async Task SpeakElevenLabsAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_elevenLabsApiKey))
        {
            Console.Error.WriteLine(
                "[tts] elevenlabs necesita la variable de entorno CLAUDIO_ELEVENLABS_API_KEY (nunca en appsettings.json).");
            return;
        }
        if (string.IsNullOrWhiteSpace(_elevenLabsVoiceId))
        {
            Console.Error.WriteLine("[tts] elevenlabs necesita 'elevenLabsVoiceId' en appsettings.json.");
            return;
        }

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{_elevenLabsVoiceId}");
        req.Headers.Add("xi-api-key", _elevenLabsApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        req.Content = JsonContent.Create(new
        {
            text,
            model_id = _elevenLabsModel,
            voice_settings = new { stability = 0.5, similarity_boost = 0.75, use_speaker_boost = true },
        });

        using var resp = await ElevenLabsHttp.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            Console.Error.WriteLine($"[tts] ElevenLabs devolvió {(int)resp.StatusCode}: {Trim(err)}");
            return;
        }

        var mp3 = Path.Combine(Path.GetTempPath(), $"claudio-tts-{Guid.NewGuid():N}.mp3");
        await using (var fs = File.Create(mp3))
            await resp.Content.CopyToAsync(fs, ct);

        try { await PlayMp3Async(mp3, ct); }
        finally { TryDelete(mp3); }
    }

    static async Task PlayMp3Async(string mp3, CancellationToken ct)
    {
        using var reader = new Mp3FileReader(mp3);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();

        while (output.PlaybackState == PlaybackState.Playing)
        {
            if (ct.IsCancellationRequested)
            {
                output.Stop();
                break;
            }
            await Task.Delay(100, CancellationToken.None);
        }
    }

    static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";

    /// <summary>
    /// Reescribe la frecuencia de muestreo del WAV (sin tocar las muestras) para que
    /// suene más grave y pausada cuanto más bajo sea <paramref name="speedFactor"/>
    /// (p. ej. 0.90 ≈ un tono más grave y un 10 % más lenta). Es el mismo truco que
    /// un "audio ralentizado": no requiere ninguna librería de pitch-shift.
    /// </summary>
    static string MakeDeeper(string wavPath, double speedFactor)
    {
        using var reader = new WaveFileReader(wavPath);
        var original = reader.WaveFormat;
        var deeper = new WaveFormat((int)(original.SampleRate * speedFactor), original.BitsPerSample, original.Channels);

        var outPath = Path.Combine(Path.GetTempPath(), $"claudio-tts-deep-{Guid.NewGuid():N}.wav");
        using (var writer = new WaveFileWriter(outPath, deeper))
            reader.CopyTo(writer);
        return outPath;
    }

    static async Task PlayWavAsync(string wav, CancellationToken ct)
    {
        using var reader = new AudioFileReader(wav);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();

        while (output.PlaybackState == PlaybackState.Playing)
        {
            if (ct.IsCancellationRequested)
            {
                output.Stop();
                break;
            }
            await Task.Delay(100, CancellationToken.None);
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* da igual */ }
    }

    public void Dispose() => _sapi?.Dispose();
}
