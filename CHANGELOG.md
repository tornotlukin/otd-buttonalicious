# Changelog

## 0.3.0 — 2026-05-21

- Added `Pen Tap` as a fourth option in the `Button` dropdown. When selected, the click is synthesized on the pen channel (via VMulti's writable HID interface) instead of through Win32 `SendInput` on the mouse channel.
  - **Why:** Photoshop and other Windows Ink apps maintain separate click counters for pen-channel and mouse-channel events. A real pen tap followed by a synthetic mouse click does *not* register as a double-click, which prevented `Left` + `HoldAndTap` + `DoubleClick` from selecting a word in a Photoshop text field.
  - **How:** in HoldAndTap mode, the real pen tap counts as click 1; the watcher captures its screen position and pressure, then after `Gap Between Clicks` emits a single synthetic pen-channel tap at the same position with the same pressure. Photoshop's pen-channel click counter pairs them and the double-click registers.
  - In Immediate mode, `Pen Tap` synthesizes the full N-tap sequence at the current cursor position.
- Plugin now opens its own HidSharp stream to VMulti's writable interface (VID `0x00FF`, PID `0xBACC`). No bundled HidSharp DLL — relies on the copy OpenTabletDriver's daemon has already loaded.
- Renamed in-OTD display names so they don't collide with other plugins' generic "mouse click" labels:
  - `Mouse Click` → `Buttonalicious Click`
  - `Mouse Click Tap Watcher` → `Buttonalicious Hold-and-Tap Watcher`
- Added `metadata.json` next to the DLL so OpenTabletDriver's Plugin Manager UI displays the plugin's name, description, and version when installed manually.
- README clarified to explain both install paths (Plugin Manager vs manual) and the four `Button` options.

Acknowledgement: the pen-channel report format, VMulti device fingerprint, and the in-range/press/out-of-range sequence pattern are derived from X9VoiD's [VoiDPlugins](https://github.com/X9VoiD/VoiDPlugins) (GPL-3.0-only).

## 0.1.0 — 2026-05-17

Initial release.

- `Mouse Click` binding (`IStateBinding`): single or double click on press, configurable mouse button (Left/Right/Middle), configurable click hold and inter-click gap.
- `Mouse Click Tap Watcher` filter (`IPositionedPipelineElement<IDeviceReport>`): cooperates with the binding to enable Wacom-style hold-and-tap — pen button arms the click, the next tip tap fires it at the tip position.
- HoldAndTap supports a "Multiple Taps Per Arm" toggle for classic-style (every tap fires) vs modern-style (one tap then re-arm) behavior.
- All clicks emitted via Win32 `SendInput`, bypassing OpenTabletDriver's output mode pipeline — works under `Windows Ink Absolute Mode` where the built-in `Mouse Button` binding does not fire.
- Targets OpenTabletDriver 0.6.7 / .NET 8.
