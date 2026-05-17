using System;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace Buttonalicious;

// Shared state between the IStateBinding (pen button) and the IPositionedPipelineElement
// (tip watcher). Both classes run on different OTD threads; volatile + simple atomic writes
// are enough — worst case is a single stale frame on disarm, which is sub-millisecond and
// harmless.
internal static class ClickState
{
    public static volatile bool Armed;
    public static volatile int  ActiveButtonIndex; // 0=Left, 1=Right, 2=Middle
    public static volatile int  ClickHoldMs = 10;
    public static volatile int  InterClickGapMs = 80;
    public static volatile bool DoubleClick;
    public static volatile bool MultipleTapsPerArm;
    public static volatile bool LastTipPressed;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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

[PluginName("Mouse Click")]
public class MouseClickBinding : IStateBinding
{
    [Property("Button"),
     DefaultPropertyValue("Left"),
     PropertyValidated(nameof(ValidButtons)),
     ToolTip("Which mouse button to click.")]
    public string Button { get; set; } = "Left";

    [BooleanProperty("Double Click", ""),
     ToolTip("Checked: emit two clicks (with timing controlled below). Unchecked: emit one click.")]
    public bool DoubleClick { get; set; }

    [Property("Mode"),
     DefaultPropertyValue("Immediate"),
     PropertyValidated(nameof(ValidModes)),
     ToolTip("Immediate: click(s) fire the moment the pen button is pressed, at the current cursor position.\nHoldAndTap: pen button arms the click; the next tip tap fires it at the tip position. HoldAndTap requires the 'Mouse Click Tap Watcher' filter to be enabled in the Filters tab.")]
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
    public static string[] ValidButtons => new[] { "Left", "Right", "Middle" };

    public void Press(TabletReference tablet, IDeviceReport report)
    {
        try
        {
            ClickState.ActiveButtonIndex  = Button switch { "Right" => 1, "Middle" => 2, _ => 0 };
            ClickState.ClickHoldMs        = ClickHoldMs;
            ClickState.InterClickGapMs    = InterClickGapMs;
            ClickState.DoubleClick        = DoubleClick;
            ClickState.MultipleTapsPerArm = MultipleTapsPerArm;

            if (Mode == "HoldAndTap")
            {
                // Snapshot current tip state so we only fire on FUTURE tip transitions —
                // not when the user is already touching the tablet when they hit the button.
                ClickState.LastTipPressed = report is ITabletReport t && t.Pressure > 0;
                ClickState.Armed = true;
            }
            else
            {
                ClickState.EmitClick(DoubleClick);
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
        }
    }
}

[PluginName("Mouse Click Tap Watcher")]
public class MouseClickTipWatcher : IPositionedPipelineElement<IDeviceReport>
{
    public event Action<IDeviceReport>? Emit;

    // PreTransform = receive raw tablet reports, before display-area mapping.
    // We don't care about coordinates here, only tip pressure transitions.
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

                if (ClickState.Armed && tipPressed && !wasTipPressed)
                {
                    ClickState.EmitClick(ClickState.DoubleClick);
                    if (!ClickState.MultipleTapsPerArm)
                    {
                        ClickState.Armed = false;
                    }
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
