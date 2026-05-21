# Buttonalicious

An [OpenTabletDriver](https://opentabletdriver.net/) plugin that adds proper **mouse-button clicking** (and pen-channel tap synthesis) to your pen barrel buttons when you're using the Windows Ink output mode. Single click, double click, Wacom-style hold-and-tap, and a `Pen Tap` mode that synthesizes pen-channel clicks for Windows Ink apps that won't coalesce pen + mouse events into a double-click — all the click flavors a graphics-tablet user actually wants.

Built and tested against OpenTabletDriver 0.6.7 on Windows 11.

## Why this exists

When OTD's active output mode is `Windows Ink Absolute Mode` (from the `Windows Ink` plugin), OTD's built-in `Mouse Button` binding doesn't fire. Windows Ink only handles pen-protocol events — it doesn't implement OTD's `IMouseButtonHandler` interface, so any mouse-channel binding routed through it goes nowhere. Buttonalicious sidesteps the output mode entirely by calling Win32 `SendInput` directly. Mouse clicks land in Windows like they came from a real mouse.

A second perk: a "hold-and-tap" mode, the Wacom-style behavior where you hold a pen button to **arm** a click, then tap the pen tip to fire that click at exactly the tip's position. Useful when you want a precisely placed right-click or double-click.

A third (added in 0.3.0): a `Pen Tap` button option that synthesizes the click on the **pen** channel instead of the mouse channel — via VMulti's writable HID interface, the same path the Windows Ink output mode uses for real pen events. This is the right tool for double-clicking words in Photoshop text fields, where Photoshop maintains separate click counters for pen and mouse channels and won't pair a real pen tap with a synthetic mouse click.

## What's in the box

The plugin DLL exposes two `[PluginName]`-decorated classes:

| In OTD's GUI | Type | Where to set it |
|--------------|------|-----------------|
| **Buttonalicious Click** | Binding | Bindings tab → click a Pen Button slot → pick "Mouse Click" |
| **Buttonalicious Hold-and-Tap Watcher** | Filter | Filters tab → add "Mouse Click Tap Watcher" → enable |

The filter is **only required for HoldAndTap mode**. For everything else, install the binding and ignore the filter.

## Properties on the `Buttonalicious Click` binding

| Property | Type | Default | Notes |
|----------|------|---------|-------|
| Button | dropdown | `Left` | `Left`, `Right`, `Middle`, or `Pen Tap`. The first three go through Win32 `SendInput` (mouse channel). `Pen Tap` goes through VMulti's pen channel — use this for Photoshop text-field double-clicks. |
| Double Click | checkbox | unchecked | Checked: emit two clicks. Unchecked: single click. For `Pen Tap` + `HoldAndTap`, the real pen tap counts as click 1, so this controls whether we synthesize a second pen tap. |
| Mode | dropdown | `Immediate` | `Immediate` or `HoldAndTap` |
| Click Hold (ms) | int | `10` | How long each click is held down. 10 ms is the safe minimum. |
| Gap Between Clicks (ms) | int | `80` | Delay between the two clicks. For `Pen Tap` + `HoldAndTap`, the delay starts at tip-up and counts until the synthesized tap. Well inside Windows' ~500 ms double-click window. |
| Multiple Taps Per Arm | checkbox | unchecked | HoldAndTap only. If checked: every tip tap while the pen button is held fires another click. If unchecked: only the first tap per arm fires, then auto-disarms. |

## Behaviors at a glance

| Mode | Button | Double Click | What happens |
|------|--------|--------------|--------------|
| Immediate | Left / Right / Middle | unchecked | Press the pen button → one mouse click at the cursor. |
| Immediate | Left / Right / Middle | checked | Press the pen button → a real mouse double-click at the cursor. |
| Immediate | Pen Tap | unchecked | Press the pen button → one synthesized pen tap at the cursor. |
| Immediate | Pen Tap | checked | Press the pen button → two synthesized pen taps at the cursor. |
| HoldAndTap | Left / Right / Middle | unchecked | Hold the pen button → next tip tap fires one mouse click *at the tip position*. |
| HoldAndTap | Left / Right / Middle | checked | Hold the pen button → next tip tap fires a mouse double-click *at the tip position*. |
| HoldAndTap | Pen Tap | unchecked | Hold the pen button → next tip tap is the click. Nothing extra synthesized. |
| HoldAndTap | Pen Tap | checked | Hold the pen button → real pen tap = click 1, synthesized pen tap = click 2, both at the same position and pressure. Photoshop's pen-channel double-click. |

In HoldAndTap modes, "Multiple Taps Per Arm" decides whether every tap while held re-fires, or only the first.

## Install

**Option A — through OpenTabletDriver's Plugin Manager (recommended, once listed):**

Plugins menu → Open Plugin Manager → search "Buttonalicious" → Install. OTD downloads the release, extracts it, and writes the `metadata.json` automatically.

**Option B — manual install:**

1. Download the latest release ZIP from the [Releases](../../releases) page.
2. Extract its contents into your OpenTabletDriver plugins folder:

   `%LOCALAPPDATA%\OpenTabletDriver\Plugins\Buttonalicious\`

   The folder should end up containing both `Buttonalicious.dll` and `metadata.json`. The DLL is what OTD actually loads; the metadata.json is what OTD's Plugin Manager UI uses to display the plugin's name, description, and version. If you only have the DLL, the plugin still works — you just won't see info about it in the Plugin Manager list.

3. Restart OpenTabletDriver.

**Then configure it:**

- **Bindings** tab → click a Pen Button slot → pick **Buttonalicious Click** → set the properties you want → **Apply**.
- If you want HoldAndTap, also enable **Buttonalicious Hold-and-Tap Watcher** in the **Filters** tab.

## Build from source

Requires the .NET 8 SDK. Restores `OpenTabletDriver.Plugin` 0.6.7 via NuGet — no local DLL setup needed.

```powershell
dotnet build -c Release
```

Output: `bin\Release\net8.0\Buttonalicious.dll`

To install your local build straight into OTD's plugins folder:

```powershell
$src = "bin\Release\net8.0\Buttonalicious.dll"
$dst = "$env:LOCALAPPDATA\OpenTabletDriver\Plugins\Buttonalicious"
New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item $src $dst -Force
Stop-Process -Name "OpenTabletDriver*" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Start-Process "$env:LOCALAPPDATA\OpenTabletDriver\OpenTabletDriver.UX.Wpf.exe"
```

## How it works

### Immediate mode
The binding implements `IStateBinding`. On Press it emits the click(s):
- Mouse channel (`Left` / `Right` / `Middle`): Win32 `SendInput` mouse-down / sleep / mouse-up sequence.
- Pen channel (`Pen Tap`): a 4-report sequence written to VMulti — InRange → InRange|Press(pressure) → InRange(P=0) → out-of-range — at the current cursor position (captured via `GetCursorPos`).

Either path bypasses OTD's output mode entirely, so it works regardless of whether you're on Windows Ink Absolute, VMultiMode, or anything else.

### HoldAndTap mode
Two components cooperate via a shared static `ClickState`:

1. **Binding** (on the pen button) — on Press, sets `Armed = true`, snapshots the current tip pressure state and the tablet's max pressure spec. On Release, sets `Armed = false`.
2. **Filter** (`Buttonalicious Hold-and-Tap Watcher`) — subscribes to every device report through OTD's pipeline. On tip-down (pressure 0 → >0) while armed:
   - Mouse channel: fire `SendInput` clicks immediately.
   - Pen channel (`Pen Tap`): capture the current screen position and raw pressure; defer the synthesized follow-up tap until tip-up + `Gap Between Clicks` ms.

The "snapshot current tip state on Press" detail matters: pressing the button while the pen is already touching the tablet doesn't immediately fire — you have to lift and tap again. Without that, the binding would fire spuriously when you press the button mid-stroke.

The deferred fire on `Pen Tap` matters: synthesizing a pen-channel tap while OTD's Windows Ink pipeline is still emitting real pen reports would interleave; waiting for tip-up + a short gap puts our writes after OTD's pipeline has gone quiet (pen out of range).

The filter sits at `PipelinePosition.PreTransform` so it observes reports before display-area mapping. It doesn't modify reports — just observes — and forwards them downstream via `Emit?.Invoke(report)`.

### Pen Tap channel
`Pen Tap` opens VMulti's writable HID interface directly (VID `0x00FF`, PID `0xBACC`) via HidSharp — the same path the Windows Ink output mode uses. The pen-channel report format (header + buttons bitfield + X/Y/pressure/tilt) and the in-range / press / out-of-range sequence pattern are derived from [X9VoiD's VoiDPlugins](https://github.com/X9VoiD/VoiDPlugins) (GPL-3.0-only).

## Notes / known limits

- **For HoldAndTap to work, the watcher filter must be enabled.** If you set Mode = HoldAndTap on the binding but forget to add the filter in the Filters tab, the button press will arm the flag and nothing else. Symptom: nothing happens. Symptom of the fix: enable the filter, click Apply.
- **HoldAndTap fires at the tip's position, not the cursor's.** With Windows Ink Absolute these are normally the same. With non-absolute output modes they may differ.
- **`SendInput` ignores OTD's output mode entirely.** Mouse events are emitted at the OS level. If you want a pen-class double-click in a Windows Ink app, use `Pen Tap` for the Button instead — it goes through the pen channel via VMulti.
- **`Pen Tap` requires VMulti.** If VMulti isn't installed, the plugin logs an error on first use and the pen-channel click is a no-op. Install vmulti-bin (the same prerequisite the Windows Ink output mode has).
- **`Pen Tap` does not bundle HidSharp.** The plugin relies on the copy already loaded by OpenTabletDriver's daemon — same setup the Windows Ink plugin uses.
- **Single double-click per trigger** — no triple-click. Adjust source if you need it.

## License

GPL-3.0-only — see [LICENSE](LICENSE).
