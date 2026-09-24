using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MiYue.Core.Util
{
    /// <summary>
    /// XML entity escape / unescape and lenient element helpers.
    /// The firmware's XML is parsed with string scanning (like the C4 driver's soap.lua /
    /// didl.lua), never with a strict XML parser: local ID3 tags can carry raw GBK bytes,
    /// control characters or bare '&amp;' that would make XmlDocument throw.
    /// </summary>
    public static class XmlText
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 16);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Decode the five named entities plus decimal/hex numeric references.
        /// Unknown entities are kept verbatim (never throws).</summary>
        public static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s ?? string.Empty;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c != '&')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                int semi = s.IndexOf(';', i + 1);
                if (semi < 0 || semi - i > 12)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                string ent = s.Substring(i + 1, semi - i - 1);
                string rep = DecodeEntity(ent);
                if (rep == null)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                sb.Append(rep);
                i = semi + 1;
            }
            return sb.ToString();
        }

        private static string DecodeEntity(string ent)
        {
            switch (ent)
            {
                case "amp": return "&";
                case "lt": return "<";
                case "gt": return ">";
                case "quot": return "\"";
                case "apos": return "'";
            }
            if (ent.Length > 1 && ent[0] == '#')
            {
                int cp;
                bool ok;
                if (ent[1] == 'x' || ent[1] == 'X')
                    ok = int.TryParse(ent.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp);
                else
                    ok = int.TryParse(ent.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out cp);
                if (!ok || cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return null;
                return char.ConvertFromUtf32(cp);
            }
            return null;
        }

        private static readonly Regex IllegalChars = new Regex("[\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f]", RegexOptions.Compiled);
        private static readonly Regex BareAmp = new Regex("&(?!#\\d+;|#x[0-9a-fA-F]+;|[A-Za-z][A-Za-z0-9]*;)", RegexOptions.Compiled);

        /// <summary>Remove XML-illegal control characters and escape bare ampersands
        /// (HA soap.parse_xml_lenient). Used on third-party / firmware text before scanning.</summary>
        public static string Scrub(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return BareAmp.Replace(IllegalChars.Replace(s, string.Empty), "&amp;");
        }

        /// <summary>
        /// Locate element &lt;prefix:name ...&gt; (any or no namespace prefix) starting at <paramref name="from"/>.
        /// Returns the index of '&lt;' or -1; outputs the index just past the opening tag's '&gt;',
        /// the prefix (with trailing ':' or empty) and whether it is self-closing.
        /// </summary>
        public static int FindElement(string s, string name, int from, out int openEnd, out string prefix, out bool selfClosing)
        {
            openEnd = -1;
            prefix = string.Empty;
            selfClosing = false;
            if (string.IsNullOrEmpty(s)) return -1;
            int pos = from;
            while (pos < s.Length)
            {
                int lt = s.IndexOf('<', pos);
                if (lt < 0) return -1;
                int j = lt + 1;
                // read qualified name
                int nameStart = j;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '-' || s[j] == ':' || s[j] == '.')) j++;
                if (j > nameStart && j < s.Length)
                {
                    string qn = s.Substring(nameStart, j - nameStart);
                    int colon = qn.LastIndexOf(':');
                    string local = colon >= 0 ? qn.Substring(colon + 1) : qn;
                    char next = s[j];
                    if (local == name && (next == '>' || next == '/' || char.IsWhiteSpace(next)))
                    {
                        int gt = s.IndexOf('>', j);
                        if (gt < 0) return -1;
                        prefix = colon >= 0 ? qn.Substring(0, colon + 1) : string.Empty;
                        selfClosing = s[gt - 1] == '/';
                        openEnd = gt + 1;
                        return lt;
                    }
                }
                pos = lt + 1;
            }
            return -1;
        }

        /// <summary>Unescaped text of the first element named <paramref name="name"/> (any prefix);
        /// null when absent, "" when empty or self-closing. CDATA is returned verbatim.</summary>
        public static string ElementText(string s, string name)
        {
            int openEnd;
            string prefix;
            bool selfClosing;
            int start = FindElement(s, name, 0, out openEnd, out prefix, out selfClosing);
            if (start < 0) return null;
            if (selfClosing) return string.Empty;
            string close = "</" + prefix + name;
            int c = IndexOfClose(s, close, openEnd);
            if (c < 0) return string.Empty;
            return InnerText(s.Substring(openEnd, c - openEnd));
        }

        /// <summary>The opening tag string (from '&lt;' to '&gt;') of the first element named name.</summary>
        public static string OpeningTag(string s, string name)
        {
            int openEnd;
            string prefix;
            bool selfClosing;
            int start = FindElement(s, name, 0, out openEnd, out prefix, out selfClosing);
            if (start < 0) return null;
            return s.Substring(start, openEnd - start);
        }

        /// <summary>Index of a closing tag "&lt;/prefix:name" followed by optional whitespace and '&gt;'.</summary>
        public static int IndexOfClose(string s, string closeStart, int from)
        {
            int pos = from;
            while (pos < s.Length)
            {
                int c = s.IndexOf(closeStart, pos, StringComparison.Ordinal);
                if (c < 0) return -1;
                int k = c + closeStart.Length;
                while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
                if (k < s.Length && s[k] == '>') return c;
                pos = c + 1;
            }
            return -1;
        }

        /// <summary>CDATA-aware text decode of raw element content.</summary>
        public static string InnerText(string raw)
        {
            if (raw == null) return string.Empty;
            if (raw.IndexOf("<![CDATA[", StringComparison.Ordinal) >= 0)
            {
                var sb = new StringBuilder();
                int pos = 0;
                while (pos < raw.Length)
                {
                    int a = raw.IndexOf("<![CDATA[", pos, StringComparison.Ordinal);
                    if (a < 0)
                    {
                        sb.Append(Unescape(raw.Substring(pos)));
                        break;
                    }
                    sb.Append(Unescape(raw.Substring(pos, a - pos)));
                    int b = raw.IndexOf("]]>", a + 9, StringComparison.Ordinal);
                    if (b < 0)
                    {
                        sb.Append(raw.Substring(a + 9));
                        break;
                    }
                    sb.Append(raw.Substring(a + 9, b - a - 9));
                    pos = b + 3;
                }
                return sb.ToString();
            }
            return Unescape(raw);
        }

        /// <summary>Attribute value from an opening-tag string; supports "..." and '...'. Null when absent.</summary>
        public static string Attribute(string openTag, string name)
        {
            if (string.IsNullOrEmpty(openTag)) return null;
            var m = Regex.Match(openTag, "\\s" + Regex.Escape(name) + "\\s*=\\s*(\"([^\"]*)\"|'([^']*)')");
            if (!m.Success) return null;
            string v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            return Unescape(v);
        }
    }
}
