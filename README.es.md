# Cliperino

[English](README.md) · **Español**

Repetición instantánea para Windows. Corre en la bandeja del sistema, mantiene un buffer con los últimos segundos de pantalla y audio y, al apretar **Ctrl+Alt+F10**, los guarda como `.mp4`.

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
- **Ctrl+Alt+F10** guarda los últimos N segundos en `Videos\Cliperino`. Al hacer clic en la notificación se abre el editor para recortarlo.
- Desde el menú de la bandeja se activa o desactiva la repetición, se guarda un clip, se edita un clip, se abre la carpeta y se entra a los **Ajustes**.
- **Ajustes** tiene tres pestañas:
  - **Captura:** vista previa en vivo de lo que se graba, duración, modo, juegos, monitor, FPS, audio, micrófono, atajo, aviso y carpeta.
  - **Avanzado:** codificador, bitrate y desfase de audio.
  - **Extras:** inicio con Windows, actualizaciones, idioma (español o inglés, por defecto el de Windows), versión y enlaces al proyecto.
- La vista previa de la pestaña Captura solo corre mientras esa pestaña está visible. Si cambiás el monitor, el modo o los juegos, muestra cómo va a quedar antes de guardar, sin cortar la grabación.

## Editor de clips
Se abre haciendo clic en la notificación "Clip guardado" o con **Editar clip…**, en la bandeja o abajo a la izquierda de los Ajustes.

- **Recortar:** con las manijas de la línea de tiempo o con **Inicio aquí / Fin aquí** (teclas **I** / **O**). **P** reproduce solo el tramo elegido y las flechas avanzan cuadro por cuadro (**Shift** + flecha avanza 1 s).
- **Quitar audio:** deja el clip sin pista de audio.
- **Corte exacto:** recodifica el video con la GPU (o x264) para cortar en el cuadro elegido. Sin esta opción el corte es instantáneo pero cae en el keyframe anterior, hasta 1 s antes.
- **Guardar:** por defecto crea `<clip>_editado.mp4` al lado del original; con **Reemplazar el original** lo sobrescribe.
- **Requisito:** usa Microsoft Edge WebView2, que ya viene instalado en Windows 10 y 11.


## Audio
- **Sistema:**
  - *Todo el escritorio* (loopback del dispositivo predeterminado)
  - *Solo el juego*: captura por proceso; requiere Windows 11 y sólo aplica en modo juego. Si no está disponible, usa el escritorio.
- **Micrófono:** opcional; se mezcla al guardar el clip.

## Estructura
```text
cliperino/
├── Program.cs                   # Arranque, instancia única y modo --probe
├── Cliperino.csproj             # Proyecto .NET 10 y configuración de publicación
├── Core/                        # Base compartida
├── Ffmpeg/                      # Manejo de ffmpeg
├── Capture/                     # Video
├── Audio/                       # Sonido
├── UI/                          # Interfaz
└── Update/                      # Actualizaciones
```

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
- El aviso en pantalla no se ve sobre juegos en pantalla completa exclusiva; sí en ventana sin bordes o en ventana, que es como corren la mayoría de los juegos actuales. Nunca aparece en los clips.
- El inicio del clip tiene unos 2 s de precisión (se corta por segmentos sin re-codificar).
- Si el audio no queda sincronizado, se ajusta con "Desfase de audio" en Ajustes.
