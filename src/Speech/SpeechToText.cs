using System.Text;
using ClaudioAi.Diagnostics;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace ClaudioAi.Speech;

/// <summary>
/// Voz a texto con Whisper.net (bindings de whisper.cpp, 100 % local).
/// El modelo GGML se descarga una sola vez a ~/.local/share/claudio-ai/models.
/// Usa GPU (CUDA 12) si hay una tarjeta NVIDIA compatible; si no, cae a CPU
/// automáticamente — no hace falta configurar nada.
/// </summary>
public sealed class SpeechToText : IDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static bool _runtimePreferenceSet;

    readonly ClaudioConfig _cfg;
    WhisperFactory? _factory;

    public SpeechToText(ClaudioConfig cfg)
    {
        _cfg = cfg;
        EnsureRuntimePreference();
    }

    /// <summary>Prefiere CUDA 12 si hay GPU NVIDIA compatible; si falla al cargar, cae a CPU sola.</summary>
    static void EnsureRuntimePreference()
    {
        if (_runtimePreferenceSet) return;
        _runtimePreferenceSet = true;
        try
        {
            EnsureCudaOnPath();
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu];
        }
        catch (Exception ex)
        {
            Log.Error("No pude fijar la preferencia de runtime de Whisper (seguirá con la de por defecto)", ex);
        }
    }

    /// <summary>
    /// El runtime CUDA12 de Whisper.net necesita cudart64_12.dll/cublas64_12.dll en el PATH.
    /// El instalador del CUDA Toolkit los deja en el PATH de usuario, pero esa variable no
    /// se refresca hasta el siguiente inicio de sesión; aquí los añadimos también al PATH
    /// de este proceso, así funciona ya mismo aunque Windows no se haya reiniciado.
    /// </summary>
    static void EnsureCudaOnPath()
    {
        try
        {
            const string root = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
            if (!Directory.Exists(root)) return;

            var v12Bin = Directory.GetDirectories(root, "v12.*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "bin"))
                .FirstOrDefault(Directory.Exists);
            if (v12Bin is null) return;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (path.Contains(v12Bin, StringComparison.OrdinalIgnoreCase)) return;

            Environment.SetEnvironmentVariable("PATH", $"{v12Bin};{path}");
            Log.Info($"CUDA 12 encontrado, añadido al PATH: {v12Bin}");
        }
        catch (Exception ex)
        {
            Log.Error("No pude comprobar el CUDA Toolkit instalado", ex);
        }
    }

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
        Log.Info($"Whisper cargado con runtime: {RuntimeOptions.LoadedLibrary?.ToString() ?? "(desconocido)"}");
        return _factory;
    }

    public void Dispose() => _factory?.Dispose();
}
