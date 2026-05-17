# Buttonalicious

An [OpenTabletDriver](https://opentabletdriver.net/) plugin that adds proper **mouse-button clicking** to your pen barrel buttons when you're using Kuuuube's Windows Ink output mode. Single click, double click, and Wacom-style hover-then-tap (hold-and-tap) modes — all the click flavors a graphics-tablet user actually wants, on a pen channel that was designed for inking and forgets it can do anything else.

Built and tested against OpenTabletDriver 0.6.7 on Windows 11.

## Why this exists

When OTD's active output mode is `Windows Ink Absolute Mode` (from the `Windows Ink` plugin), OTD's built-in `Mouse Button` binding doesn't fire. Windows Ink only handles pen-protocol events — it doesn't implement OTD's `IMouseButtonHandler` interface, so any mouse-channel binding routed through it goes nowhere. Buttonalicious sidesteps the output mode entirely by calling Win32 `SendInput` directly. Mouse clicks land in Windows like they came from a real mouse.

A second perk: a "hold-and-tap" mode, the Wacom-style behavior where you hold a pen button to **arm** a click, then tap the pen tip to fire that click at exactly the tip's position. Useful when you want a precisely placed right-click or double-click.

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
| Button | dropdown | `Left` | `Left`, `Right`, or `Middle` |
| Double Click | checkbox | unchecked | Checked: emit two clicks. Unchecked: single click. |
| Mode | dropdown | `Immediate` | `Immediate` or `HoldAndTap` |
| Click Hold (ms) | int | `10` | How long each click is held down. 10 ms is the safe minimum. |
| Gap Between Clicks (ms) | int | `80` | Delay between the two clicks. Only relevant when Double Click is checked. Well inside Windows' ~500 ms double-click window. |
| Multiple Taps Per Arm | checkbox | unchecked | HoldAndTap only. If checked: every tip tap while the pen button is held fires another click. If unchecked: only the first tap per arm fires, then auto-disarms. |

## Behaviors at a glance

| Mode | Double Click | What happens |
|------|--------------|--------------|
| Immediate | unchecked | Press the pen button → one click fires at the cursor. |
| Immediate | checked | Press the pen button → a real double-click fires at the cursor. |
| HoldAndTap | unchecked | Hold the pen button → next tip tap fires one click *at the tip position*. |
| HoldAndTap | checked | Hold the pen button → next tip tap fires a double-click *at the tip position*. |

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
The binding implements `IStateBinding`. On Press, it calls Win32 `SendInput` to emit a mouse-down / sleep / mouse-up sequence — once for single, twice for double-click. Bypasses OTD's output mode entirely, so it works regardless of whether you're on Windows Ink Absolute, VMultiMode, or anything else.

### HoldAndTap mode
Two components cooperate via a shared static `ClickState`:

1. **Binding** (on the pen button) — on Press, sets `Armed = true` and snapshots the current tip pressure state. On Release, sets `Armed = false`.
2. **Filter** (`Mouse Click Tap Watcher`) — subscribes to every device report through OTD's pipeline. When it sees pressure > 0 (tip-down) AND pressure was 0 last frame AND `Armed == true`, it fires the click(s) via the same `SendInput` path.

The "snapshot current tip state on Press" detail matters: pressing the button while the pen is already touching the tablet doesn't immediately fire — you have to lift and tap again. Without that, the binding would fire spuriously when you press the button mid-stroke.

The filter sits at `PipelinePosition.PreTransform` so it observes reports before display-area mapping. It doesn't modify reports — just observes — and forwards them downstream via `Emit?.Invoke(report)`.

## Notes / known limits

- **For HoldAndTap to work, the watcher filter must be enabled.** If you set Mode = HoldAndTap on the binding but forget to add the filter in the Filters tab, the button press will arm the flag and nothing else. Symptom: nothing happens. Symptom of the fix: enable the filter, click Apply.
- **HoldAndTap fires at the tip's position, not the cursor's.** With Windows Ink Absolute these are normally the same. With non-absolute output modes they may differ.
- **`SendInput` ignores OTD's output mode entirely.** Mouse events are emitted at the OS level. If you want a pen-class double-click (Windows Ink pen barrel button), this isn't the right plugin — the built-in WindowsInk plugin's "Button 1" / "Button 2" bindings handle that case.
- **Single double-click per trigger** — no triple-click. Adjust source if you need it.

## License

GPL-3.0-only — see [LICENSE](LICENSE).
