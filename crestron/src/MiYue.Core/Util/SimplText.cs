using System;
using System.Text;

namespace MiYue.Core.Util
{
    /// <summary>
    /// String conversions at the SIMPL+ boundary. SIMPL+ strings are byte strings unless the program /
    /// module uses UTF-16 encoding, so Chinese text may arrive either as real Unicode or as UTF-8 bytes
    /// packed one per char (a panel keyboard in an ASCII-encoded program).
    /// </summary>
    public static class SimplText
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Normalise an incoming SIMPL+ string: when every char is a byte (&lt;= 0xFF), at least one is
        /// non-ASCII and the bytes form valid UTF-8, decode them as UTF-8; otherwise return unchanged.
        /// </summary>
        public static string FromSimpl(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            bool high = false;
            foreach (var c in value)
            {
                if (c > 0xFF) return value; // already Unicode
                if (c >= 0x80) high = true;
            }
            if (!high) return value;
            var bytes = new byte[value.Length];
            for (int i = 0; i < value.Length; i++) bytes[i] = (byte)value[i];
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return value; // genuine Latin-1 text
            }
        }

        /// <summary>UTF-8 bytes of <paramref name="value"/> packed one per char, for SIMPL programs that run
        /// with ASCII string encoding and panels that render UTF-8.</summary>
        public static string ToUtf8Bytes(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            var bytes = Encoding.UTF8.GetBytes(value);
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes) sb.Append((char)b);
            return sb.ToString();
        }

        /// <summary>Truncate to at most <paramref name="max"/> chars (SIMPL+ STRING_OUTPUT / panel limits).</summary>
        public static string Clip(string value, int max)
        {
            if (value == null) return string.Empty;
            if (max <= 0 || value.Length <= max) return value;
            // never split a surrogate pair
            int n = max;
            if (char.IsHighSurrogate(value[n - 1])) n--;
            return value.Substring(0, n);
        }
    }
}
