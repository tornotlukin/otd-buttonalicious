# Changelog

## Unreleased

- Renamed in-OTD display names so they don't collide with other plugins' generic "mouse click" labels:
  - `Mouse Click` → `Buttonalicious Click`
  - `Mouse Click Tap Watcher` → `Buttonalicious Hold-and-Tap Watcher`
- Added `metadata.json` next to the DLL so OpenTabletDriver's Plugin Manager UI can display the plugin's name, description, and version when installed manually.
- README clarified to explain both install paths (Plugin Manager vs manual).

## 0.1.0 — 2026-05-17

Initial release.

- `Mouse Click` binding (`IStateBinding`): single or double click on press, configurable mouse button (Left/Right/Middle), configurable click hold and inter-click gap.
- `Mouse Click Tap Watcher` filter (`IPositionedPipelineElement<IDeviceReport>`): cooperates with the binding to enable Wacom-style hold-and-tap — pen button arms the click, the next tip tap fires it at the tip position.
- HoldAndTap supports a "Multiple Taps Per Arm" toggle for classic-style (every tap fires) vs modern-style (one tap then re-arm) behavior.
- All clicks emitted via Win32 `SendInput`, bypassing OpenTabletDriver's output mode pipeline — works under `Windows Ink Absolute Mode` where the built-in `Mouse Button` binding does not fire.
- Targets OpenTabletDriver 0.6.7 / .NET 8.
