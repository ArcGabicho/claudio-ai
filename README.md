# Claudio · asistente de voz para Windows 11

MVP de un asistente tipo Jarvis en **C# / .NET 10**. Escucha una orden por
micrófono, la transcribe con Whisper (local), decide qué hacer con Claude y
ejecuta la acción (abrir programas, buscar en la web, consultar el sistema),
respondiendo de viva voz.

## Pipeline

```
ENTER → grabar        (NAudio, WAV 16 kHz mono 16-bit)
      → transcribir    (Whisper.net, 100 % local)
      → decidir        (CLI de Claude Code → JSON {action,target,say})
      → ejecutar        (ActionRouter: open_app | web_search | shell)
      → hablar          (SAPI / System.Speech; opcional: Piper)
```

El "cerebro" usa el **CLI de `claude`**, que va cubierto por la suscripción
Claude Pro/Max. Para pasar a un servicio siempre-activo se sustituye
`ClaudeBrain` por el SDK oficial de Anthropic (NuGet `Anthropic`) con una API
key; el resto del código no cambia.

## Requisitos

- **Windows 11** (x64).
- **.NET SDK 10** — <https://dotnet.microsoft.com/download> o `winget install Microsoft.DotNet.SDK.10`.
- **CLI de `claude`** en el PATH y con sesión iniciada (`claude` a secas debe abrir la sesión).
- Un **micrófono** con permiso concedido en *Configuración → Privacidad y seguridad → Micrófono*
  (activa además "Permitir que las aplicaciones de escritorio accedan al micrófono").
- **Voz de salida:** se usa SAPI (`System.Speech`), incluido en Windows, sin instalar nada.
  - Para que hable en español con acento nativo, instala una voz española:
    *Configuración → Hora e idioma → Idioma y región → añade "Español"* y, en sus
    opciones de idioma, descarga **Voz**. Claudio la detecta y la usa automáticamente.
  - Alternativa de más calidad: [Piper](https://github.com/rhasspy/piper) — pon
    `piper.exe` en el PATH, indica la ruta de un modelo `.onnx` de voz española en
    `appsettings.json` → `piperModel` y `ttsEngine` a `piper`.

En la primera ejecución se descarga el modelo Whisper (`base`, ~142 MB) a
`%LOCALAPPDATA%\claudio-ai\models\`.

## Uso

```powershell
dotnet run -c Release
```

Pulsa **ENTER**, habla durante la ventana de grabación (6 s por defecto) y
espera la respuesta. `q` + ENTER para salir.

### Diagnósticos

```powershell
dotnet run -c Release -- --transcribe C:\ruta\audio.wav      # solo transcribir un WAV
dotnet run -c Release -- --record 5                          # grabar 5 s y guardar el WAV
dotnet run -c Release -- --say "hola, esto es una prueba"     # probar la voz de salida
dotnet run -c Release -- --do '{\"action\":\"shell\",\"target\":\"Get-Date\",\"say\":\"\"}'  # probar una acción
```

## Configuración — `appsettings.json`

| Clave           | Por defecto | Notas                                        |
|-----------------|-------------|----------------------------------------------|
| `language`      | `es`        | idioma de transcripción y respuestas         |
| `whisperModel`  | `base`      | `tiny` \| `base` \| `small` \| `medium`      |
| `recordSeconds` | `6`         | duración fija de cada grabación (MVP)        |
| `claudeCommand` | `claude`    | ejecutable del cerebro                        |
| `ttsEngine`     | `auto`      | `auto` \| `sapi` \| `piper` \| `none`         |
| `piperModel`    | `null`      | ruta al `.onnx` de Piper                      |

Cualquier valor se puede sobreescribir con variables `CLAUDIO_*`
(`CLAUDIO_WHISPER_MODEL=small`, `CLAUDIO_TTS=sapi`, …).

## Estructura

```
src/
  Program.cs              bucle principal + diagnósticos (--transcribe, --record, --say, --do)
  ClaudioConfig.cs        configuración (json + env)
  Audio/AudioRecorder.cs  grabación con NAudio (WaveInEvent)
  Audio/Voice.cs          texto a voz: SAPI (System.Speech) o Piper
  Speech/SpeechToText.cs  Whisper.net + descarga del modelo
  Brain/ClaudeBrain.cs    invoca el CLI de claude, parsea la acción
  Brain/AssistantAction.cs
  Actions/ActionRouter.cs open_app | web_search | shell (PowerShell, lista blanca)
```

### Acción `shell`

Solo se ejecutan consultas de PowerShell **no destructivas**: el primer comando
debe estar en una lista blanca (`Get-Date`, `Get-CimInstance`, `Get-Volume`,
`whoami`, `hostname`, `systeminfo`, …) y se rechaza cualquier verbo peligroso
(`Remove-`, `Set-`, `New-`, `Stop-`, `Invoke-`, …), tuberías `|`, redirecciones
`>`, `;`, `&` y subexpresiones `$( )`.

## Roadmap

1. **Wake word** ("Jarvis") con escucha continua y VAD, en vez de ENTER + duración fija.
2. **Cerebro por API**: `ClaudeBrain` → SDK `Anthropic` con tool-calling real y *prompt caching*.
3. **Más acciones**: control de ventanas, multimedia (SMTC), volumen/brillo, Telegram, correo, recordatorios.
4. **Servicio en segundo plano** que arranque con la sesión de Windows.
5. TTS de calidad con Piper y voz española fija.
