using System.Text;
using Whisper.net;

namespace ClaudioAi.Speech;

/// <summary>
/// Voz a texto con Whisper.net (bindings de whisper.cpp, 100 % local).
/// El modelo GGML se descarga una sola vez a ~/.local/share/claudio-ai/models.
/// </summary>
public sealed class SpeechToText : IDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    readonly ClaudioConfig _cfg;
    WhisperFactory? _factory;

    public SpeechToText(ClaudioConfig cfg) => _cfg = cfg;

    /// <summary>Descarga (si hace falta) y carga el modelo, para no pagar ese coste en el primer turno.</summary>
    public async Task WarmUpAsync(CancellationToken ct = default) => await GetFactoryAsync(ct);

    public async Task<string> TranscribeAsync(string wavPath, CancellationToken ct = default)
    {
        var factory = await GetFactoryAsync(ct);

        await using var processor = factory.CreateBuilder()
            .WithLanguage(_cfg.Language)
            .Build();

        await using var fs = File.OpenRead(wavPath);

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(fs, ct))
            sb.Append(segment.Text);

        return sb.ToString().Trim();
    }

    async Task<WhisperFactory> GetFactoryAsync(CancellationToken ct)
    {
        if (_factory is not null) return _factory;

        Directory.CreateDirectory(_cfg.ModelsDir);
        var modelPath = Path.Combine(_cfg.ModelsDir, $"ggml-{_cfg.WhisperModel}.bin");

        if (!File.Exists(modelPath))
        {
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{_cfg.WhisperModel}.bin";
            Console.WriteLine($"[stt] Descargando modelo Whisper '{_cfg.WhisperModel}' (solo la primera vez)…");

            var tmp = modelPath + ".part";
            await using (var net = await Http.GetStreamAsync(url, ct))
            await using (var file = File.Create(tmp))
                await net.CopyToAsync(file, ct);
            File.Move(tmp, modelPath, overwrite: true);

            Console.WriteLine($"[stt] Modelo guardado en {modelPath}");
        }

        _factory = WhisperFactory.FromPath(modelPath);
        return _factory;
    }

    public void Dispose() => _factory?.Dispose();
}
