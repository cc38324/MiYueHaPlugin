using System.Collections.Generic;
using MiYue.Core.Didl;
using MiYue.Core.Soap;
using MiYue.Core.Tests.Support;
using MiYue.Core.Util;
using Xunit;

namespace MiYue.Core.Tests
{
    /// <summary>DIDL-Lite parsing: HA tests/test_didl.py ported + C4 fixture checks.</summary>
    public class DidlTests
    {
        public DidlTests()
        {
            Fixtures.EnsureGbk();
        }

        // A real GetTimeline Result captured from a live M330B (netease queue) - HA LIVE_DIDL.
        private const string LiveDidl =
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\"" +
            " xmlns:dc=\"http://purl.org/dc/elements/1.1/\"" +
            " xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"" +
            " xmlns:miyue=\"urn:miyue-hk:metadata\">" +
            "<item id=\"1927693793\" parentID=\"-1\" restricted=\"1\">" +
            "<dc:title>再等冬天(Memories)</dc:title>" +
            "<upnp:artist></upnp:artist><dc:creator></dc:creator>" +
            "<upnp:album>故事商铺·上</upnp:album>" +
            "<upnp:albumArtURI>http://p3.music.126.net/x==/109951169798343077.jpg</upnp:albumArtURI>" +
            "<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
            "<miyue:songSrc>15</miyue:songSrc><miyue:songId>1927693793</miyue:songId>" +
            "<miyue:musicId>640085</miyue:musicId>" +
            "<res protocolInfo=\"http-get:*:audio/mpeg:*\" duration=\"0:03:42\"></res>" +
            "</item></DIDL-Lite>";

        private const string Wrap =
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\"" +
            " xmlns:dc=\"http://purl.org/dc/elements/1.1/\"" +
            " xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"" +
            " xmlns:miyue=\"urn:miyue-hk:metadata\">{0}</DIDL-Lite>";

        [Fact]
        public void Parse_live_didl()
        {
            var tracks = DidlDoc.Parse(LiveDidl);
            Assert.Single(tracks);
            var t = tracks[0];
            Assert.Equal("再等冬天(Memories)", t.Title);
            Assert.Equal("故事商铺·上", t.Album);
            Assert.EndsWith("109951169798343077.jpg", t.AlbumArt);
            Assert.Equal(15, t.SongSrc);
            Assert.Equal("1927693793", t.SongId);
            Assert.Equal("640085", t.MusicId);
            Assert.Equal("0:03:42", t.Duration);
            Assert.Equal("1927693793", t.Id);
            Assert.StartsWith("<item id=\"1927693793\"", t.Raw);
            Assert.EndsWith("</item>", t.Raw);
        }

        [Fact]
        public void Parse_empty_and_junk()
        {
            Assert.Empty(DidlDoc.Parse(""));
            Assert.Empty(DidlDoc.Parse("   "));
            Assert.Empty(DidlDoc.Parse("<not-xml"));
            Assert.Empty(DidlDoc.Parse(null));
            // empty results are a self-closing container (contract quirk)
            Assert.Empty(DidlDoc.Parse("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\"/>"));
        }

        [Fact]
        public void Artist_falls_back_to_dc_creator()
        {
            // Linux GetPositionInfo fixture: dc:creator only, self-closing <res duration/>
            var r = SoapResponse.Parse("GetPositionInfo", 200, Fixtures.Text("linux.AVT.GetPositionInfo.xml"));
            var t = DidlDoc.Parse(r.Get("TrackMetaData"))[0];
            Assert.Equal("Look What You Made Me Do", t.Title);
            Assert.Equal("Taylor Swift", t.Artist);
            Assert.Equal(11, t.SongSrc);
            Assert.Equal("0:03:31", t.Duration);
            Assert.Equal("", t.Res);
        }

        [Fact]
        public void Android_queue_fixture()
        {
            var r = SoapResponse.Parse("GetQueue", 200, Fixtures.Text("android.MiyueQueue.GetQueue.xml"));
            var items = DidlDoc.Parse(r.Get("Result"));
            Assert.Equal(3, items.Count);
            Assert.Equal("The Look Of Love", items[2].Title);
            Assert.Equal("Diana Krall", items[2].Artist);
            Assert.Equal(25, items[2].SongSrc);
            Assert.Equal("https://static.qobuz.com/images/covers/95/15/0060253771595_600.jpg", items[2].AlbumArt);
        }

        [Fact]
        public void Radios_fixture_keeps_stream_url_and_music_id()
        {
            var r = SoapResponse.Parse("GetCollectedRadios", 200, Fixtures.Text("MiyueLibrary.GetCollectedRadios.xml"));
            var t = DidlDoc.Parse(r.Get("Result"))[0];
            Assert.Equal("上海新闻广播", t.Title);
            Assert.Equal(8, t.SongSrc);
            Assert.Equal("282", t.MusicId);
            Assert.Equal("http://ls.qingting.fm/live/270/24k.m3u8", t.Res);
        }

        [Fact]
        public void Still_escaped_document_is_tolerated()
        {
            Assert.Single(DidlDoc.Parse(XmlText.Escape(LiveDidl)));
        }

        [Fact]
        public void Repair_mojibake_fixes_real_gbk()
        {
            Assert.Equal("音乐热搜", Mojibake.Repair("ÒôÀÖÈÈËÑ"));
            Assert.Equal("站长素材(sc.chinaz.com)", Mojibake.Repair("Õ¾³¤ËØ²Ä(sc.chinaz.com)"));
        }

        [Theory]
        [InlineData("Björk")]
        [InlineData("Größe")]
        [InlineData("Sigur Rós")]
        [InlineData("Motörhead")]
        [InlineData("São Paulo")]
        [InlineData("naïve")]
        [InlineData("Café del Mar")]
        [InlineData("Joel Adams")]
        [InlineData("周杰伦")]
        public void Repair_mojibake_never_touches_real_latin(string s)
        {
            Assert.Equal(s, Mojibake.Repair(s));
        }

        [Fact]
        public void Only_local_tracks_are_repaired()
        {
            string tmpl =
                "<item id=\"1\" parentID=\"-1\" restricted=\"1\"><dc:title>ÒôÀÖÈÈËÑ</dc:title><upnp:artist>x</upnp:artist>" +
                "<upnp:album></upnp:album><upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
                "<miyue:songSrc>{0}</miyue:songSrc><miyue:songId>1</miyue:songId><res protocolInfo=\"http-get:*:audio/mpeg:*\"></res></item>";
            Assert.Equal("音乐热搜", DidlDoc.Parse(string.Format(Wrap, string.Format(tmpl, 0)))[0].Title);
            Assert.Equal("ÒôÀÖÈÈËÑ", DidlDoc.Parse(string.Format(Wrap, string.Format(tmpl, 15)))[0].Title);
        }

        private static string RadioItem(string songId, string res) =>
            "<item id=\"" + songId + "\" parentID=\"-1\" restricted=\"1\"><dc:title>radio</dc:title>" +
            "<upnp:class>object.item.audioItem.musicTrack</upnp:class><miyue:songSrc>8</miyue:songSrc><miyue:songId>" + songId +
            "</miyue:songId><res protocolInfo=\"http-get:*:audio/mpeg:*\" duration=\"0:00:00\">" + res + "</res></item>";

        [Fact]
        public void Fill_radio_res_synthesizes_qingting_url()
        {
            var filled = DidlDoc.FillRadioRes(string.Format(Wrap, RadioItem("20003", "")));
            Assert.Contains("http://ls.qingting.fm/live/20003/24k.m3u8", filled);
            Assert.EndsWith("/20003/24k.m3u8", DidlDoc.Parse(filled)[0].Res);
        }

        [Fact]
        public void Fill_radio_res_leaves_good_entries_and_non_radio_alone()
        {
            var didl = string.Format(Wrap, RadioItem("270", "http://ls.qingting.fm/live/270/24k.m3u8"));
            Assert.Equal(didl, DidlDoc.FillRadioRes(didl));
            var ne = string.Format(Wrap, RadioItem("20003", "")).Replace(">8<", ">15<");
            Assert.Equal(ne, DidlDoc.FillRadioRes(ne));
        }

        [Fact]
        public void Wrap_and_merge_keep_items_verbatim()
        {
            var a = DidlDoc.Parse(LiveDidl)[0].Raw;
            var doc = DidlDoc.Wrap(new[] { a, a });
            Assert.StartsWith(DidlDoc.Header, doc);
            Assert.Equal(2, DidlDoc.Parse(doc).Count);
            var merged = DidlDoc.Merge(new List<string> { doc, LiveDidl, "", DidlDoc.Header + DidlDoc.Footer });
            Assert.Equal(3, DidlDoc.Parse(merged).Count);
            Assert.EndsWith(DidlDoc.Footer, merged);
        }

        [Fact]
        public void Direct_link_sources()
        {
            Assert.True(DidlDoc.IsDirectLinkSrc(8));
            Assert.True(DidlDoc.IsDirectLinkSrc(10));
            Assert.True(DidlDoc.IsDirectLinkSrc(21));
            Assert.False(DidlDoc.IsDirectLinkSrc(15));
            Assert.False(DidlDoc.IsDirectLinkSrc(null));
        }
    }
}
