using System.Linq;
using System.Text;
using MiYue.Core.Didl;
using MiYue.Core.Engine;
using MiYue.Core.Soap;
using MiYue.Core.Tests.Support;
using Xunit;
using B = MiYue.Core.Engine.BrowserSignals;

namespace MiYue.Core.Tests
{
    public class BrowserEngineTests
    {
        private const string Ip = "192.168.1.47";

        private static (Rig rig, FakeMiyue dev, PlayerEngine player, BrowserEngine browser, RecordingSink sink) Setup(int pageSize = 2)
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (player, _) = rig.StartPlayer(Ip);
            var browser = new BrowserEngine(rig.Rt);
            var sink = new RecordingSink();
            browser.Start(Ip, pageSize, sink);
            return (rig, dev, player, browser, sink);
        }

        [Fact]
        public void Attaches_to_a_player_that_starts_later()
        {
            var rig = new Rig();
            rig.AddDevice(Ip);
            var browser = new BrowserEngine(rig.Rt);
            var sink = new RecordingSink();
            browser.Start(Ip, 5, sink);
            Assert.False(sink.Dig(B.DoPlayerFound));
            rig.StartPlayer(Ip);
            rig.Clock.Advance(BrowserEngine.FindPlayerRetryMs + 100);
            Assert.True(sink.Dig(B.DoPlayerFound));
        }

        [Fact]
        public void Liked_list_pages_and_plays_the_whole_list_from_the_selected_row()
        {
            var (rig, dev, player, browser, sink) = Setup(pageSize: 2);
            browser.Digital(B.DiListLiked, true);
            Assert.Equal("我喜欢的 Liked", sink.Ser(B.SoListTitle));
            Assert.Equal(3, sink.Ana(B.AoTotalItems));
            Assert.Equal(2, sink.Ana(B.AoPageCount));
            Assert.Equal(1, sink.Ana(B.AoPage));
            Assert.Equal(2, sink.Ana(B.AoItemsOnPage));
            Assert.Equal("Go Your Own Way", sink.Ser(B.SoItemTextBase + 1));
            Assert.Equal("The Weeknd", sink.Ser(B.SoItemSubBase + 2));
            Assert.Equal("http://art/1.jpg", sink.Ser(B.SoItemIconBase + 2));
            Assert.True(sink.Dig(B.DoHasNext));
            Assert.False(sink.Dig(B.DoHasPrev));

            browser.Digital(B.DiPageNext, true);
            Assert.Equal(2, sink.Ana(B.AoPage));
            Assert.Equal(1, sink.Ana(B.AoItemsOnPage));
            Assert.Equal("The Look Of Love", sink.Ser(B.SoItemTextBase + 1));
            Assert.Equal("", sink.Ser(B.SoItemTextBase + 2));

            browser.Analog(B.AiItemClicked, 1);         // absolute index 2
            var rq = rig.Net.Named("ReplaceQueue").Single();
            Assert.Equal("2", rq.Arg("StartingIndex"));
            Assert.Equal(3, DidlDoc.Parse(rq.Arg("Items")).Count);
            Assert.Equal(new[] { "Items", "StartingIndex" }, rq.Args.Select(a => a.Key));
        }

        [Fact]
        public void Songlists_drill_into_tracks_and_back()
        {
            var (rig, dev, player, browser, sink) = Setup(pageSize: 5);
            dev.SonglistsJson = SoapResponse.Parse("GetCollectedSonglists", 200, Fixtures.Text("MiyueLibrary.GetCollectedSonglists.xml")).Get("Songlists");
            dev.SonglistTracks["23"] = SoapResponse.Parse("GetSonglistTracks", 200, Fixtures.Text("android.MiyueLibrary.GetSonglistTracks.23.xml")).Get("Result");
            browser.Digital(B.DiListSonglists, true);
            Assert.Equal("日本鬼子", sink.Ser(B.SoItemTextBase + 1));
            Assert.Equal("2 首", sink.Ser(B.SoItemSubBase + 1));
            Assert.False(sink.Dig(B.DoCanBack));

            browser.Digital(B.DiItemBase + 1, true);
            Assert.Equal("23", rig.Net.Named("GetSonglistTracks").Single().Arg("SonglistId"));
            Assert.Equal("日本鬼子", sink.Ser(B.SoListTitle));
            Assert.Equal("风马牛", sink.Ser(B.SoItemTextBase + 1));
            Assert.True(sink.Dig(B.DoCanBack));

            browser.Digital(B.DiPlayAll, true);
            Assert.Equal("0", rig.Net.Named("ReplaceQueue").Single().Arg("StartingIndex"));

            browser.Digital(B.DiBack, true);
            Assert.Equal("歌单 Songlists", sink.Ser(B.SoListTitle));
            Assert.False(sink.Dig(B.DoCanBack));
        }

        [Fact]
        public void Queue_pages_come_from_the_device_and_select_seeks()
        {
            var (rig, dev, player, browser, sink) = Setup(pageSize: 2);
            dev.CurrentIndex = 2;
            browser.Digital(B.DiListQueue, true);
            var gq = rig.Net.Named("GetQueue").Last();
            Assert.Equal("0", gq.Arg("StartIndex"));
            Assert.Equal("2", gq.Arg("RequestedCount"));
            browser.Digital(B.DiPageNext, true);
            gq = rig.Net.Named("GetQueue").Last();
            Assert.Equal("2", gq.Arg("StartIndex"));
            Assert.Equal("The Look Of Love", sink.Ser(B.SoItemTextBase + 1));
            Assert.True(sink.Dig(B.DoItemCurrentBase + 1));
            browser.Digital(B.DiItemBase + 1, true);
            Assert.Equal("2", rig.Net.Named("SeekToTrack").Single().Arg("Index"));
        }

        [Fact]
        public void Radios_get_synthesized_stream_urls()
        {
            var (rig, dev, player, browser, sink) = Setup();
            dev.CollectedRadiosDidl = DidlDoc.Header +
                "<item id=\"20003\" parentID=\"-1\" restricted=\"1\"><dc:title>电台</dc:title><upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
                "<miyue:songSrc>8</miyue:songSrc><miyue:songId>20003</miyue:songId><res protocolInfo=\"http-get:*:audio/mpeg:*\" duration=\"0:00:00\"></res></item>" +
                DidlDoc.Footer;
            browser.Digital(B.DiListRadios, true);
            browser.Digital(B.DiItemBase + 1, true);
            Assert.Contains("http://ls.qingting.fm/live/20003/24k.m3u8", rig.Net.Named("ReplaceQueue").Single().Arg("Items"));
        }

        [Fact]
        public void Direct_link_track_without_url_is_refused()
        {
            var (rig, dev, player, browser, sink) = Setup();
            dev.CollectedMusicDidl = DidlDoc.Header +
                "<item id=\"9\" parentID=\"-1\" restricted=\"1\"><dc:title>儿歌</dc:title><upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
                "<miyue:songSrc>10</miyue:songSrc><miyue:songId>9</miyue:songId><res protocolInfo=\"http-get:*:audio/mpeg:*\"></res></item>" +
                DidlDoc.Footer;
            browser.Digital(B.DiListLiked, true);
            browser.Digital(B.DiItemBase + 1, true);
            Assert.Empty(rig.Net.Named("ReplaceQueue"));
            Assert.Contains("songSrc=10", sink.Ser(B.SoStatus));
        }

        [Fact]
        public void Scenes_list_executes()
        {
            var (rig, dev, player, browser, sink) = Setup(pageSize: 10);
            browser.Digital(B.DiListScenes, true);
            Assert.Equal("以后还会", sink.Ser(B.SoItemTextBase + 1));
            browser.Digital(B.DiItemBase + 3, true);
            Assert.Equal("3", rig.Net.Named("ExecuteSensor").Single().Arg("SensorId"));
        }

        [Fact]
        public void Queue_list_follows_track_changes()
        {
            var (rig, dev, player, browser, sink) = Setup(pageSize: 3);
            browser.Digital(B.DiListQueue, true);
            Assert.True(sink.Dig(B.DoItemCurrentBase + 1));
            dev.CurrentIndex = 1;
            rig.Clock.Advance(4000);
            Assert.False(sink.Dig(B.DoItemCurrentBase + 1));
            Assert.True(sink.Dig(B.DoItemCurrentBase + 2));
        }

        [Fact]
        public void Load_failure_is_reported()
        {
            var (rig, dev, player, browser, sink) = Setup();
            dev.TransportDown = true;
            browser.Digital(B.DiListLiked, true);
            Assert.Contains("Load failed", sink.Ser(B.SoStatus));
            Assert.False(sink.Dig(B.DoBusy));
            Assert.Equal(0, sink.Ana(B.AoItemsOnPage));
        }
    }
}
