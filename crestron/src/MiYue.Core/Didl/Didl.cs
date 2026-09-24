using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MiYue.Core.Util;

namespace MiYue.Core.Didl
{
    /// <summary>One DIDL-Lite &lt;item&gt; as the controller needs it. <see cref="Raw"/> keeps the verbatim
    /// item string so it can be fed back to ReplaceQueue / AppendQueue unchanged.</summary>
    public sealed class DidlItem
    {
        public string Id = string.Empty;
        public string Title = string.Empty;
        public string Artist = string.Empty;
        public string Album = string.Empty;
        public string AlbumArt = string.Empty;
        public string UpnpClass = string.Empty;
        /// <summary>&lt;miyue:songSrc&gt; or null when absent/unparseable.</summary>
        public int? SongSrc;
        public string SongId = string.Empty;
        public string MusicId = string.Empty;
        /// <summary>&lt;res duration&gt; as given ("h:mm:ss", may be "0:00:00").</summary>
        public string Duration = string.Empty;
        public string Res = string.Empty;
        public string Raw = string.Empty;
    }

    /// <summary>
    /// DIDL-Lite parsing and building. String scanning like the C4 didl.lua (prefix tolerant,
    /// self-closing aware, CDATA aware) plus HA didl.py semantics: artist = upnp:artist falling back to
    /// dc:creator (Android emits dc:creator), GBK-mojibake repair only for local tracks (songSrc 0),
    /// qingting URL synthesis for collected radios with an empty &lt;res&gt;.
    /// </summary>
    public static class DidlDoc
    {
        public const string Header =
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:miyue=\"urn:miyue-hk:metadata\">";

        public const string Footer = "</DIDL-Lite>";

        /// <summary>Direct-link sources the firmware will NOT self-resolve: &lt;res&gt; must be non-empty
        /// or playback is silent (radio 8, ergeduoduo 10, NAS/DMS 21) - HA didl.DIRECT_LINK_SRCS.</summary>
        public static readonly int[] DirectLinkSrcs = { 8, 10, 21 };

        public static bool IsDirectLinkSrc(int? src)
        {
            if (!src.HasValue) return false;
            return Array.IndexOf(DirectLinkSrcs, src.Value) >= 0;
        }

        /// <summary>Split a DIDL document into verbatim &lt;item ...&gt;...&lt;/item&gt; strings.</summary>
        public static List<string> RawItems(string xml)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(xml)) return list;
            int pos = 0;
            while (pos < xml.Length)
            {
                int a = xml.IndexOf("<item", pos, StringComparison.Ordinal);
                if (a < 0) break;
                int after = a + 5;
                if (after >= xml.Length) break;
                char nx = xml[after];
                if (!(nx == '>' || nx == '/' || char.IsWhiteSpace(nx)))
                {
                    pos = after;
                    continue;
                }
                int gt = xml.IndexOf('>', after);
                if (gt < 0) break;
                if (xml[gt - 1] == '/')
                {
                    list.Add(xml.Substring(a, gt - a + 1));
                    pos = gt + 1;
                    continue;
                }
                int c = xml.IndexOf("</item>", gt + 1, StringComparison.Ordinal);
                if (c < 0)
                {
                    list.Add(xml.Substring(a)); // unterminated: take the rest and stop
                    break;
                }
                list.Add(xml.Substring(a, c + 7 - a));
                pos = c + 7;
            }
            return list;
        }

        public static List<DidlItem> Parse(string xml)
        {
            var items = new List<DidlItem>();
            if (string.IsNullOrEmpty(xml) || xml.Trim().Length == 0) return items;
            // tolerate a still-escaped document handed in by mistake
            if (xml.IndexOf("<item", StringComparison.Ordinal) < 0 && xml.IndexOf("&lt;item", StringComparison.Ordinal) >= 0)
                xml = XmlText.Unescape(xml);
            foreach (var raw in RawItems(xml)) items.Add(ParseItem(raw));
            return items;
        }

        public static DidlItem ParseItem(string raw)
        {
            var it = new DidlItem { Raw = raw ?? string.Empty };
            if (string.IsNullOrEmpty(raw)) return it;
            int openEnd = raw.IndexOf('>');
            string openTag = openEnd >= 0 ? raw.Substring(0, openEnd + 1) : raw;
            it.Id = XmlText.Attribute(openTag, "id") ?? string.Empty;
            it.Title = (XmlText.ElementText(raw, "title") ?? string.Empty).Trim();
            string artist = (XmlText.ElementText(raw, "artist") ?? string.Empty).Trim();
            if (artist.Length == 0) artist = (XmlText.ElementText(raw, "creator") ?? string.Empty).Trim();
            it.Artist = artist;
            it.Album = (XmlText.ElementText(raw, "album") ?? string.Empty).Trim();
            it.AlbumArt = (XmlText.ElementText(raw, "albumArtURI") ?? string.Empty).Trim();
            it.UpnpClass = (XmlText.ElementText(raw, "class") ?? string.Empty).Trim();
            int src;
            var srcText = XmlText.ElementText(raw, "songSrc");
            if (srcText != null && int.TryParse(srcText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out src))
                it.SongSrc = src;
            it.SongId = (XmlText.ElementText(raw, "songId") ?? string.Empty).Trim();
            it.MusicId = (XmlText.ElementText(raw, "musicId") ?? string.Empty).Trim();
            var resTag = XmlText.OpeningTag(raw, "res");
            if (resTag != null)
            {
                it.Duration = XmlText.Attribute(resTag, "duration") ?? string.Empty;
                it.Res = (XmlText.ElementText(raw, "res") ?? string.Empty).Trim();
            }
            if (it.SongSrc == 0)
            {
                // only local files carry GBK-as-Latin-1 ID3 tags
                it.Title = Mojibake.Repair(it.Title);
                it.Artist = Mojibake.Repair(it.Artist);
                it.Album = Mojibake.Repair(it.Album);
            }
            return it;
        }

        /// <summary>Wrap verbatim &lt;item&gt; strings into a DIDL-Lite document (C4 Didl.wrap).</summary>
        public static string Wrap(IEnumerable<string> rawItems)
        {
            var sb = new StringBuilder(Header);
            if (rawItems != null)
                foreach (var r in rawItems)
                    if (!string.IsNullOrEmpty(r)) sb.Append(r);
            sb.Append(Footer);
            return sb.ToString();
        }

        /// <summary>Merge several DIDL pages by splicing item runs (HA browse._merge_didl). String-level on
        /// purpose: re-serializing would rewrite namespace prefixes the firmware string-matches.</summary>
        public static string Merge(IList<string> pages)
        {
            var real = new List<string>();
            if (pages != null)
                foreach (var p in pages)
                    if (!string.IsNullOrEmpty(p) && p.IndexOf("<item", StringComparison.Ordinal) >= 0) real.Add(p);
            if (real.Count == 0) return string.Empty;
            if (real.Count == 1) return real[0];
            string first = real[0];
            int close = first.LastIndexOf(Footer, StringComparison.Ordinal);
            if (close < 0) return first;
            var sb = new StringBuilder(first.Substring(0, close));
            for (int i = 1; i < real.Count; i++)
            {
                var page = real[i];
                int s = page.IndexOf("<item", StringComparison.Ordinal);
                int e = page.LastIndexOf(Footer, StringComparison.Ordinal);
                if (s < 0 || e < 0) continue;
                sb.Append(page.Substring(s, e - s));
            }
            sb.Append(Footer);
            return sb.ToString();
        }

        private static readonly Regex ItemRe = new Regex("<item\\b.*?</item>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex EmptyResRe = new Regex("(<res\\b[^>]*>)\\s*(</res>)", RegexOptions.Compiled);
        private static readonly Regex SongIdRe = new Regex("<miyue:songId>(\\d+)</miyue:songId>", RegexOptions.Compiled);
        private const string RadioTag = "<miyue:songSrc>8</miyue:songSrc>";

        /// <summary>Synthesize missing qingting stream URLs for collected radios (HA didl.fill_radio_res):
        /// old collect paths stored songSrc=8 radios with an EMPTY &lt;res&gt; and the Android controller-queue
        /// path never self-resolves direct-link sources. http://ls.qingting.fm/live/&lt;songId&gt;/24k.m3u8</summary>
        public static string FillRadioRes(string didl)
        {
            if (string.IsNullOrEmpty(didl) || didl.IndexOf(RadioTag, StringComparison.Ordinal) < 0) return didl;
            return ItemRe.Replace(didl, m =>
            {
                string block = m.Value;
                if (block.IndexOf(RadioTag, StringComparison.Ordinal) < 0) return block;
                var sid = SongIdRe.Match(block);
                if (!sid.Success || !EmptyResRe.IsMatch(block)) return block;
                string url = "http://ls.qingting.fm/live/" + sid.Groups[1].Value + "/24k.m3u8";
                return EmptyResRe.Replace(block, mm => mm.Groups[1].Value + url + mm.Groups[2].Value, 1);
            });
        }
    }

    /// <summary>
    /// Repair GBK ID3 tags a device served as Latin-1 (e.g. "ÒôÀÖÈÈËÑ" -> "音乐热搜"), HA didl.repair_mojibake.
    /// Deliberately strict so accented Latin names (Björk, Größe) are never corrupted. Needs a GBK
    /// encoding at runtime; when the platform has none the text passes through unchanged (C4 behaviour).
    /// </summary>
    public static class Mojibake
    {
        public static string Repair(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            int highs = 0;
            foreach (var c in value)
            {
                if (c > 0xFF) return value; // already real Unicode
                if (c > 0x7F)
                {
                    if (c < 0x81) return value; // 0x80..0xA0 aren't GBK lead bytes -> ordinary accented Latin
                    highs++;
                }
            }
            if (highs < 4 || highs % 2 != 0) return value;
            var gbk = TextDecoder.Gbk;
            if (gbk == null) return value;
            string repaired;
            try
            {
                var bytes = new byte[value.Length];
                for (int i = 0; i < value.Length; i++) bytes[i] = (byte)value[i];
                repaired = gbk.GetString(bytes);
            }
            catch (Exception)
            {
                return value;
            }
            int cjk = 0;
            foreach (var c in repaired)
                if (c >= '一' && c <= '鿿') cjk++;
            if (cjk >= 2 && cjk >= highs / 2 - 1) return repaired;
            return value;
        }
    }
}
