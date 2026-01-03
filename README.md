# Audio Processor And Streamer

A Windows desktop application for capturing audio, processing it through VST plugins, and streaming it via HLS/DASH protocols.

## Features

- **Audio Capture**
  - WASAPI (Windows Audio Session API) support for capturing system audio
  - ASIO driver support for low-latency professional audio interfaces
  - Multiple simultaneous audio streams

- **VST Plugin Processing**
  - Load and chain multiple VST 2.x plugins
  - Real-time audio processing before encoding
  - Plugin preset management

- **Streaming Formats**
  - HLS (HTTP Live Streaming) with hls.js player
  - DASH (Dynamic Adaptive Streaming over HTTP) with dash.js player
  - Adaptive bitrate streaming with multiple quality profiles

- **Container Formats**
  - fMP4 (Fragmented MP4) for modern browsers
  - MPEG-TS for legacy compatibility

- **Audio Codecs**
  - AAC (Advanced Audio Coding)
  - MP3
  - Opus

- **Built-in Web Server**
  - Serves HLS/DASH manifests and segments
  - Built-in streams overview page
  - Configurable port and domain

- **Real-time Visualization**
  - Input/output spectrum analyzers
  - Audio level monitoring

- **Monitor Output**
  - Route processed audio to a local output device for monitoring

## Requirements

- Windows 10/11 (64-bit)
- FFmpeg (included in installer or place in `FFmpeg/bin/` folder)
- VST 2.x plugins (optional, place in `Plugins/` folder)

## Installation

### Using the Installer

1. Download the latest release from the [Releases](../../releases) page
2. Run `AudioProcessorAndStreamer-Setup-x.x.x.exe`
3. Follow the installation wizard

The installer includes all dependencies - no .NET runtime installation required.

### Manual Installation

1. Download and extract the release archive
2. Ensure FFmpeg binaries are in the `FFmpeg/bin/` folder
3. Run `AudioProcessorAndStreamer.exe`

## Usage

### Quick Start

1. Launch the application
2. Click **Settings** to configure your first stream:
   - Select an audio input device (WASAPI or ASIO)
   - Choose encoding profiles (bitrates)
   - Optionally add VST plugins for processing
3. Click **Start** on the web server
4. Click **Start** on your stream
5. Click the URL displayed to open the streams page in your browser

### Configuration

Access settings via the **Settings** button:

| Setting | Description |
|---------|-------------|
| Web Server Port | HTTP port for the streaming server (default: 8080) |
| Base Domain | Public URL for stream access |
| Streams Page Path | URL path to the streams overview page |
| Segment Duration | HLS/DASH segment length in seconds (default: 2) |
| Playlist Size | Number of segments in playlist (default: 5) |
| Stream Format | HLS or DASH |
| Container Format | fMP4 or MPEG-TS (HLS only) |

### Stream Configuration

Each stream can be configured with:

- **Audio Input**: Select WASAPI loopback, WASAPI device, or ASIO device
- **Sample Rate**: 44100, 48000, 96000 Hz
- **Buffer Size**: 256-4096 samples
- **Encoding Profiles**: Multiple bitrate options (64-320 kbps)
- **VST Plugins**: Chain multiple plugins for processing
- **Stream Path**: Custom URL path for the stream

## Building from Source

### Prerequisites

- Visual Studio 2022 or later
- .NET 8.0 SDK
- Inno Setup 6 (for building installer)

### Build Commands

```bash
# Build for development
dotnet build -c Debug -p:Platform=x64

# Build for release
dotnet build -c Release -p:Platform=x64

# Publish self-contained single-file executable
dotnet publish -c Release -p:Platform=x64

# Build installer (requires Inno Setup)
build-installer.bat
```

### Publish installer
```powershell
  # Build with current version
  .\build-installer.ps1

  # Update version to 1.0.0 and build
  .\build-installer.ps1 -Version 1.0.0

  # Only create installer (skip publish step)
  .\build-installer.ps1 -SkipBuild
```

### Auto Updates

autoupdates.json

```json
  {
    "version": "0.9.8",
    "downloadUrl": "https://www.mahn.it/software/audioprocessorandstreamer/AudioProcessorAndStreamer-Setup-0.9.8.exe",
    "releaseNotes": "Added improved debug logging"
  }
```

### Project Structure

```
AudioProcessorAndStreamer/
├── Models/              # Data models and configuration
├── ViewModels/          # MVVM view models
├── Views/               # WPF views and dialogs
├── Services/
│   ├── Audio/           # WASAPI/ASIO capture services
│   ├── Encoding/        # FFmpeg encoding management
│   ├── Streaming/       # Stream processing pipeline
│   ├── Vst/             # VST plugin hosting
│   └── Web/             # HLS web server
├── Infrastructure/      # Utilities and helpers
├── Assets/              # Images and resources
├── FFmpeg/              # FFmpeg binaries
└── Plugins/             # VST plugins folder
```

## Technology Stack

- **.NET 8.0** - Application framework
- **WPF** - User interface
- **CommunityToolkit.Mvvm** - MVVM implementation
- **NAudio** - Audio capture (WASAPI/ASIO)
- **VST.NET** - VST plugin hosting
- **FFmpeg** - Audio encoding
- **ASP.NET Core** - Embedded web server
- **hls.js / dash.js** - Browser-based players

## License

MIT License - See [LICENSE](LICENSE) file for details.

## Acknowledgments

- [NAudio](https://github.com/naudio/NAudio) - Audio library for .NET
- [VST.NET](https://github.com/obiwanjacobi/vst.net) - VST plugin hosting
- [FFmpeg](https://ffmpeg.org/) - Multimedia framework
- [hls.js](https://github.com/video-dev/hls.js) - HLS client library
- [dash.js](https://github.com/Dash-Industry-Forum/dash.js) - DASH client library
