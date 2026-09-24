using System;
using System.Globalization;

namespace MiYue.Core.Util
{
    /// <summary>
    /// Time strings. DIDL &lt;res duration&gt; uses unpadded hours "h:mm:ss", AVTransport
    /// RelTime/TrackDuration "HH:MM:SS"; both may carry ".fff" and AVTransport may answer
    /// "NOT_IMPLEMENTED". Parsing is colon-separated big-endian (HA duration_to_seconds).
    /// </summary>
    public static class TimeText
    {
        /// <summary>Seconds (fraction truncated); 0 for empty / junk / NOT_IMPLEMENTED.</summary>
        public static int ToSeconds(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            value = value.Trim();
            int dot = value.IndexOf('.');
            if (dot >= 0) value = value.Substring(0, dot);
            if (value.Length == 0) return 0;
            var parts = value.Split(':');
            long secs = 0;
            foreach (var p in parts)
            {
                int n;
                if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return 0;
                secs = secs * 60 + n;
                if (secs > int.MaxValue) return 0;
            }
            return (int)secs;
        }

        /// <summary>"H:MM:SS" (the AVTransport Seek REL_TIME target format HA sends).</summary>
        public static string ToHms(int seconds)
        {
            if (seconds < 0) seconds = 0;
            int h = seconds / 3600;
            int m = (seconds % 3600) / 60;
            int s = seconds % 60;
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s);
        }

        /// <summary>Display text: "m:ss", or "h:mm:ss" from one hour up.</summary>
        public static string ToDisplay(int seconds)
        {
            if (seconds < 0) seconds = 0;
            int h = seconds / 3600;
            int m = (seconds % 3600) / 60;
            int s = seconds % 60;
            if (h > 0) return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
        }
    }

    /// <summary>IPv4 validation for group MasterIp (HA group.is_ipv4).</summary>
    public static class Ipv4
    {
        /// <summary>Dotted-quad IPv4 only; rejects empty, IPv6, host names, 0.0.0.0,
        /// 127.x (loopback) and 255.255.255.255.</summary>
        public static bool IsValid(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split('.');
            if (parts.Length != 4) return false;
            var b = new int[4];
            for (int i = 0; i < 4; i++)
            {
                var p = parts[i];
                if (p.Length == 0 || p.Length > 3) return false;
                foreach (var c in p)
                    if (c < '0' || c > '9') return false;
                b[i] = int.Parse(p, CultureInfo.InvariantCulture);
                if (b[i] > 255) return false;
            }
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0) return false;
            if (b[0] == 127) return false;
            if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return false;
            return true;
        }
    }
}
