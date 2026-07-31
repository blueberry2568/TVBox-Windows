# Changelog

## Unreleased

## 1.0.6 - 2026-08-01

### Fixed

- Replaced the fixed playback-window transition delay with native window and WinUI layout stability detection, reducing jitter and flicker when entering or leaving full-screen and picture-in-picture modes.
- Synchronized the final Flyleaf swap-chain size after every presentation change and ignored repeated mode commands while a transition is still settling.

## 1.0.5 - 2026-07-31

### Fixed

- Removed the experimental frozen-window transition that could duplicate the video, expose the desktop, or time out while entering picture-in-picture from a maximized window; playback presentation is back on the stable native presenter and placement path.
- Replaced recycled-card `ContextFlyout` instances on favorites and history pages with page-owned context menus, avoiding a repeatable access violation in `Microsoft.UI.Input.dll` when right-clicking a poster.

## 1.0.4 - 2026-07-31

### Added

- Added a live-player channel picker that reuses the video episode selector icon and right-side overlay interaction.

### Fixed

- Kept live channel selection and channel navigation available while a stream is resolving; choosing another channel now cancels the stale load and starts the latest request.
- Fixed video and live bottom-bar flyout controls freezing or crashing the app when opening expandable menus.
- Hid the video-player back button in picture-in-picture mode while preserving keyboard and bottom-bar exit controls.
- Applied the shared 8-pixel player corner radius in normal and picture-in-picture modes while retaining square, edge-to-edge full-screen playback.
- Added even spacing around the live player in its normal page layout without exposing the underlying surface during picture-in-picture resizing.
- Covered the uncropped Flyleaf swap-chain corner pixels with theme-matched inverse-corner masks in the normal video and live layouts.
- Stabilized the original maximized restore rectangle after leaving picture-in-picture so the system Restore command no longer reuses compact-window bounds.
- Prevented navigation rail glyphs from losing edge pixels after full-screen or picture-in-picture transitions by removing nested fixed-size clipping and stabilizing the restored shell layout.
- Replaced navigation rail font glyphs with inset vector paths and changed shell restoration from repeated 50 ms layout forcing to one debounced final layout pass.
- Isolated picture-in-picture presenter properties, preserved and guarded the shared native restore bounds, and applied one transition to video and live full-screen/picture-in-picture changes.
- Prevented video and live line switching from rebuilding an active WinUI flyout during its click event, eliminating the native input crash.
- Serialized media source transitions and rejected stale resolver, decoder, recovery, and playback callbacks from previous lines.
- Unified search, detail, and player back buttons with one shared icon, size, padding, and spacing specification.
- Removed the extra focus outline around episode cards on the video detail page.
- Removed the nested live-page surface and duplicate title, aligning all primary pages on the shared page padding and title styles.

### Documentation

- Added the GPL-3.0 project license and a README interface screenshot.
- Included the project license in future portable and MSI release layouts.

## 1.0.3 - 2026-07-29

### Fixed

- Made the persisted collapsed navigation mode authoritative at startup and after video or live full-screen transitions.
- Restored maximized windows directly without briefly showing their previous normal bounds.
- Fixed the apphost runtime lookup path that incorrectly resolved `runtime` below the managed entry directory and kept reporting a missing .NET Desktop Runtime even after users installed it.
- Fixed the silent WinUI startup crash by redirecting the embedded registration-free activation manifest to the structured runtime directories before `Application.Start`.
- Replaced the flat self-contained output with a framework-dependent application in `libs` plus a standard private dotnet-root in `runtime`, so the multi-file release starts without a system .NET installation.
- Reorganized Node and FFmpeg as root runtime components, grouped icons and JavaScript under `assets`, and added explicit `libs`, `locales`, and `runtime` classifications.
- Consolidated WinUI language resources under `locales\winui`, retained only Chinese, English, Japanese, and Korean resources, and removed duplicate MUI payload from `libs`.
- Rebuilt the original blue cube artwork on a polished Apple-style rounded-rectangle application icon without changing the cube itself.
- Ensured the portable and installed layouts contain exactly one root `TVBox.exe`; single-file publishing remains disabled.
- Added an installer page for the optional desktop shortcut while retaining the Start menu shortcut and selectable install directory.
- Synchronized executable, assembly, manifest, package, and local status-page versions.
- Hardened portable and MSI validation against preferences, sources, history, favorites, logs, dumps, debug symbols, credentials, and other machine-local files.
- Kept release artifacts stateless while preserving user configuration and history under `%LOCALAPPDATA%\TVBox for Windows` across upgrades.
- Made VOD/live sources empty and every user-facing switch off for a clean first run.
- Made settings, source records, history, and favorites crash-safe with serialized atomic writes and backup recovery.
- Automatically reloads the active CatPawOpen source after its external configuration center saves a stable configuration change.

## 1.0.2 - 2026-07-29

### Fixed

- Added a blocking first-run source setup with required VOD and optional live configuration.
- Reorganized portable and installed files around a visible root launcher and an `app` runtime directory.
- Upgraded to matching Flyleaf FFmpeg bindings and LGPL FFmpeg 8.1 libraries so bundled playback libraries load correctly.
- Added a stable taskbar identity, explicit shortcut icon, and a selectable MSI installation directory.

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
