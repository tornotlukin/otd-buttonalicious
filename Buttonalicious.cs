using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace Buttonalicious;

// Shared state between the IStateBinding (pen button) and the
// IPositionedPipelineElement (tip watcher). Both classes run on different
// OTD threads; volatile + simple atomic writes are enough — worst case is a
// single stale frame on disarm, sub-millisecond and harmless.
internal static class ClickState
{
    public static volatile bool   Armed;
    public static volatile int    ActiveButtonIndex; // 0=Left, 1=Right, 2=Middle, 3=Pen Tap
    public static volatile int    ClickHoldMs = 10;
    public static volatile int    InterClickGapMs = 80;
    public static volatile bool   DoubleClick;
    public static volatile bool   MultipleTapsPerArm;
    public static volatile bool   LastTipPressed;

    // For Pen Tap mode: captured from the user's real pen tap so the
    // synthetic follow-up tap matches its position and pressure.
    public static volatile int    PenTapScreenX;
    public static volatile int    PenTapScreenY;
    public static          uint   PenTapRawPressure;     // raw from ITabletReport
    public static          uint   PenTapMaxPressure = 1; // from TabletReference at arm time
    public static volatile bool   PenTapPendingFire;     // armed at tip-down, consumed at tip-up

    public static void EmitClick(bool doubled)
    {
        var (down, up) = ActiveButtonIndex switch
        {
            1 => (Win32.MOUSEEVENTF_RIGHTDOWN,  Win32.MOUSEEVENTF_RIGHTUP),
            2 => (Win32.MOUSEEVENTF_MIDDLEDOWN, Win32.MOUSEEVENTF_MIDDLEUP),
            _ => (Win32.MOUSEEVENTF_LEFTDOWN,   Win32.MOUSEEVENTF_LEFTUP),
        };

        Win32.SendMouseFlag(down);
        Thread.Sleep(ClickHoldMs);
        Win32.SendMouseFlag(up);

        if (doubled)
        {
            Thread.Sleep(InterClickGapMs);
            Win32.SendMouseFlag(down);
            Thread.Sleep(ClickHoldMs);
            Win32.SendMouseFlag(up);
        }
    }

    public static void EmitPenTapAtCaptured()
    {
        var pen = VMultiPen.Instance;
        if (pen == null) return;

        ushort vmultiPressure;
        if (PenTapMaxPressure == 0)
        {
            vmultiPressure = (ushort)(pen.MaxPressure * 0.6);
        }
        else
        {
            double pct = Math.Clamp((double)PenTapRawPressure / PenTapMaxPressure, 0.05, 1.0);
            vmultiPressure = (ushort)(pct * pen.MaxPressure);
        }

        pen.EmitTap(PenTapScreenX, PenTapScreenY, vmultiPressure, ClickHoldMs);
    }
}

internal static class Win32
{
    public const uint INPUT_MOUSE            = 0;
    public const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int    Dx;
        public int    Dy;
        public uint   MouseData;
        public uint   Flags;
        public uint   Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint   Flags;
        public uint   Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint   uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT    Mouse;
        [FieldOffset(0)] public KEYBDINPUT    Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct INPUT
    {
        public uint       Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    public static void SendMouseFlag(uint flags)
    {
        var input = new INPUT
        {
            Type = INPUT_MOUSE,
            Data = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags } }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}

[PluginName("Buttonalicious Click")]
public class MouseClickBinding : IStateBinding
{
    [Property("Button"),
     DefaultPropertyValue("Left"),
     PropertyValidated(nameof(ValidButtons)),
     ToolTip("Which click to emit.\nLeft / Right / Middle: mouse-channel click via SendInput.\nPen Tap: pen-channel tap synthesized through VMulti. Use this for double-clicking words in Photoshop text fields where mouse + pen clicks don't coalesce.")]
    public string Button { get; set; } = "Left";

    [BooleanProperty("Double Click", ""),
     ToolTip("Checked: emit two clicks (with timing controlled below).\nUnchecked: emit one click.\nFor Pen Tap + HoldAndTap: the real pen tap is click 1, this checkbox controls whether we synthesize a second pen tap after Gap Between Clicks.")]
    public bool DoubleClick { get; set; }

    [Property("Mode"),
     DefaultPropertyValue("Immediate"),
     PropertyValidated(nameof(ValidModes)),
     ToolTip("Immediate: click(s) fire the moment the pen button is pressed, at the current cursor position.\nHoldAndTap: pen button arms the click; the next tip tap fires it at the tip position. HoldAndTap requires the 'Buttonalicious Hold-and-Tap Watcher' filter to be enabled in the Filters tab.")]
    public string Mode { get; set; } = "Immediate";

    [Property("Click Hold (ms)"),
     DefaultPropertyValue(10),
     Unit("ms"),
     ToolTip("How long each individual click is held down. 10 ms is the safe minimum.")]
    public int ClickHoldMs { get; set; } = 10;

    [Property("Gap Between Clicks (ms)"),
     DefaultPropertyValue(80),
     Unit("ms"),
     ToolTip("Delay between the two clicks when Double Click is checked. Windows double-click recognition window is ~500 ms; 80 ms is well inside it.")]
    public int InterClickGapMs { get; set; } = 80;

    [BooleanProperty("Multiple Taps Per Arm", ""),
     ToolTip("HoldAndTap only. If checked: every tip tap while the pen button is held fires another click (or double-click). If unchecked: only the first tap per pen-button-press fires, then auto-disarms.")]
    public bool MultipleTapsPerArm { get; set; }

    public static string[] ValidModes   => new[] { "Immediate", "HoldAndTap" };
    public static string[] ValidButtons => new[] { "Left", "Right", "Middle", "Pen Tap" };

    public void Press(TabletReference tablet, IDeviceReport report)
    {
        try
        {
            ClickState.ActiveButtonIndex  = Button switch
            {
                "Right"   => 1,
                "Middle"  => 2,
                "Pen Tap" => 3,
                _         => 0,
            };
            ClickState.ClickHoldMs        = ClickHoldMs;
            ClickState.InterClickGapMs    = InterClickGapMs;
            ClickState.DoubleClick        = DoubleClick;
            ClickState.MultipleTapsPerArm = MultipleTapsPerArm;

            // Snapshot tablet's max pressure so the watcher can scale the
            // captured raw pressure to VMulti's range later.
            try
            {
                ClickState.PenTapMaxPressure = tablet?.Properties?.Specifications?.Pen?.MaxPressure ?? 8191u;
                if (ClickState.PenTapMaxPressure == 0) ClickState.PenTapMaxPressure = 8191u;
            }
            catch { ClickState.PenTapMaxPressure = 8191u; }

            if (Mode == "HoldAndTap")
            {
                // Snapshot current tip state so we only fire on FUTURE tip
                // transitions — not when the user is already touching the
                // tablet when they hit the button.
                ClickState.LastTipPressed = report is ITabletReport t && t.Pressure > 0;
                ClickState.Armed = true;
            }
            else
            {
                // Immediate: fire at current cursor position.
                if (ClickState.ActiveButtonIndex == 3)
                {
                    Win32.GetCursorPos(out var pt);
                    ClickState.PenTapScreenX = pt.X;
                    ClickState.PenTapScreenY = pt.Y;
                    // Immediate pen-tap has no real tap to mirror, so pick a
                    // safe pressure: 60% of VMulti max via the helper's fallback.
                    ClickState.PenTapRawPressure = 0;
                    ClickState.PenTapMaxPressure = 0;
                    ClickState.EmitPenTapAtCaptured();
                    if (DoubleClick)
                    {
                        Thread.Sleep(InterClickGapMs);
                        ClickState.EmitPenTapAtCaptured();
                    }
                }
                else
                {
                    ClickState.EmitClick(DoubleClick);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception(ex);
        }
    }

    public void Release(TabletReference tablet, IDeviceReport report)
    {
        if (Mode == "HoldAndTap")
        {
            ClickState.Armed = false;
            ClickState.PenTapPendingFire = false;
        }
    }
}

[PluginName("Buttonalicious Hold-and-Tap Watcher")]
public class MouseClickTipWatcher : IPositionedPipelineElement<IDeviceReport>
{
    public event Action<IDeviceReport>? Emit;

    // PreTransform = receive raw tablet reports, before display-area mapping.
    public PipelinePosition Position => PipelinePosition.PreTransform;

    public void Consume(IDeviceReport report)
    {
        try
        {
            if (report is ITabletReport tablet)
            {
                bool tipPressed    = tablet.Pressure > 0;
                bool wasTipPressed = ClickState.LastTipPressed;
                ClickState.LastTipPressed = tipPressed;

                bool tipDown = tipPressed && !wasTipPressed;
                bool tipUp   = !tipPressed && wasTipPressed;

                if (ClickState.Armed && tipDown)
                {
                    if (ClickState.ActiveButtonIndex == 3)
                    {
                        // Pen Tap mode: capture the real tap's position and
                        // pressure, defer the synthesized follow-up until
                        // after tip-up + InterClickGap.
                        Win32.GetCursorPos(out var pt);
                        ClickState.PenTapScreenX     = pt.X;
                        ClickState.PenTapScreenY     = pt.Y;
                        ClickState.PenTapRawPressure = tablet.Pressure;
                        ClickState.PenTapPendingFire = ClickState.DoubleClick;
                    }
                    else
                    {
                        ClickState.EmitClick(ClickState.DoubleClick);
                    }

                    if (!ClickState.MultipleTapsPerArm)
                    {
                        ClickState.Armed = false;
                    }
                }

                if (tipUp && ClickState.PenTapPendingFire)
                {
                    ClickState.PenTapPendingFire = false;
                    int gap = ClickState.InterClickGapMs;
                    Task.Run(() =>
                    {
                        try
                        {
                            if (gap > 0) Thread.Sleep(gap);
                            ClickState.EmitPenTapAtCaptured();
                        }
                        catch (Exception ex) { Log.Exception(ex); }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception(ex);
        }
        finally
        {
            Emit?.Invoke(report);
        }
    }
}
