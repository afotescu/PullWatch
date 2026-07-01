# Privacy

PullWatch is a local desktop recorder. It does not include telemetry, analytics,
cloud sync, or any upload feature.

PullWatch may contact GitHub Releases to check whether a newer version is
available. It does not download update packages until you choose to update.

PullWatch records only the World of Warcraft window. Recording starts when:

- you press manual start, or
- an enabled automatic recording rule sees a supported Mythic+ or raid combat-log
  event.

PullWatch does not continuously record in the background. When no
recording is active, it watches for the WoW process and reads the configured WoW
combat-log folder so it can detect supported encounters.

Recordings are saved locally to your configured recordings folder. Settings and
the recording catalog are stored under `%LOCALAPPDATA%\PullWatch`.

Diagnostics can include local file paths, WoW window details, recent application
log messages, and selected settings. Review copied or exported diagnostics before
sharing them publicly.
