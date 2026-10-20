# Cliperino

**English** · [Español](README.es.md)

Instant replay for Windows. It runs in the system tray, keeps a buffer with the last few seconds of screen and audio and, when you press **Ctrl+Alt+F10**, saves them as an `.mp4`.

## Requirements
- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/) to build
- ffmpeg 6 or later. If the app can't find it, it offers to download it (gyan.dev *essentials* build) to `%LOCALAPPDATA%\Cliperino\ffmpeg`. It also looks for it next to the `.exe` and in the `PATH`.

## Build and run
```
dotnet run
```

## Publish
```
dotnet publish
```
Produces `publish\Cliperino.exe`: a single executable of about 50 MB that includes the .NET runtime, so nothing else needs to be installed. It's the only file you need to distribute. The settings live in `Cliperino.csproj`, in the `Release` block.

- **Lightweight version** (a few MB, but requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)):
  ```
  dotnet publish -p:SelfContained=false
  ```
- **ffmpeg** isn't bundled in the executable: the app offers to download it the first time. If you'd rather ship it next to the `.exe`, the app will find it there too, but the *essentials* build is GPL and you have to include its license.

## Usage
- The tray icon changes color depending on the state: 🔴 recording, ⚪ off, 🟠 detecting or saving.
- **Ctrl+Alt+F10** saves the last N seconds to `Videos\Cliperino`. Clicking the notification opens the editor so you can trim it.
- From the tray menu you can turn instant replay on or off, save a clip, edit a clip, open the folder and open **Settings**.
- **Settings** has three tabs:
  - **Capture:** live preview of what's being recorded, duration, mode, games, monitor, FPS, audio, microphone, shortcut, notice and folder.
  - **Advanced:** encoder, bitrate and audio offset.
  - **Extras:** start with Windows, updates, language (Spanish or English, defaults to the Windows language), version and links to the project.
- The preview in the Capture tab only runs while that tab is visible. If you change the monitor, the mode or the games, it shows how it will look before you save, without stopping the recording.

## Clip editor
Opens by clicking the "Clip saved" notification or with **Edit clip…**, in the tray or at the bottom left of Settings.

- **Trim:** with the timeline handles or with **Start here / End here** (keys **I** / **O**). **P** plays only the selected range and the arrow keys step frame by frame (**Shift** + arrow jumps 1 s).
- **Remove audio:** leaves the clip without an audio track.
- **Exact cut:** re-encodes the video with the GPU (or x264) to cut on the chosen frame. Without this option the cut is instant but lands on the previous keyframe, up to 1 s earlier.
- **Save:** by default creates `<clip>_edited.mp4` next to the original; with **Replace the original** it overwrites it.
- **Requirement:** uses Microsoft Edge WebView2, which already comes installed on Windows 10 and 11.


## Audio
- **System:**
  - *Everything playing on the PC* (loopback of the default device)
  - *Just the game*: per-process capture; requires Windows 11 and only applies in game mode. If it's not available, it falls back to the desktop.
- **Microphone:** optional; it's mixed in when the clip is saved.

## Structure
```text
cliperino/
├── Program.cs                   # Startup, single instance and --probe mode
├── Cliperino.csproj             # .NET 10 project and publish settings
├── Core/                        # Shared base
├── Ffmpeg/                      # ffmpeg handling
├── Capture/                     # Video
├── Audio/                       # Sound
├── UI/                          # Interface
└── Update/                      # Updates
```

## How it works
- **Video:** ffmpeg records 2 s MPEG-TS segments to `%LOCALAPPDATA%\Cliperino\buffer`. Cliperino deletes the ones that fall outside the replay window.
- **Audio:** the audio (from the desktop or the game) and the optional microphone are kept in circular buffers in RAM, aligned with the system clock.
- **Sync:** when saving, the audio is aligned with the video timestamps (PTS). That way it stays in sync even when there are gaps, for example while the game is minimized.
- **Saving:** the segments are joined without re-encoding, the audio is encoded to AAC (mixing in the microphone if it's on) and the `.mp4` is written.
- The ffmpeg processes are tied to a Job Object, so if Cliperino closes or crashes, ffmpeg closes with it.

## Limitations
- Only one global shortcut per key combination. If the NVIDIA overlay uses Alt+F10, Cliperino can't use it. If the shortcut is already taken, the app lets you know.
- In screen mode, some fullscreen games may not show up (you get the desktop instead) and DRM-protected content may come out black. For games, game mode works better.
- On HDR displays colors may look washed out.
- The on-screen notice doesn't show over games in exclusive fullscreen; it does in borderless or windowed mode, which is how most modern games run. It never shows up in clips.
- The start of the clip is accurate to about 2 s (it's cut by segments without re-encoding).
- If the audio is out of sync, adjust it with "Audio offset" in Settings.
