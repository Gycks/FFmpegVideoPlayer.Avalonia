[![NuGet](https://img.shields.io/nuget/v/FFmpegVideoPlayer.Avalonia.svg)](https://www.nuget.org/packages/FFmpegVideoPlayer.Avalonia/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

# FFmpegVideoPlayer.Avalonia

FFmpeg-based media player control for Avalonia, with video and audio-only playback. Works on Windows, macOS, and Linux (including Apple Silicon).

![Preview](https://raw.githubusercontent.com/jojomondag/FFmpegVideoPlayer.Avalonia/main/images/Preview1.png)

## Install

```bash
dotnet add package FFmpegVideoPlayer.Avalonia
```

Requires **Avalonia 11.3.15+** and **.NET 8+**.

## Quick start

**1. Initialize FFmpeg** (e.g. in `Program.cs` before the app starts):

```csharp
using FFmpegVideoPlayer.Core;

FFmpegInitializer.Initialize();
```

**2. Add the control in XAML:**

```xml
xmlns:ffmpeg="clr-namespace:Avalonia.FFmpegVideoPlayer;assembly=Avalonia.FFmpegVideoPlayer"

<ffmpeg:VideoPlayerControl Source="C:\path\to\video.mp4" ShowControls="True" />
```

Video and audio-only files are both supported, for example `.mp4`, `.mov`, `.mp3`, `.wav`, `.flac`, `.ogg`, and `.m4a`.

Audio (OpenAL) is included in the main package. On Windows, install [OpenAL Soft](https://www.openal-soft.org/) or place `OpenAL32.dll` next to your app — otherwise playback is video-only.

## FFmpeg

| Platform | Default |
|----------|---------|
| Windows x64 | Bundled DLLs in the package |
| macOS | System/Homebrew (`brew install ffmpeg`); can auto-install |
| Linux | System packages (apt/dnf/pacman); can auto-install with passwordless sudo |

Custom FFmpeg path:

```csharp
FFmpegInitializer.Initialize(customPath: @"C:\ffmpeg\bin", useBundledBinaries: false);
```

Subscribe to `StatusChanged` for progress during discovery or auto-install.

## Example

```bash
git clone https://github.com/jojomondag/FFmpegVideoPlayer.Avalonia.git
cd FFmpegVideoPlayer.Avalonia/examples/FFmpegVideoPlayerExample
dotnet run
```

## VideoPlayerControl

**Properties:** `Source`, `AutoPlay`, `Volume`, `ShowControls`, `ShowOpenButton`, `VideoStretch`, `VideoBackground`, `EnableKeyboardShortcuts`, `RenderingMode` (`Cpu` / `OpenGL`), `AudioPlayerFactory`, `IconProvider`

**Methods:** `Open`, `OpenUri`, `Play`, `Pause`, `Stop`, `TogglePlayPause`, `Seek`, `ToggleMute`

**Shortcuts:** Space (play/pause), arrow keys (seek/volume), M (mute)

Set `AudioPlayerFactory` to customize or disable audio. OpenAL via OpenTK is the default.

Custom icons: implement `IIconProvider` and set `IconProvider`.

## License

MIT
