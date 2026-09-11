# Claudio · asistente de voz para Windows 11

MVP de un asistente tipo Jarvis en **C# / .NET 10**. Vive en la **bandeja del
sistema**, escucha el micrófono todo el rato y, al oír **"Claudio, {orden}"**,
transcribe con Whisper (local), decide qué hacer con Claude y ejecuta la acción
(abrir programas, buscar en la web, consultar el sistema), respondiendo de viva
voz. Arranca con Windows por defecto, como Discord.

## Pipeline

```
escucha continua  (NAudio + VAD por energía, trocea en frases)
      → transcribir cada frase   (Whisper.net, 100 % local)
      → ¿empieza por "Claudio"?  si no, se ignora
      → decidir                 (CLI de Claude Code → JSON {action,target,say})
      → ejecutar                 (ActionRouter: open_app | web_search | shell)
      → hablar                   (SAPI / System.Speech; opcional: Piper)
```

La palabra de activación se detecta sobre el propio texto de Whisper (no con el
reconocedor de voz de Windows: Windows 11 ya no trae el motor clásico que
necesita `System.Speech.Recognition`). Es más pesado que un detector dedicado,
pero funciona 100 % offline y sin dependencias nuevas.

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
dotnet build -c Release
.\bin\Release\net10.0-windows\claudio.exe
```

No aparece ninguna consola: Claudio queda como un icono en la **bandeja del
sistema** (círculo azul = escuchando). Di **"Claudio, qué hora es"** o
**"Claudio, abre el navegador"** en voz alta desde cualquier sitio; el icono se
pone verde mientras te escucha y ámbar mientras piensa/responde. Si dices solo
"Claudio" a secas, se queda en verde esperando la orden en la frase siguiente.

### Abrir un proyecto por voz

**"Claudio, abre el proyecto claudio-ai"** — Claudio no adivina la ruta: busca
de verdad las carpetas que hay bajo `OneDrive\Documents\Proyectos` y bajo
`~/Proyectos` de la distro WSL configurada, y compara lo que dijiste contra los
nombres reales (tolera acentos, mayúsculas y transcripciones imperfectas). Con
`dotnet run -c Release -- --projects` ves qué encuentra, y con `--match "nombre"`
compruebas a qué proyecto resolvería una frase concreta.

- Si dices con qué abrirlo ("…**con Visual Studio Code**", "…**con Claude Code**",
  "…**con los dos**"), lo abre directo.
- Si no lo dices, Claudio **pregunta** ("¿con Visual Studio Code, con Claude Code,
  o con los dos?") y espera tu respuesta en la frase siguiente.
- Si el nombre es ambiguo (dos proyectos muy parecidos, uno en Windows y otro en
  WSL), primero pregunta cuál de los dos.
- Claude Code en un proyecto de WSL requiere tener el CLI `claude` instalado
  **dentro** de esa distro; si no está, Claudio te avisa por voz en vez de abrir
  una terminal con un error.

Menú del icono (clic derecho):

| Opción | Qué hace |
|---|---|
| Pausar escucha / Reanudar | corta o retoma el micrófono (doble clic hace lo mismo) |
| Iniciar con Windows | activa/desactiva el arranque automático (activado por defecto la primera vez) |
| Ver registro… | abre `claudio.log` con el historial de activaciones y errores |
| Salir | cierra Claudio |

> **Importante:** el arranque automático registra la ruta del `.exe` que estés
> ejecutando. Para que sobreviva a un `dotnet build` posterior, compílalo una
> vez y déjalo en una carpeta estable (o publícalo con `dotnet publish`); si lo
> mueves, vuelve a activar "Iniciar con Windows" desde el menú. Ejecutar con
> `dotnet run` **no** se registra para el arranque (apunta a `dotnet.exe`).

### Diagnósticos (con consola, útiles para calibrar)

```powershell
dotnet run -c Release -- --hear 20                            # escucha 20 s e imprime cada frase que transcribe
dotnet run -c Release -- --recognizers                        # lista reconocedores de System.Speech (informativo)
dotnet run -c Release -- --transcribe C:\ruta\audio.wav       # solo transcribir un WAV
dotnet run -c Release -- --record 5                           # grabar 5 s y guardar el WAV
dotnet run -c Release -- --listen                              # graba una frase con fin automático y la transcribe
dotnet run -c Release -- --say "hola, esto es una prueba"      # probar la voz de salida
dotnet run -c Release -- --do '{\"action\":\"shell\",\"target\":\"Get-Date\",\"say\":\"\"}'  # probar una acción
dotnet run -c Release -- --projects                            # lista los proyectos encontrados (Windows + WSL)
dotnet run -c Release -- --match "claudio ai"                  # a qué proyecto(s) resolvería ese nombre
```

`--hear` es el más útil para ajustar el micrófono: si transcribe ruido de fondo
como frases, sube `silenceThreshold`; si corta tus frases a mitad, sube
`silenceMs`.

## Configuración — `appsettings.json`

| Clave                    | Por defecto | Notas                                                        |
|--------------------------|-------------|---------------------------------------------------------------|
| `language`               | `es`        | idioma de transcripción y respuestas                          |
| `whisperModel`           | `base`      | `tiny` \| `base` \| `small` \| `medium` — `tiny` responde más rápido |
| `claudeCommand`          | `claude`    | ejecutable del cerebro                                         |
| `ttsEngine`              | `auto`      | `auto` \| `sapi` \| `piper` \| `none`                          |
| `piperModel`             | `null`      | ruta al `.onnx` de Piper                                        |
| `wakeWord`               | `Claudio`   | palabra que activa a Claudio                                    |
| `wakeWordVariants`       | ver abajo   | grafías que Whisper suele usar para "Claudio" y también activan |
| `chimeOnWake`            | `true`      | sonido del sistema al detectar la activación                    |
| `silenceMs`              | `800`       | silencio (ms) que da por acabada una frase                      |
| `silenceThreshold`       | `0.02`      | energía mínima (0..1) para considerar que hay voz                |
| `maxCommandSeconds`      | `15`        | tope de duración de una frase/orden                              |
| `noSpeechTimeoutSeconds` | `4`         | tras decir "Claudio" solo, segundos que se espera la orden       |
| `startWithWindows`       | `true`      | arrancar con Windows la primera vez que se ejecuta               |
| `recordSeconds`          | `6`         | solo para el diagnóstico `--record`                              |
| `wslDistro`              | `archlinux` | distro de WSL donde también se buscan proyectos; vacío para desactivar |
| `wslProjectRoot`         | `~/Proyectos` | carpeta dentro de esa distro; `~` se resuelve al `$HOME` real  |

`projectWindowsRoots` (no aparece en `appsettings.json` por defecto) son las
carpetas de Windows donde se buscan proyectos; por defecto
`OneDrive\Documents\Proyectos` del usuario actual. Para añadir más carpetas o
cambiarla, añade la clave con una lista de rutas.

Cualquier valor se puede sobreescribir con variables `CLAUDIO_*`
(`CLAUDIO_WHISPER_MODEL=small`, `CLAUDIO_TTS=sapi`, `CLAUDIO_WAKE_WORD=Jarvis`,
`CLAUDIO_SILENCE_THRESHOLD=0.03`, `CLAUDIO_START_WITH_WINDOWS=false`, …).

## Estructura

```
src/
  Program.cs                    arranque de la bandeja + diagnósticos (--hear, --listen, --say, --do, ...)
  ClaudioConfig.cs               configuración (json + env)
  Diagnostics/Log.cs             registro a %LOCALAPPDATA%\claudio-ai\claudio.log
  Audio/AudioRecorder.cs         grabación de ventana fija (diagnóstico --record)
  Audio/CommandCapture.cs        grabación de una frase con fin automático por silencio (diagnóstico --listen)
  Audio/Voice.cs                 texto a voz: SAPI (System.Speech) o Piper
  Speech/SpeechToText.cs         Whisper.net + descarga del modelo
  Speech/ContinuousListener.cs   escucha continua + VAD + Whisper; detecta "Claudio, {orden}"
  Brain/ClaudeBrain.cs           invoca el CLI de claude, parsea la acción
  Brain/AssistantAction.cs
  Actions/ActionRouter.cs        open_app | web_search | shell (PowerShell, lista blanca)
  Tray/ClaudioTrayContext.cs     orquesta el turno completo; icono y menú de la bandeja
  Tray/Autostart.cs              arranque con Windows (registro HKCU\...\Run)
  Tray/TrayIconFactory.cs        dibuja el icono de la bandeja (sin ficheros .ico)
  Tray/SystemChime.cs            avisos sonoros del sistema
  Projects/ProjectResolver.cs    busca proyectos reales (Windows + WSL), empareja el nombre dicho y los abre
  Projects/ProjectRef.cs         nombre/tipo/ruta de un proyecto encontrado
```

### Acción `shell`

Solo se ejecutan consultas de PowerShell **no destructivas**: el primer comando
debe estar en una lista blanca (`Get-Date`, `Get-CimInstance`, `Get-Volume`,
`whoami`, `hostname`, `systeminfo`, …) y se rechaza cualquier verbo peligroso
(`Remove-`, `Set-`, `New-`, `Stop-`, `Invoke-`, …), tuberías `|`, redirecciones
`>`, `;`, `&` y subexpresiones `$( )`.

## Roadmap

1. ~~Wake word con escucha continua y VAD~~ — hecho (Whisper en vez de un detector dedicado).
2. **Cerebro por API**: `ClaudeBrain` → SDK `Anthropic` con tool-calling real y *prompt caching* (bajaría la latencia del turno).
3. **Más acciones**: control de ventanas, multimedia (SMTC), volumen/brillo, Telegram, correo, recordatorios.
4. ~~Servicio en segundo plano que arranque con la sesión~~ — hecho (bandeja + `HKCU\...\Run`).
5. Detector de activación dedicado (Porcupine) si la carga de Whisper en continuo pesa demasiado.
6. TTS de calidad con Piper y voz española fija.
