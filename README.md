# PullWatch

PullWatch is a lightweight World of Warcraft gameplay recorder that automatically records Mythic+ runs using combat-log events.

The current goal is reliable Mythic+ recording without requiring OBS or another external recording application.

## Current Status

PullWatch currently:

- Reads and tails the latest WoW combat log.
- Starts recording on `CHALLENGE_MODE_START`.
- Stops recording on `CHALLENGE_MODE_END`.
- Captures the World of Warcraft window at its current resolution.
- Records system audio without microphone input.
- Uses hardware-accelerated H.264 encoding at 60 FPS.
- Finalizes active recordings when PullWatch exits.
- Logs recording startup, duration, finalization time, file size, and failures.

Recordings are saved to:

```text
Videos/PullWatch
```

## How It Works

```text
WoW Combat Log
    -> CombatLogReader
    -> CombatLogEventHandler
    -> ScreenRecordingService
    -> MP4 recording
```

PullWatch currently handles:

- `CHALLENGE_MODE_START`
- `CHALLENGE_MODE_END`

Other WoW events are defined for future encounter and raid support.

## Requirements

### Building

- Windows x64
- .NET 10 SDK

### Running

A framework-dependent build requires the .NET 10 runtime.

A self-contained release does not require a separately installed .NET runtime. It still requires:

- Windows x64
- Visual C++ Redistributable x64
- Windows Media Foundation

## Dependencies

- [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib)
- [Microsoft.Extensions.Logging](https://learn.microsoft.com/dotnet/core/extensions/logging)

PullWatch does not require OBS or another external recording application.

## Running Locally

Update the WoW log directory in `PullWatch/Program.cs`:

```csharp
var logsDirectory = @"E:\World of Warcraft\_retail_\Logs";
```

Run a Release build:

```powershell
dotnet run --project PullWatch -c Release -p:Platform=x64
```

## Publishing

Create a self-contained Windows x64 release:

```powershell
dotnet publish PullWatch/PullWatch.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true
```

Users can run the resulting `PullWatch.exe` without installing .NET 10.

## Current Recording Configuration

- World of Warcraft window capture
- H.264 hardware encoding
- 60 FPS
- 12 Mbps target bitrate
- System audio enabled
- Microphone disabled
- Cursor capture enabled
- Windows capture border disabled

These values are initial defaults and will become configurable later.

## Current Scope

- Mythic+ recording
- Latest combat-log file monitoring
- System output audio capture
- Command-line operation with initial recording defaults

## Roadmap

### Next

- Test reliability across real Mythic+ runs.
- Make the WoW log directory configurable.
- Make the recording output folder configurable.
- Handle new combat-log files created while PullWatch is running.
- Make bitrate, frame rate, encoder, and audio settings configurable.
- Improve recording failure recovery.
- Add focused automated tests.

### Later

- Add a desktop GUI.
- Investigate capturing audio from World of Warcraft only.
- Add recording metadata and improved filenames.
- Investigate raid encounter recording with pre-roll.
- Add recording history and playback management.
