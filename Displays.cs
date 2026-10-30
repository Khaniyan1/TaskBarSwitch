using System;
using System.Linq;
using System.Management;
using System.Windows.Forms;

namespace TaskbarSwitch
{
    static class Displays
    {
        // Connection types that mean "the laptop's own panel":
        // 0x80000000 internal, 6 LVDS, 11 embedded DisplayPort, 13 embedded UDI
        static readonly uint[] InternalTypes = { 0x80000000u, 6u, 11u, 13u };

        public struct Info
        {
            public int Internal;
            public int External;
            public string Details;
            public bool ExternalConnected => External > 0;
            public override string ToString() =>
                $"{Internal} built-in, {External} external screen{(External == 1 ? "" : "s")} active";
        }

        static string TypeName(uint t)
        {
            switch (t)
            {
                case 0x80000000u: return "internal";
                case 0: return "VGA";
                case 4: return "DVI";
                case 5: return "HDMI";
                case 6: return "LVDS";
                case 10: return "DisplayPort";
                case 11: return "embedded DisplayPort";
                case 13: return "embedded UDI";
                case 15: return "Miracast";
                case 0xFFFFFFFFu: return "other";
                default: return "type " + t;
            }
        }

        /// <summary>
        /// Cheap fingerprint of the current screen layout (a few microseconds), so the 10 s poll only
        /// runs the WMI query when something changed.
        /// </summary>
        public static string Layout()
        {
            var sb = new System.Text.StringBuilder();
            MonitorEnumProc add = (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
            {
                sb.Append($"{r.Left},{r.Top},{r.Right},{r.Bottom};");
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, add, IntPtr.Zero);
            return sb.ToString();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

        public static Info Detect()
        {
            var info = new Info();
            try
            {
                var sb = new System.Text.StringBuilder();
                using (var s = new ManagementObjectSearcher(@"root\wmi",
                    "SELECT InstanceName, Active, VideoOutputTechnology FROM WmiMonitorConnectionParams"))
                {
                    foreach (ManagementObject m in s.Get())
                    {
                        using (m)
                        {
                            bool active = m["Active"] is bool a && a;
                            uint t = Convert.ToUInt32(m["VideoOutputTechnology"]);
                            bool isInternal = InternalTypes.Contains(t);
                            sb.Append($"    {m["InstanceName"]}  type={TypeName(t)} (0x{t:X})  " +
                                      $"{(isInternal ? "built-in" : "external")}  {(active ? "active" : "inactive")}\r\n");
                            if (!active) continue;
                            if (isInternal) info.Internal++;
                            else info.External++;
                        }
                    }
                }
                info.Details = sb.Length > 0 ? sb.ToString().TrimEnd() : "    (WMI returned no monitors)";
            }
            catch (Exception ex)
            {
                Log.Warn("Monitor detection via WMI failed, falling back to screen count", ex);
                // Fallback: more than one screen means something external is attached.
                int n = Screen.AllScreens.Length;
                info.Internal = 1;
                info.External = Math.Max(0, n - 1);
                info.Details = $"    (fallback) {n} screen(s)";
            }
            return info;
        }
    }
}
