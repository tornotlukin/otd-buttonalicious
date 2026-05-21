// VMulti pen-channel synthesis.
//
// Opens VMulti's writable HID interface (VID 0x00FF, PID 0xBACC, 65/65 byte
// report length) and emits Windows-Ink digitizer reports independently of any
// active OutputMode. Sequence per synthetic tap:
//
//   1. InRange                         (pen approach, no contact)
//   2. InRange | Press, P=pressure     (tip down)
//   3. InRange,         P=0            (tip up)
//   4. 0                               (out of range)
//
// Report layout and the tap sequence pattern are derived from VoiDPlugins'
// VMulti library and WinInk output mode, both GPL-3.0-only. Original work by
// X9VoiD: https://github.com/X9VoiD/VoiDPlugins

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;
using HidSharp.Reports;
using OpenTabletDriver.Plugin;

namespace Buttonalicious;

[Flags]
internal enum InkButton : byte
{
    Press   = 1,
    Barrel  = 2,
    Eraser  = 4,
    Invert  = 8,
    InRange = 16
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DigitizerReport
{
    public const byte NormalReportId   = 0x05; // pressure 0..8191
    public const byte ExtendedReportId = 0x06; // pressure 0..16383

    public byte   VMultiId;
    public byte   ReportLength;
    public byte   ReportId;
    public byte   Buttons;
    public ushort X;
    public ushort Y;
    public ushort Pressure;
    public sbyte  XTilt;
    public sbyte  YTilt;

    public static DigitizerReport Make(byte reportId)
    {
        var r = new DigitizerReport
        {
            VMultiId     = 0x40,
            ReportLength = (byte)(Unsafe.SizeOf<DigitizerReport>() - 1),
            ReportId     = reportId,
        };
        return r;
    }
}

internal sealed class VMultiPen
{
    private const int VID = 0x00FF;
    private const int PID = 0xBACC;

    private static readonly Lazy<VMultiPen?> _instance = new(() => TryOpen(), LazyThreadSafetyMode.ExecutionAndPublication);
    public static VMultiPen? Instance => _instance.Value;

    private readonly HidStream _stream;
    private readonly byte[]    _buffer;
    private readonly bool      _extended;
    private readonly object    _writeLock = new();

    public ushort MaxPressure => (ushort)(_extended ? 16383 : 8191);

    private VMultiPen(HidStream stream, bool extended)
    {
        _stream   = stream;
        _extended = extended;
        _buffer   = new byte[Unsafe.SizeOf<DigitizerReport>()];
    }

    private static VMultiPen? TryOpen()
    {
        try
        {
            var devices = DeviceList.Local.GetHidDevices(VID, PID).ToArray();
            if (devices.Length == 0)
            {
                Log.Write("Buttonalicious", "VMulti device not found. Install vmulti-bin.", LogLevel.Error);
                return null;
            }

            HidStream? writable = null;
            foreach (var d in devices)
            {
                if (d.GetMaxInputReportLength() == 65 && d.GetMaxOutputReportLength() == 65)
                {
                    if (d.TryOpen(out writable))
                        break;
                }
            }

            bool extended = false;
            foreach (var d in devices)
            {
                if (d.GetMaxInputReportLength() == 10)
                {
                    var desc = d.GetReportDescriptor();
                    if (desc.TryGetReport(ReportType.Input, DigitizerReport.ExtendedReportId, out _))
                        extended = true;
                }
            }

            if (writable == null)
            {
                Log.Write("Buttonalicious", "VMulti writable HID interface not openable.", LogLevel.Error);
                return null;
            }

            Log.Write("Buttonalicious", $"VMulti pen opened ({(extended ? "extended" : "normal")} digitizer).");
            return new VMultiPen(writable, extended);
        }
        catch (Exception ex)
        {
            Log.Exception(ex);
            return null;
        }
    }

    // Convert absolute screen coords to virtual-screen 0..32767 used by VMulti.
    private static (ushort x, ushort y) ToVirtualScreen(int screenX, int screenY)
    {
        int vLeft   = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vTop    = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vWidth  = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vHeight = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        if (vWidth <= 0)  vWidth  = 1;
        if (vHeight <= 0) vHeight = 1;

        double nx = (double)(screenX - vLeft) / vWidth  * 32767.0;
        double ny = (double)(screenY - vTop)  / vHeight * 32767.0;
        nx = Math.Clamp(nx, 0, 32767);
        ny = Math.Clamp(ny, 0, 32767);
        return ((ushort)nx, (ushort)ny);
    }

    // Emit a single tap at the given absolute screen position, with the given
    // pressure (already scaled to VMulti's range — see MaxPressure).
    public void EmitTap(int screenX, int screenY, ushort pressure, int holdMs)
    {
        var (vx, vy) = ToVirtualScreen(screenX, screenY);
        byte reportId = _extended ? DigitizerReport.ExtendedReportId : DigitizerReport.NormalReportId;

        var inRange = DigitizerReport.Make(reportId);
        inRange.X = vx; inRange.Y = vy; inRange.Pressure = 0;
        inRange.Buttons = (byte)InkButton.InRange;

        var tipDown = inRange;
        tipDown.Buttons = (byte)(InkButton.InRange | InkButton.Press);
        tipDown.Pressure = pressure;

        var tipUp = inRange;
        tipUp.Buttons = (byte)InkButton.InRange;
        tipUp.Pressure = 0;

        var outOfRange = DigitizerReport.Make(reportId);
        outOfRange.X = vx; outOfRange.Y = vy; outOfRange.Pressure = 0;
        outOfRange.Buttons = 0;

        lock (_writeLock)
        {
            WriteReport(inRange);
            WriteReport(tipDown);
            if (holdMs > 0) Thread.Sleep(holdMs);
            WriteReport(tipUp);
            WriteReport(outOfRange);
        }
    }

    private unsafe void WriteReport(DigitizerReport r)
    {
        fixed (byte* dst = _buffer)
        {
            *(DigitizerReport*)dst = r;
        }
        _stream.Write(_buffer);
    }

    internal static class Win32
    {
        public const int SM_XVIRTUALSCREEN  = 76;
        public const int SM_YVIRTUALSCREEN  = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }
}
