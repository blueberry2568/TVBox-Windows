# Changelog

## 1.0.1 - 2026-07-29

### Fixed

- Moved runtime data to `%LOCALAPPDATA%\TVBox for Windows` and completed the internal namespace rename.
- Removed the MSI launch condition that incorrectly rejected supported Windows 11 systems.
- Published distinct `1.0.1` package names so cached `1.0.0` installers cannot be mistaken for the corrected build.

## 1.0.0 - 2026-07-29

### Added

- Native WinUI 3 desktop shell with retained state for each navigation section.
- TVBox/CatVod JSON, CatPawOpen Node subscriptions, Jint JavaScript spiders, and live playlists.
- Flyleaf/FFmpeg playback with subtitles, danmaku, playback speed, aspect modes, full screen, and resizable picture-in-picture.
- Cross-site search with site tabs or collapsible grouped results.
- Clean portable ZIP and WiX MSI release pipelines with user-data and secret scanning.

### Fixed

- Preserved maximized window geometry and sidebar state across full-screen and picture-in-picture transitions.
- Removed compact navigation overlay and full-screen frame flashes.
- Kept grouped search headers full width when collapsed.
- Made configuration reload transactional and Node runtime replacement non-destructive.
- Prevented stale live-load results and canceled WebView sniff sessions from overwriting or leaking resources.
- Paused hidden playback timers and refreshed favorites/history only when their data changes.
- Restricted the local server to loopback by default, bounded uploads, and confined file endpoints to app data.
- Replaced the GPL FFmpeg bundle with a verified LGPL-3.0-or-later shared build and bundled provenance files.
- Corrected Windows Installer version detection so Windows 10 and 11 are not rejected as unsupported.

### Packaging

- First supported architecture: Windows x64.
- Minimum operating system: Windows 10 version 1809 (build 17763).
- Release binaries are currently unsigned.
