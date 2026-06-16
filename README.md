# PullWatch

PullWatch is a lightweight Windows desktop application that automatically records
World of Warcraft Mythic+ runs and raid encounters using combat-log events.

The current goal is reliable Mythic+ recording without requiring OBS or another external recording application.

## Current Status

PullWatch currently:

- Reads and tails the latest WoW combat log.
- Starts recording on `CHALLENGE_MODE_START`.
- Stops recording on `CHALLENGE_MODE_END`.
- Starts and stops raid encounter recordings using `ENCOUNTER_START` and
  `ENCOUNTER_END`.
- Captures the World of Warcraft window at its current resolution.
- Supports configurable system-audio and microphone capture.
- Uses hardware-accelerated H.264 encoding at 60 FPS.
- Finalizes active recordings when PullWatch exits.
- Logs recording startup, duration, finalization time, file size, and failures.
- Provides a WPF dashboard, settings, diagnostics, and system tray controls.

Recordings are saved to:

```text
Videos/PullWatch
```

## How It Works

```text
WoW Combat Log
    -> CombatLogReader
    -> CombatLogEventHandler
    -> RecordingCoordinator
    -> ScreenRecordingService
    -> MP4 recording
```

PullWatch currently handles:

- `CHALLENGE_MODE_START`
- `CHALLENGE_MODE_END`
- `ENCOUNTER_START`
- `ENCOUNTER_END`

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

Create `%LocalAppData%\PullWatch\settings.json` to configure PullWatch:

```json
{
  "Version": 1,
  "WowLogsDirectory": "E:\\World of Warcraft\\_retail_\\Logs",
  "RecordingsDirectory": "D:\\Videos\\PullWatch",
  "RecordMythicPlus": true,
  "RecordRaidEncounters": true,
  "Video": {
    "Bitrate": 12000000,
    "FrameRate": 60,
    "CaptureCursor": true,
    "ShowCaptureBorder": false
  },
  "Audio": {
    "CaptureSystemAudio": true,
    "CaptureMicrophone": false
  }
}
```

When no file exists, PullWatch creates it with defaults and attempts to detect
the WoW retail logs directory. An unreadable or invalid file is rejected as a
whole and defaults are used without overwriting the file.

Run a Release build:

```powershell
dotnet run --project PullWatch.App/PullWatch.App.csproj -c Release -p:Platform=x64
```

## Testing

Run the fast automated test suite:

```powershell
dotnet test PullWatch.sln
```

## Publishing

Create a self-contained Windows x64 release:

```powershell
dotnet publish PullWatch.App/PullWatch.App.csproj `
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

These values are the defaults used when settings are not configured.

## Current Scope

- Mythic+ recording
- Raid encounter recording
- Latest combat-log file monitoring
- Configurable system-output and microphone capture
- WPF dashboard, settings, diagnostics, and tray controls
- Single-instance desktop operation

## Roadmap

### Next

- Harden and package the first self-contained desktop release.
- Improve recovery from malformed combat-log events.
- Add release automation and continuous integration.

### Later

- Add user-friendly encoder selection.
- Add launch-with-Windows configuration.
- Investigate capturing audio from World of Warcraft only.
- Add recording history and playback management.
