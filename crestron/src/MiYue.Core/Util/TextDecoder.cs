using System;
using System.Text;

namespace MiYue.Core.Util
{
    /// <summary>
    /// Decode firmware responses that may mix UTF-8 with raw GBK byte runs (HA soap.decode_mixed).
    /// The Linux firmware can emit local-file ID3 tags as their original GBK bytes inside an
    /// otherwise UTF-8 XML document. Valid UTF-8 stretches decode normally; each invalid run is
    /// transcoded as GBK when a GBK encoding is available on the platform, else as Latin-1.
    /// Never throws.
    /// </summary>
    public static class TextDecoder
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static Encoding _gbk;
        private static bool _gbkProbed;
        private static readonly object GbkLock = new object();

        /// <summary>GBK (code page 936) if the runtime provides it; null otherwise.
        /// .NET Framework on Windows has it natively; .NET Core/Mono may need a provider
        /// (the tests register System.Text.Encoding.CodePages).</summary>
        public static Encoding Gbk
        {
            get
            {
                lock (GbkLock)
                {
                    if (!_gbkProbed)
                    {
                        _gbkProbed = true;
                        try
                        {
                            _gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                        }
                        catch (Exception)
                        {
                            _gbk = null;
                        }
                    }
                    return _gbk;
                }
            }
        }

        /// <summary>Force a re-probe of the GBK encoding (after a provider was registered).</summary>
        public static void ResetGbkProbe()
        {
            lock (GbkLock)
            {
                _gbkProbed = false;
                _gbk = null;
            }
        }

        public static string DecodeMixed(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return string.Empty;
            int start = 0;
            // strip a UTF-8 BOM
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) start = 3;
            try
            {
                return StrictUtf8.GetString(raw, start, raw.Length - start);
            }
            catch (DecoderFallbackException)
            {
            }

            var sb = new StringBuilder(raw.Length);
            int i = start;
            int n = raw.Length;
            while (i < n)
            {
                int validLen = ValidUtf8Prefix(raw, i, n);
                if (validLen > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(raw, i, validLen));
                    i += validLen;
                    continue;
                }
                // invalid run: consume GBK pairs (lead 0x81-0xFE + trail 0x40-0xFE, not 0x7F)
                int j = i;
                while (j < n && raw[j] >= 0x81)
                {
                    j++;
                    if (j < n && raw[j] >= 0x40 && raw[j] <= 0xFE && raw[j] != 0x7F) j++;
                }
                if (j == i) j = i + 1; // a lone invalid byte below 0x81: skip it defensively
                sb.Append(DecodeRun(raw, i, j - i));
                i = j;
            }
            return sb.ToString();
        }

        private static string DecodeRun(byte[] raw, int offset, int count)
        {
            if (count == 1 && raw[offset] < 0x81) return string.Empty;
            var gbk = Gbk;
            if (gbk != null)
            {
                try
                {
                    return gbk.GetString(raw, offset, count);
                }
                catch (DecoderFallbackException)
                {
                }
            }
            var sb = new StringBuilder(count);
            for (int k = 0; k < count; k++) sb.Append((char)raw[offset + k]); // Latin-1
            return sb.ToString();
        }

        /// <summary>Length of the longest prefix of raw[i..n) that is valid, complete UTF-8.</summary>
        private static int ValidUtf8Prefix(byte[] raw, int i, int n)
        {
            int p = i;
            while (p < n)
            {
                byte b = raw[p];
                int need;
                if (b < 0x80) need = 0;
                else if (b >= 0xC2 && b <= 0xDF) need = 1;
                else if (b >= 0xE0 && b <= 0xEF) need = 2;
                else if (b >= 0xF0 && b <= 0xF4) need = 3;
                else break;
                if (p + need >= n && need > 0) break;
                bool ok = true;
                for (int k = 1; k <= need; k++)
                {
                    byte c = raw[p + k];
                    if (c < 0x80 || c > 0xBF) { ok = false; break; }
                }
                if (ok && need == 2)
                {
                    byte c1 = raw[p + 1];
                    if (b == 0xE0 && c1 < 0xA0) ok = false;           // overlong
                    if (b == 0xED && c1 > 0x9F) ok = false;           // surrogates
                }
                if (ok && need == 3)
                {
                    byte c1 = raw[p + 1];
                    if (b == 0xF0 && c1 < 0x90) ok = false;
                    if (b == 0xF4 && c1 > 0x8F) ok = false;
                }
                if (!ok) break;
                p += need + 1;
            }
            return p - i;
        }
    }
}
