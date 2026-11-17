<#
.SYNOPSIS
    Read-only HID probe. Lists the HID collections of a device (e.g. a 2.4G mouse receiver),
    what each one declares, and the reports it exposes - to find out whether and how the
    receiver reports battery level.

.DESCRIPTION
    This script only READS from the device (HidD_GetFeature / HidD_GetInputReport and
    listening for input reports). It never sends commands, so it cannot change any
    mouse setting (DPI, RGB, ...).

    Results are printed and also saved to "hid-probe-result.txt" on the Desktop.

    Usage:
        powershell -ExecutionPolicy Bypass -File hid-probe.ps1
        powershell -ExecutionPolicy Bypass -File hid-probe.ps1 -Filter "vid_1d57&pid_fa60" -ListenSeconds 30
#>
param(
    # Part of the device path to match (vendor/product id). Default: Attack Shark X11 2.4G receiver.
    [string]$Filter = 'vid_1d57&pid_fa60',
    # How long to listen for reports sent by the device.
    [int]$ListenSeconds = 20
)

$source = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

public static class HidProbe
{
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage; public byte ReportID; public byte IsAlias; public ushort BitField; public ushort LinkCollection;
        public ushort LinkUsage; public ushort LinkUsagePage; public byte IsRange; public byte IsStringRange;
        public byte IsDesignatorRange; public byte IsAbsolute; public byte HasNull; public byte Reserved;
        public ushort BitSize; public ushort ReportCount; public ushort R1, R2, R3, R4, R5;
        public uint UnitsExp; public uint Units; public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, S1, S2, S3, S4, S5, S6;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int reportType, [In, Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr pp);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder sb, int bytes);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetManufacturerString(SafeFileHandle h, StringBuilder sb, int bytes);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buffer, int length);

    static readonly object Gate = new object();
    static StringBuilder _log;

    static void W(string line)
    {
        lock (Gate) { Console.WriteLine(line); _log.AppendLine(line); }
    }

    public static string Run(string filter, int listenSeconds)
    {
        _log = new StringBuilder();
        W("HID probe | filter: " + filter + " | " + DateTime.Now + " | " + Environment.OSVersion);
        List<string> paths = GetHidPaths(filter.ToLowerInvariant());
        W("Found " + paths.Count + " HID collection(s).");

        var listeners = new List<Tuple<string, int>>();
        foreach (string p in paths)
        {
            W("");
            W("=== " + ShortName(p));
            int inputLen;
            if (Describe(p, out inputLen) && inputLen > 0) listeners.Add(Tuple.Create(p, inputLen));
        }

        if (listeners.Count > 0 && listenSeconds > 0)
        {
            W("");
            W("=== Listening for " + listenSeconds + "s on " + listeners.Count + " collection(s): move the mouse, click,");
            W("    and if the Attack Shark software is installed, open it on the battery screen now.");
            var tasks = new List<Task>();
            foreach (var l in listeners)
            {
                string path = l.Item1; int len = l.Item2;
                tasks.Add(Task.Run(() => Listen(path, len, listenSeconds)));
            }
            Task.WaitAll(tasks.ToArray());
        }

        W("");
        W("Done.");
        return _log.ToString();
    }

    static List<string> GetHidPaths(string filter)
    {
        var result = new List<string>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA();
            data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref data); i++)
            {
                int required;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, required, out required, IntPtr.Zero))
                    {
                        string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                        if (path != null && path.ToLowerInvariant().Contains(filter)) result.Add(path);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    static bool Describe(string path, out int inputLen)
    {
        inputLen = 0;
        using (SafeFileHandle h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) { W("  cannot open (error " + Marshal.GetLastWin32Error() + ")"); return false; }

            var sb = new StringBuilder(256);
            if (HidD_GetManufacturerString(h, sb, 512)) W("  Manufacturer: " + sb);
            sb.Clear();
            if (HidD_GetProductString(h, sb, 512)) W("  Product:      " + sb);

            IntPtr pp;
            if (!HidD_GetPreparsedData(h, out pp)) { W("  no preparsed data"); return false; }
            try
            {
                HIDP_CAPS caps;
                HidP_GetCaps(pp, out caps);
                W(string.Format("  Collection: page 0x{0:X4} usage 0x{1:X4} ({2})", caps.UsagePage, caps.Usage, UsageName(caps.UsagePage, caps.Usage)));
                W(string.Format("  Report sizes (bytes): input {0}, output {1}, feature {2}",
                    caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
                inputLen = caps.InputReportByteLength;

                List<byte> inputIds = DumpValueCaps(pp, 0, caps.NumberInputValueCaps, "Input");
                DumpValueCaps(pp, 1, caps.NumberOutputValueCaps, "Output");
                List<byte> featureIds = DumpValueCaps(pp, 2, caps.NumberFeatureValueCaps, "Feature");
                if (featureIds.Count == 0 && caps.FeatureReportByteLength > 0) featureIds.Add(0);

                using (SafeFileHandle rw = OpenForReading(path, 0))
                {
                    if (rw == null)
                    {
                        W("  (cannot open for reading - normal for mouse/keyboard collections owned by Windows)");
                        return false;
                    }
                    foreach (byte id in featureIds) TryGet(rw, id, caps.FeatureReportByteLength, true);
                    foreach (byte id in inputIds) TryGet(rw, id, caps.InputReportByteLength, false);
                }
                return true;
            }
            finally { HidD_FreePreparsedData(pp); }
        }
    }

    static List<byte> DumpValueCaps(IntPtr pp, int reportType, ushort count, string name)
    {
        var ids = new List<byte>();
        if (count == 0) return ids;
        var caps = new HIDP_VALUE_CAPS[count];
        ushort length = count;
        if (HidP_GetValueCaps(reportType, caps, ref length, pp) != HIDP_STATUS_SUCCESS)
        {
            W("  " + name + " value caps: error");
            return ids;
        }
        for (int i = 0; i < length; i++)
        {
            HIDP_VALUE_CAPS c = caps[i];
            string usage = c.IsRange != 0
                ? string.Format("0x{0:X4}-0x{1:X4}", c.UsageMin, c.UsageMax)
                : string.Format("0x{0:X4}", c.UsageMin);
            W(string.Format("  {0,-7} report 0x{1:X2}: page 0x{2:X4} usage {3}, {4} bit x {5}, logical {6}..{7}{8}",
                name, c.ReportID, c.UsagePage, usage, c.BitSize, c.ReportCount, c.LogicalMin, c.LogicalMax,
                IsBatteryUsage(c.UsagePage, c.UsageMin) ? "   <== STANDARD BATTERY FIELD" : ""));
            if (!ids.Contains(c.ReportID)) ids.Add(c.ReportID);
        }
        return ids;
    }

    static void TryGet(SafeFileHandle h, byte id, int length, bool feature)
    {
        if (length <= 0) return;
        var buf = new byte[length];
        buf[0] = id;
        bool ok = feature ? HidD_GetFeature(h, buf, length) : HidD_GetInputReport(h, buf, length);
        W(string.Format("  {0}(0x{1:X2}): {2}", feature ? "GetFeature" : "GetInputReport", id,
            ok ? Hex(buf, length) : "failed (error " + Marshal.GetLastWin32Error() + ")"));
    }

    static void Listen(string path, int length, int seconds)
    {
        string name = ShortName(path);
        SafeFileHandle h = OpenForReading(path, FILE_FLAG_OVERLAPPED);
        if (h == null) return;

        var seen = new HashSet<string>();
        string previous = null;
        int total = 0, printed = 0;
        try
        {
            using (var fs = new FileStream(h, FileAccess.Read, Math.Max(length, 1), true))
            {
                DateTime end = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < end)
                {
                    var buf = new byte[length];
                    Task<int> read = fs.ReadAsync(buf, 0, length);
                    TimeSpan remaining = end - DateTime.UtcNow;
                    if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                    if (!read.Wait(remaining)) break; // time is up; closing the handle cancels the read
                    int n = read.Result;
                    if (n <= 0) break;
                    total++;
                    string hex = Hex(buf, n);
                    seen.Add(hex);
                    // Print every change (not only new values), so transitions like A -> B -> A show up.
                    if (hex != previous && printed < 200)
                    {
                        W(string.Format("  [{0:HH:mm:ss.fff}] {1}: {2}", DateTime.Now, name, hex));
                        printed++;
                    }
                    previous = hex;
                }
            }
        }
        catch (Exception ex)
        {
            Exception e = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
            W("  " + name + ": listen stopped (" + e.GetType().Name + ": " + e.Message + ")");
        }
        W(string.Format("  {0}: {1} report(s) received, {2} distinct", name, total, seen.Count));
    }

    static SafeFileHandle OpenForReading(string path, uint flags)
    {
        uint[] modes = { GENERIC_READ | GENERIC_WRITE, GENERIC_READ };
        foreach (uint access in modes)
        {
            SafeFileHandle h = CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
        }
        return null;
    }

    static bool IsBatteryUsage(ushort page, ushort usage)
    {
        return (page == 0x06 && usage == 0x20)   // Generic Device Controls: Battery Strength
            || page == 0x84 || page == 0x85;     // Power Device / Battery System pages
    }

    static string UsageName(ushort page, ushort usage)
    {
        if (page == 0x01 && usage == 0x02) return "mouse";
        if (page == 0x01 && usage == 0x06) return "keyboard";
        if (page == 0x01 && usage == 0x80) return "system control";
        if (page == 0x0C) return "consumer control";
        if (page == 0x06) return "generic device controls";
        if (page == 0x84 || page == 0x85) return "power / battery";
        if (page >= 0xFF00) return "vendor-defined";
        return "other";
    }

    static string ShortName(string path)
    {
        string[] parts = path.Split('#');
        return parts.Length > 1 ? parts[1] : path;
    }

    static string Hex(byte[] buf, int n)
    {
        return BitConverter.ToString(buf, 0, n).Replace("-", " ");
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp
$result = [HidProbe]::Run($Filter, $ListenSeconds)

$file = Join-Path ([Environment]::GetFolderPath('Desktop')) 'hid-probe-result.txt'
[IO.File]::WriteAllText($file, $result)
Write-Host ''
Write-Host "Saved to $file"
