# Cliperino

Repetición instantánea para Windows, al estilo de NVIDIA ShadowPlay. Corre en la bandeja del sistema, mantiene un buffer con los últimos segundos de pantalla y audio y, al apretar **Ctrl+Alt+F10**, los guarda como `.mp4`.

## Requisitos
- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/) para compilar
- ffmpeg 6 o superior. Si la app no lo encuentra, ofrece descargarlo (build *essentials* de gyan.dev) a `%LOCALAPPDATA%\Cliperino\ffmpeg`. También lo busca junto al `.exe` y en el `PATH`.

## Compilar y ejecutar
```
dotnet run
```

## Publicar
```
dotnet publish
```
Genera `publish\Cliperino.exe`: un único ejecutable de unos 50 MB que incluye el runtime de .NET, así que no hace falta instalar nada. Es lo único que hay que distribuir. La configuración está en `Cliperino.csproj`, en el bloque `Release`.

- **Versión liviana** (unos pocos MB, pero requiere el [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)):
  ```
  dotnet publish -p:SelfContained=false
  ```
- **ffmpeg** no va dentro del ejecutable: la app ofrece descargarlo la primera vez. Si preferís distribuirlo junto al `.exe`, la app lo encuentra igual, pero el build *essentials* es GPL y hay que incluir su licencia. 

## Uso
- El ícono de la bandeja cambia de color según el estado: 🔴 grabando, ⚪ desactivado, 🟠 detectando o guardando.
- **Ctrl+Alt+F10** guarda los últimos N segundos en `Videos\Cliperino`. Al hacer clic en la notificación se abre el archivo en el Explorador.
- Desde el menú de la bandeja se activa o desactiva la repetición, se guarda un clip, se abre la carpeta y se entra a los **Ajustes**: duración, FPS, bitrate, modo de captura, juegos, monitor, codificador, atajo, carpeta, audio, micrófono, desfase de audio e inicio con Windows.

## Modos de captura
| Modo | Qué graba | Cómo |
|---|---|---|
| **Juego** (por defecto) | Solo la imagen del juego, en pantalla completa o en ventana, escalada a la resolución del monitor | Windows.Graphics.Capture (`gfxcapture`, ffmpeg 8+) |
| **Pantalla** | Todo el monitor elegido, escritorio y otras ventanas incluidos | Desktop Duplication (`ddagrab`) |

- **Qué juego capturar:** en modo juego, Cliperino revisa cada 3 s si está abierto alguno de los ejecutables de la lista **Juegos** (por defecto `VALORANT-Win64-Shipping.exe`).
- **Si no hay ningún juego abierto,** graba el monitor.
- **Al abrir o cerrar un juego** cambia solo de modo, pero ese cambio reinicia el buffer.
- **Si minimizás el juego,** el clip muestra su último frame hasta que volvés.
- **¿Por qué hace falta el modo juego?** Muchos juegos en pantalla completa mandan la imagen directo al monitor ("independent flip" o planos de overlay) sin pasar por la composición de Windows. En ese caso Desktop Duplication ve el escritorio de abajo en lugar del juego. Pasó con Valorant después de minimizarlo y volver.
  - NVIDIA no tiene el problema porque captura a nivel de driver (NvFBC).
  - En OBS, "Captura de juego" se inyecta en el juego, y por eso está en la lista de permitidos de los anticheats.
  - El modo juego de Cliperino toma la imagen de la ventana del juego (que existe aunque esté en pantalla completa) sin inyectar nada.

## Audio
- **Sistema:**
  - *Todo el escritorio* (loopback del dispositivo predeterminado)
  - *Solo el juego*: captura por proceso; requiere Windows 11 y sólo aplica en modo juego. Si no está disponible, usa el escritorio.
- **Micrófono:** opcional; se mezcla al guardar el clip.

## GPUs soportadas
Cliperino detecta qué GPU maneja el monitor elegido y captura con `ddagrab` (Desktop Duplication) en esa misma GPU. Después prueba los codificadores en este orden y se queda con el primero que funcione:

| Orden | Pipeline | Cuándo |
|---|---|---|
| 1 | Zero-copy en la GPU del monitor: NVENC, AMF o Quick Sync | NVIDIA, AMD (dedicada o APU) o Intel integrada |
| 2 | Mismo encoder, pasando por memoria del sistema | Si el driver rechaza el zero-copy |
| 3 | Encoder de otra GPU, vía memoria | Laptops híbridas (pantalla en la iGPU, encoder en la dGPU) |
| 4 | x264 en CPU | Sin encoder por hardware |
| 5 | Captura GDI + x264 | Último recurso |

El resultado se guarda en caché y se vuelve a detectar si cambia el hardware, el monitor o la versión de ffmpeg. También se puede forzar desde Ajustes con "Volver a detectar".

### Diagnóstico
```
Cliperino.exe --probe
```
Lista las GPUs y los monitores, prueba cada pipeline y deja el detalle en `%LOCALAPPDATA%\Cliperino\logs\probe.log`. El log general está en `logs\cliperino.log`.

## Estructura
| Carpeta | Contenido |
|---|---|
| `Core/` | Rutas, log y configuración |
| `Ffmpeg/` | Búsqueda/descarga de ffmpeg, procesos y Job Object |
| `Capture/` | Detección de GPU, pipelines, buffer de video y armado de clips |
| `Audio/` | Captura WASAPI y buffer circular |
| `UI/` | Bandeja, atajo global y ventana de ajustes |

## Cómo funciona
- **Video:** ffmpeg graba segmentos MPEG-TS de 2 s en `%LOCALAPPDATA%\Cliperino\buffer`. Cliperino borra los que quedan fuera de la ventana de replay.
- **Audio:** el audio (del escritorio o del juego) y el micrófono opcional se guardan en buffers circulares en RAM, alineados con el reloj del sistema.
- **Sincronización:** al guardar, el audio se alinea con los timestamps (PTS) del video. Así sigue sincronizado aunque haya huecos, por ejemplo con el juego minimizado.
- **Guardado:** se concatenan los segmentos sin re-codificar, se codifica el audio a AAC (mezclando el micrófono si está activado) y se escribe el `.mp4`.
- Los procesos ffmpeg están atados a un Job Object, así que si Cliperino se cierra o se cuelga, ffmpeg se cierra con él.

## Limitaciones
- Un solo atajo global por combinación. Si la overlay de NVIDIA usa Alt+F10, Cliperino no puede usarlo. Si el atajo está ocupado, la app avisa.
- En modo pantalla, algunos juegos en pantalla completa pueden no verse (sale el escritorio) y el contenido con DRM puede salir en negro. Para juegos conviene el modo juego.
- En pantallas HDR los colores pueden verse lavados.
- El inicio del clip tiene unos 2 s de precisión (se corta por segmentos sin re-codificar).
- Si el audio no queda sincronizado, se ajusta con "Desfase de audio" en Ajustes.
