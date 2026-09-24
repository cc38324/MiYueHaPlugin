using System.Linq;
using MiYue.Core.Model;
using MiYue.Core.Soap;
using MiYue.Core.Tests.Support;
using MiYue.Core.Upnp;
using MiYue.Core.Util;
using Xunit;

namespace MiYue.Core.Tests
{
    /// <summary>songSrc skip gating - HA tests/test_song_src.py ported.</summary>
    public class SongSrcTests
    {
        [Theory]
        [InlineData(0)] [InlineData(4)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(15)] [InlineData(21)]
        public void Queue_sources_can_skip(int src) => Assert.True(SongSrc.CanSkip(src));

        [Theory]
        [InlineData(9)] [InlineData(20)]
        public void Reverse_control_sources_can_skip(int src) => Assert.True(SongSrc.CanSkip(src));

        [Theory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(8)] [InlineData(14)] [InlineData(16)]
        public void Single_streams_cannot_skip(int src) => Assert.False(SongSrc.CanSkip(src));

        [Fact]
        public void Sync_slave_and_unknown_are_permissive()
        {
            Assert.True(SongSrc.CanSkip(13));
            Assert.True(SongSrc.CanSkip(SongSrc.Unknown));
            Assert.True(SongSrc.CanSkip(-7));
        }

        [Fact]
        public void Sentinel_maps_to_owner()
        {
            Assert.Equal(8, SongSrc.Effective(null, -100, null));
            Assert.Equal(20, SongSrc.Effective(null, -102, null));
            Assert.Equal(9, SongSrc.Effective(null, -106, null));
            Assert.Equal(13, SongSrc.Effective(null, -105, null));
        }

        [Theory]
        [InlineData(-107)] [InlineData(-108)] [InlineData(-109)]
        public void Transient_sentinels_fall_through_to_track(int idx)
        {
            Assert.Equal(8, SongSrc.Effective(null, idx, 8));
            Assert.Equal(15, SongSrc.Effective(null, idx, 15));
            Assert.Equal(SongSrc.Unknown, SongSrc.Effective(null, idx, null));
        }

        [Fact]
        public void Active_external_input_wins()
        {
            Assert.Equal(1, SongSrc.Effective("AUX", 3, 15));
            Assert.Equal(2, SongSrc.Effective("SPDIF", 3, 15));
            Assert.True(SongSrc.CanSkip(SongSrc.Effective("Bluetooth", -100, null)));
            Assert.Equal(15, SongSrc.Effective(null, 3, 15));
            Assert.Equal(8, SongSrc.Effective(null, 0, 8));
            Assert.Equal(SongSrc.Unknown, SongSrc.Effective(null, -1, null));
        }
    }

    public class ModelTests
    {
        [Fact]
        public void External_inputs_fixture()
        {
            var r = SoapResponse.Parse("GetExternalInputs", 200, Fixtures.Text("MiyueAudioSource.GetExternalInputs.xml"));
            var x = ExternalInputs.From(r);
            Assert.True(x.WithSpdif);
            Assert.True(x.WithBluetooth);
            Assert.False(x.WithAux);
            Assert.Null(x.ActiveInput);
            Assert.Equal("Music", x.ActiveSource);
            x.OpenSpdif = true;
            Assert.Equal("SPDIF", x.ActiveSource);
        }

        [Fact]
        public void Group_info_role_none_vetoes_gid()
        {
            var g = GroupInfo.From(SoapResponse.Parse("GetGroupInfo", 200, Fixtures.Text("MiyueGroup.GetGroupInfo.xml")));
            Assert.True(g.IsStandalone);
            Assert.Equal("064c69619ceb4fe393a795de075f", g.GroupId);
            Assert.Equal("", g.EffectiveGroupId);
            Assert.Equal("uuid:064c6961-9ceb-4fe3-93a7-95de075feb9c", g.DeviceUuid);
            g.Role = "Master";
            Assert.Equal("064c69619ceb4fe393a795de075f", g.EffectiveGroupId);
            g.GroupId = "null";
            Assert.Equal("", g.EffectiveGroupId);
        }

        [Fact]
        public void Scenes_only_open_ones_in_device_order()
        {
            var json = SoapResponse.Parse("ListSensors", 200, Fixtures.Text("MiyueSensor.ListSensors.xml")).Get("Sensors");
            var scenes = Scene.ParseOpen(json);
            Assert.True(scenes.Count >= 4);
            Assert.Equal("1", scenes[0].Id);
            Assert.Equal("以后还会", scenes[0].Name);
            Assert.Equal("QQ", scenes[0].Cmd);
            var closed = Scene.ParseOpen("[{\"id\":\"9\",\"cmdName\":\"x\",\"isOpen\":0},{\"id\":\"10\",\"isOpen\":\"1\"}]");
            Assert.Single(closed);
            Assert.Equal("#10", closed[0].Name);
        }

        [Fact]
        public void Songlists_and_boards_json()
        {
            var sl = Songlist.Parse(SoapResponse.Parse("GetCollectedSonglists", 200, Fixtures.Text("MiyueLibrary.GetCollectedSonglists.xml")).Get("Songlists"));
            Assert.Equal(8, sl.Count);
            Assert.Equal("23", sl[0].Id);
            Assert.Equal("日本鬼子", sl[0].Name);
            Assert.Equal(2, sl[0].Count);
            var boards = Songlist.Parse(SoapResponse.Parse("GetCollectedBoards", 200, Fixtures.Text("MiyueLibrary.GetCollectedBoards.xml")).Get("Boards"));
            Assert.Equal(new[] { "4", "6" }, boards.Select(b => b.Id).ToArray());
            Assert.Empty(Songlist.Parse("null"));
            Assert.Empty(Songlist.Parse("{broken"));
        }

        [Fact]
        public void Play_mode_normalize_and_cycle()
        {
            Assert.Equal("REPEAT_ONE", PlayModes.Normalize("repeat_one"));
            Assert.Equal("NORMAL", PlayModes.Normalize("FOO"));
            Assert.Equal("NORMAL", PlayModes.Normalize(""));
            Assert.Equal("REPEAT_ALL", PlayModes.Next("NORMAL"));
            Assert.Equal("REPEAT_ONE", PlayModes.Next("REPEAT_ALL"));
            Assert.Equal("SHUFFLE", PlayModes.Next("REPEAT_ONE"));
            Assert.Equal("NORMAL", PlayModes.Next("SHUFFLE"));
            Assert.Equal("REPEAT_ALL", PlayModes.Next(null));
        }
    }

    public class UtilTests
    {
        [Fact]
        public void Time_parsing_accepts_both_formats()
        {
            Assert.Equal(222, TimeText.ToSeconds("0:03:42"));
            Assert.Equal(3600, TimeText.ToSeconds("1:00:00"));
            Assert.Equal(43020, TimeText.ToSeconds("11:57:00"));
            Assert.Equal(85, TimeText.ToSeconds("00:01:25"));
            Assert.Equal(394, TimeText.ToSeconds("0:06:34.000"));
            Assert.Equal(0, TimeText.ToSeconds("NOT_IMPLEMENTED"));
            Assert.Equal(0, TimeText.ToSeconds(""));
            Assert.Equal(0, TimeText.ToSeconds("garbage"));
            Assert.Equal("0:01:05", TimeText.ToHms(65));
            Assert.Equal("1:00:00", TimeText.ToHms(3600));
            Assert.Equal("1:05", TimeText.ToDisplay(65));
            Assert.Equal("1:00:01", TimeText.ToDisplay(3601));
        }

        [Fact]
        public void Ipv4_validation_matches_ha()
        {
            Assert.True(Ipv4.IsValid("192.168.1.21"));
            Assert.False(Ipv4.IsValid(""));
            Assert.False(Ipv4.IsValid("0.0.0.0"));
            Assert.False(Ipv4.IsValid("255.255.255.255"));
            Assert.False(Ipv4.IsValid("127.0.0.1"));
            Assert.False(Ipv4.IsValid("fe80::1"));
            Assert.False(Ipv4.IsValid("speaker.local"));
            Assert.False(Ipv4.IsValid("1.2.3.256"));
        }

        [Fact]
        public void Mini_json()
        {
            var list = MiniJson.ParseObjectList("[{\"id\":\"4\",\"n\":1.5,\"b\":true,\"z\":null,\"s\":\"a\\u4e2d\\n\"},{\"id\":7}]");
            Assert.Equal(2, list.Count);
            Assert.Equal("4", MiniJson.Str(list[0], "id"));
            Assert.Equal("7", MiniJson.Str(list[1], "id"));
            Assert.Equal("1.5", MiniJson.Str(list[0], "n"));
            Assert.Equal("a中\n", MiniJson.Str(list[0], "s"));
            Assert.Equal("", MiniJson.Str(list[0], "z"));
            Assert.Equal(1, MiniJson.Int(list[0], "b", 0));
            Assert.Single(MiniJson.ParseObjectList("{\"id\":1}"));
            Assert.Empty(MiniJson.ParseObjectList("null"));
        }

        [Fact]
        public void Xml_unescape_numeric_and_unknown_entities()
        {
            Assert.Equal("a&b<c>\"'中", XmlText.Unescape("a&amp;b&lt;c&gt;&quot;&apos;&#20013;"));
            Assert.Equal("中", XmlText.Unescape("&#x4e2d;"));
            Assert.Equal("&nbsp;x", XmlText.Unescape("&nbsp;x"));
            Assert.Equal("a & b", XmlText.Unescape("a & b"));
            Assert.Equal("a&amp;b", XmlText.Scrub("a&b\u0001"));
        }
    }

    public class UpnpTests
    {
        [Fact]
        public void Android_description()
        {
            var d = DeviceDescription.Parse(Fixtures.Text("android_description.xml"));
            Assert.NotNull(d);
            Assert.Equal("uuid:064c6961-9ceb-4fe3-93a7-95de075feb9c", d.Udn);
            Assert.Equal("餐厅", d.FriendlyName);
            Assert.Equal("/_control/MiyueQueue", d.Endpoints["MiyueQueue"].ControlPath);
            Assert.Equal("urn:miyue-hk:service:MiyueGroup:1", d.Endpoints["MiyueGroup"].Type);
            Assert.Equal("/MediaRenderer/AVTransport/Control", d.Endpoints["AVTransport"].ControlPath);
            Assert.Equal("urn:schemas-upnp-org:service:RenderingControl:1", d.Endpoints["RenderingControl"].Type);
        }

        [Fact]
        public void Linux_description()
        {
            var d = DeviceDescription.Parse(Fixtures.Text("linux_description.xml"));
            Assert.NotNull(d);
            Assert.StartsWith("uuid:", d.Udn);
            Assert.DoesNotContain("-mr", d.Udn);
            foreach (var name in Services.Defaults.Keys) Assert.True(d.Endpoints.ContainsKey(name), name);
        }

        [Fact]
        public void Not_a_description()
        {
            Assert.Null(DeviceDescription.Parse("<html>nope</html>"));
            Assert.Null(DeviceDescription.Parse(""));
        }

        [Fact]
        public void Probe_order_puts_last_good_first()
        {
            var order = PortPolicy.ProbeOrder(49503);
            Assert.Equal(49503, order[0]);
            Assert.Equal(49495, order[1]);
            Assert.Equal(26, order.Count);
            Assert.Equal(49520, order[order.Count - 1]);
            Assert.Equal(26, PortPolicy.ProbeOrder(0).Count);
            Assert.Equal(49495, PortPolicy.ProbeOrder(0)[0]);
        }

        [Fact]
        public void Backoff_5_15_30_60_then_sticks()
        {
            Assert.Equal(new[] { 5, 15, 30, 60, 60, 60 }, Enumerable.Range(1, 6).Select(PortPolicy.BackoffFor).ToArray());
        }

        [Fact]
        public void Service_name_from_type()
        {
            Assert.Equal("MiyueQueue", DeviceDescription.ServiceNameOf("urn:miyue-hk:service:MiyueQueue:1"));
            Assert.Equal("AVTransport", DeviceDescription.ServiceNameOf("urn:schemas-upnp-org:service:AVTransport:1"));
            Assert.Null(DeviceDescription.ServiceNameOf("junk"));
        }
    }
}
