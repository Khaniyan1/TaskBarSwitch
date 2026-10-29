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
