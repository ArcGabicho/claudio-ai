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
      → ejecutar                 (ActionRouter: open_app | web_search | web_answer | shell | ...)
      → hablar                   (SAPI / System.Speech; opcional: Piper o ElevenLabs)
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
- **Voz de salida:** por defecto usa [Piper](https://github.com/rhasspy/piper) con una
  voz masculina grave en español (ver más abajo); si `ttsEngine` es `sapi` o no
  encuentra Piper, cae a SAPI (`System.Speech`), incluido en Windows sin instalar nada
  — para que SAPI hable en español con acento nativo, instala una voz en
  *Configuración → Hora e idioma → Idioma y región → añade "Español"* → **Voz**.

En la primera ejecución se descarga el modelo Whisper (`large-v3-turbo` por
defecto, ~1.6 GB — la mejor precisión que ofrece Whisper sin perder demasiada
velocidad) a `%LOCALAPPDATA%\claudio-ai\models\`.

### Escucha rápida con GPU (NVIDIA)

`large-v3-turbo` es mucho más preciso que `base`, pero en CPU va demasiado
lento para hablar en tiempo real (~9 s por cada 1 s de audio en un portátil
normal). Con una GPU NVIDIA y CUDA instalado, baja a **~0.3 s por segundo de
audio** (medido con una RTX 3050 6 GB) — Claudio detecta el runtime de CUDA
solo, sin tocar nada en el código ni en `appsettings.json`.

**Importante:** el paquete de Whisper.net para GPU necesita el runtime de
**CUDA 12.x** en concreto — instalar la versión más reciente (13.x) no sirve,
aunque el driver ya la soporte, porque las DLL se llaman distinto
(`cudart64_12.dll` frente a `cudart64_13.dll`) y no son intercambiables:

```powershell
winget install --id Nvidia.CUDA --version 12.9
```

Es el toolkit completo (varios GB) y pide permisos de administrador; si
prefieres instalar solo lo imprescindible (sin el compilador, ejemplos ni
documentación) y sin tocar el driver que ya tengas:

```powershell
$exe = "$env:TEMP\cuda_12.9.1.exe"
Invoke-WebRequest "https://developer.download.nvidia.com/compute/cuda/12.9.1/local_installers/cuda_12.9.1_576.57_windows.exe" -OutFile $exe
Start-Process $exe -ArgumentList "-s","cudart_12.9","cublas_12.9","cublas_dev_12.9" -Verb RunAs -Wait
```

Puedes tener a la vez CUDA 12.x y 13.x instalados sin conflicto — Claudio
busca la carpeta `v12.*` que encuentre y la usa, sin necesidad de reiniciar
Windows (aunque si ya tenías una terminal abierta, ábrela de nuevo para que
el PATH del sistema también quede al día). Sin GPU o sin CUDA instalado, cae
a CPU sola y sigue funcionando, pero conviene bajar a un modelo más pequeño —
`whisperModel: "small"` va decente en CPU (~2× tiempo real); `base` es el más
rápido de todos aunque menos preciso. Compara tiempos reales en tu máquina
con `dotnet run -c Release -- --bench archivo.wav 3` (la primera pasada tras
instalar CUDA es lenta porque la GPU compila y cachea sus kernels; las
siguientes ya van a la velocidad real).

### Voz grave con Piper (opcional pero recomendado)

Piper es un TTS neuronal offline, bastante mejor que las voces SAPI de Windows.
Se le puede además bajar el tono y la velocidad para una voz más grave y
pausada — no es una clonación de ninguna voz de personaje, solo un ajuste
sobre una voz neutra:

```powershell
# 1. Piper (Windows x64)
Invoke-WebRequest -Uri "https://github.com/rhasspy/piper/releases/latest/download/piper_windows_amd64.zip" -OutFile "$env:TEMP\piper.zip"
Expand-Archive "$env:TEMP\piper.zip" "$env:LOCALAPPDATA\claudio-ai\tools" -Force

# 2. Una voz masculina en español (Piper ofrece varias; davefx y sharvard son las más graves)
$voice = "$env:LOCALAPPDATA\claudio-ai\models"
New-Item -ItemType Directory -Force -Path $voice | Out-Null
Invoke-WebRequest -Uri "https://huggingface.co/rhasspy/piper-voices/resolve/main/es/es_ES/davefx/medium/es_ES-davefx-medium.onnx" -OutFile "$voice\es_ES-davefx-medium.onnx"
Invoke-WebRequest -Uri "https://huggingface.co/rhasspy/piper-voices/resolve/main/es/es_ES/davefx/medium/es_ES-davefx-medium.onnx.json" -OutFile "$voice\es_ES-davefx-medium.onnx.json"
```

En `appsettings.json`:

```json
"ttsEngine": "piper",
"piperPath": "C:\\ruta\\a\\claudio-ai\\tools\\piper\\piper.exe",
"piperModel": "C:\\ruta\\a\\claudio-ai\\models\\es_ES-davefx-medium.onnx",
"piperSpeedFactor": 0.9
```

`piperSpeedFactor` (por defecto `1.0`) reescribe la frecuencia de muestreo del
audio que genera Piper: por debajo de `1.0` la voz suena más grave y pausada
(prueba entre `0.85` y `0.95`); por encima, más aguda y rápida. Pruébalo con
`dotnet run -c Release -- --say "texto de prueba"` sin tener que reiniciar
Claudio, y ajusta el número a tu gusto — no hay una respuesta "correcta", es
cuestión de oído. Otras voces masculinas de España para probar: `sharvard`,
`carlfm` (mismo patrón de URL, cambiando el nombre).

### Voz de nivel cinematográfico con ElevenLabs (de pago, opcional)

Piper es gratis y offline; [ElevenLabs](https://elevenlabs.io) suena mucho
más natural (voces reales entrenadas, no síntesis clásica) a cambio de una
cuenta de pago y de que el texto que Claudio va a decir salga de tu equipo.
Tienes que darte de alta tú (no puedo crear la cuenta por ti):

1. Crea una cuenta en <https://elevenlabs.io> y saca una API key en
   *Settings → API Keys*.
2. Elige una voz de su biblioteca (*Voice Library*, busca algo grave/narrador)
   y copia su **Voice ID**.
3. Guarda la clave **solo** como variable de entorno de usuario (nunca en
   `appsettings.json`, que está en git):
   ```powershell
   setx CLAUDIO_ELEVENLABS_API_KEY "tu-api-key"
   ```
   Ábrela en una terminal nueva para que se aplique.
4. En `appsettings.json`:
   ```json
   "ttsEngine": "elevenlabs",
   "elevenLabsVoiceId": "el-id-de-la-voz-que-elegiste"
   ```

Pruébalo con `dotnet run -c Release -- --say "texto de prueba"` — el motor de
voz de la primera línea debe decir `elevenlabs`. `ttsEngine: "auto"` nunca la
elige sola (es de pago): hay que pedirla explícitamente.

### Notificaciones a Discord (opcional)

`new_project`, `clone_repo`, los fallos no controlados de un turno y los fallos
al arrancar avisan a un canal de Discord vía webhook (`src/Notifications/DiscordNotifier.cs`),
con embeds 🚀 iniciada / ✅ completada / ❌ fallida / 🔄 progreso / ⚠️ alerta. Si
no se configura, esas notificaciones simplemente no se envían — Claudio sigue
funcionando igual.

1. En Discord: *Configuración del servidor → Integraciones → Webhooks → Nuevo
   webhook*, y copia su URL.
2. Guárdala **solo** como variable de entorno de usuario (nunca en
   `appsettings.json`, que está en git):
   ```powershell
   setx CLAUDIO_DISCORD_WEBHOOK_URL "https://discord.com/api/webhooks/…"
   ```
   Ábrela en una terminal nueva para que se aplique.

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

### Crear y clonar proyectos por voz

Usa solo `git` (nada de CLI de GitHub ni cuentas que autenticar):

- **"Claudio, crea un proyecto llamado tienda online"** — crea la carpeta en
  `OneDrive\Documents\Proyectos\tienda-online` con `git init` y un primer
  commit. Solo local.
- **"Claudio, clona claudio-ai de ArcGabicho"** / **"clona mi repo X"** — clona
  ese repositorio (debe ser público, o uno privado tuyo si ya tienes sesión de
  git guardada en el equipo) a tu carpeta de Proyectos. Si no dices de quién
  es, usa el usuario configurado en `gitHubUsername`.

De momento estas dos acciones solo operan sobre proyectos de **Windows**
(no WSL). Puedes probarlas sin usar la voz:

```powershell
dotnet run -c Release -- --new-project "mi proyecto"
dotnet run -c Release -- --clone-repo "ArcGabicho/claudio-ai"
```

### Preguntarle algo y que busque en internet

**"Claudio, qué es el efecto Mandela"**, **"Claudio, cuánto cuesta ahora mismo
el dólar en soles"**, **"Claudio, qué pasó hoy con..."** — para preguntas que
cambian con el tiempo o que Claudio no sabe con certeza, busca en internet con
las herramientas del propio Claude Code (`WebSearch`/`WebFetch`, permitidas
sin pedir confirmación solo para esta consulta) y te contesta hablando, breve
y sin abrir el navegador. Si en cambio quieres navegar tú mismo, pídele que
"busque X" o "abra una búsqueda de X" y te abre la pestaña en el navegador.

Pruébalo sin voz con `dotnet run -c Release -- --web "tu pregunta"`.

### Memoria persistente

**"Claudio, recuerda que la wifi de invitados es Casa2024"** — guarda la nota
en `%LOCALAPPDATA%\claudio-ai\memory.md` (texto plano, una nota por línea,
editable a mano) y la tiene en cuenta en **todas** las conversaciones futuras,
aunque cierres Claudio o reinicies el PC. Luego, **"Claudio, cuál es la wifi de
invitados"** te la dice sin que tengas que repetírsela. **"Claudio, olvida lo
de la wifi de invitados"** la borra (busca por parecido, no hace falta decirlo
exacto). Se guardan como mucho `maxMemoryEntries` notas (200 por defecto);
al llegar al límite se olvidan las más antiguas.

Diagnósticos sin voz:

```powershell
dotnet run -c Release -- --memory                    # lista todo lo guardado
dotnet run -c Release -- --remember "texto"           # añade una nota directamente
dotnet run -c Release -- --forget "de qué trataba"    # borra la que mejor encaje
dotnet run -c Release -- --memory-clear               # la vacía entera
dotnet run -c Release -- --decide "una orden hablada" # qué acción y respuesta decidiría Claude (sin ejecutarla)
```

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
dotnet run -c Release -- --new-project "mi proyecto"           # crea carpeta + git init + primer commit
dotnet run -c Release -- --clone-repo "ArcGabicho/claudio-ai"  # clona un repositorio a Proyectos
dotnet run -c Release -- --web "tu pregunta"                   # busca en internet y responde en texto
dotnet run -c Release -- --decide "una orden hablada"          # qué acción tomaría Claude, sin ejecutarla
dotnet run -c Release -- --memory                              # lista las notas guardadas
dotnet run -c Release -- --bench audio.wav 3                   # tiempos de carga e inferencia de Whisper (CPU vs GPU)
```

`--hear` es el más útil para ajustar el micrófono: si transcribe ruido de fondo
como frases, sube `silenceThreshold`; si corta tus frases a mitad, sube
`silenceMs`.

## Configuración — `appsettings.json`

| Clave                    | Por defecto | Notas                                                        |
|--------------------------|-------------|---------------------------------------------------------------|
| `language`               | `es`        | idioma de transcripción y respuestas                          |
| `whisperModel`           | `large-v3-turbo` | `tiny` \| `base` \| `small` \| `medium` \| `large-v3` \| `large-v3-turbo` — más grande = más preciso y más lento; usa GPU si hay CUDA |
| `claudeCommand`          | `claude`    | ejecutable del cerebro                                         |
| `ttsEngine`              | `piper`     | `auto` \| `sapi` \| `piper` \| `elevenlabs` \| `none`           |
| `piperModel`             | ver abajo   | ruta al `.onnx` de Piper                                        |
| `piperPath`              | `piper`     | ruta al ejecutable de Piper (por defecto lo busca en el PATH)   |
| `piperSpeedFactor`       | `1.0`       | `<1.0` = voz más grave y pausada; `>1.0` = más aguda y rápida   |
| `elevenLabsVoiceId`      | `null`      | id de la voz de ElevenLabs (solo si `ttsEngine: elevenlabs`)    |
| `elevenLabsModel`        | `eleven_multilingual_v2` | modelo de ElevenLabs a usar                        |
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
| `gitHubUsername`         | `ArcGabicho` | usuario de GitHub por defecto para "clona mi repo X"            |
| `maxMemoryEntries`       | `200`       | notas máximas en la memoria persistente antes de olvidar las más antiguas |
| *(sin clave)*            | —           | `discordWebhookUrl` no existe aquí: se lee solo de `CLAUDIO_DISCORD_WEBHOOK_URL` (ver arriba) |

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
  Speech/SpeechToText.cs         Whisper.net + descarga del modelo; detecta y usa CUDA 12 si lo encuentra
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
  Projects/GitOps.cs             crea proyectos locales y clona repositorios (solo git, sin gh)
  Memory/MemoryStore.cs          notas persistentes ("recuerda que...") en memory.md, entre reinicios
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
6. ~~TTS de calidad con Piper~~ — hecho, con voz masculina grave por defecto.
7. ~~Crear/clonar proyectos por voz~~ — hecho para Windows, solo con `git` (sin GitHub CLI); falta soporte para WSL.
8. ~~Responder preguntas buscando en internet~~ — hecho (`web_answer`, vía las herramientas de Claude Code).
9. ~~Memoria persistente entre reinicios~~ — hecho (`remember`/`forget`, `Memory/MemoryStore.cs`).
10. **Interrumpirlo mientras habla** ("Claudio, para") — pendiente.
11. **Control de sistema y multimedia**: volumen, brillo, play/pausa, bloquear/apagar/reiniciar (con confirmación), captura de pantalla — pendiente.
12. ~~Escucha con GPU y modelo grande~~ — hecho: `large-v3-turbo` por defecto, con aceleración CUDA 12 automática (~0.3 s por segundo de audio con GPU, frente a los ~9 s en CPU).
13. ~~Voz de nivel cinematográfico~~ — hecho: soporte para ElevenLabs (`ttsEngine: elevenlabs`), de pago y opcional.

## Licencia

[MIT](LICENSE.md) © 2026 ArcGabicho
