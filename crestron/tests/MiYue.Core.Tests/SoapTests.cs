using System.Text;
using MiYue.Core.Soap;
using MiYue.Core.Tests.Support;
using MiYue.Core.Util;
using Xunit;

namespace MiYue.Core.Tests
{
    public class SoapEnvelopeTests
    {
        [Fact]
        public void Envelope_matches_what_the_firmware_accepts()
        {
            var env = SoapEnvelope.Build("urn:miyue-hk:service:MiyueQueue:1", "SeekToTrack", new SoapArgs().Add("Index", 3));
            Assert.Equal(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:SeekToTrack xmlns:u=\"urn:miyue-hk:service:MiyueQueue:1\"><Index>3</Index></u:SeekToTrack></s:Body></s:Envelope>",
                env);
            Assert.Equal("\"urn:miyue-hk:service:MiyueQueue:1#SeekToTrack\"", SoapEnvelope.SoapActionHeader("urn:miyue-hk:service:MiyueQueue:1", "SeekToTrack"));
            Assert.Equal("text/xml; charset=\"utf-8\"", SoapEnvelope.ContentType);
        }

        [Fact]
        public void Arguments_keep_their_order_and_are_escaped()
        {
            var args = new SoapArgs().Add("TargetUUID", "uuid:x").Add("GroupID", "g").Add("MasterIp", "1.2.3.4")
                .Add("MulticastAddr", "239.10.1.2").Add("Role", "Master");
            var env = SoapEnvelope.Build("urn:miyue-hk:service:MiyueGroup:1", "SetGroupConfig", args);
            Assert.Contains("<TargetUUID>uuid:x</TargetUUID><GroupID>g</GroupID><MasterIp>1.2.3.4</MasterIp><MulticastAddr>239.10.1.2</MulticastAddr><Role>Master</Role>", env);

            var tts = SoapEnvelope.Build("urn:miyue-hk:service:MiyueSystem:1", "PlayTTS", new SoapArgs().Add("Text", "a<b & \"c\" '中文'").Add("Volume", 20));
            Assert.Contains("<Text>a&lt;b &amp; &quot;c&quot; &apos;中文&apos;</Text><Volume>20</Volume>", tts);
        }

        [Fact]
        public void Didl_items_arg_is_escaped_once()
        {
            var didl = "<DIDL-Lite><item id=\"1\"><dc:title>A&amp;B</dc:title></item></DIDL-Lite>";
            var env = SoapEnvelope.Build("urn:miyue-hk:service:MiyueQueue:1", "ReplaceQueue", new SoapArgs().Add("Items", didl).Add("StartingIndex", 0));
            Assert.Contains("<Items>&lt;DIDL-Lite&gt;&lt;item id=&quot;1&quot;&gt;&lt;dc:title&gt;A&amp;amp;B&lt;/dc:title&gt;", env);
            // and the fake device (like the firmware) gets the original back
            var args = FakeNetwork.ParseArgs(env, "ReplaceQueue");
            Assert.Equal(didl, args[0].Value);
        }
    }

    public class SoapResponseTests
    {
        private static SoapResult Fx(string file, string action) => SoapResponse.Parse(action, 200, Fixtures.Text(file));

        [Fact]
        public void Parses_standard_service_fixtures()
        {
            Assert.Equal("PAUSED_PLAYBACK", Fx("AVT.GetTransportInfo.xml", "GetTransportInfo").Get("CurrentTransportState"));
            Assert.Equal(0, Fx("RC.GetVolume.xml", "GetVolume").GetInt("CurrentVolume", -1));
            Assert.Equal("0", Fx("RC.GetMute.xml", "GetMute").Get("CurrentMute"));
            Assert.Equal("REPEAT_ALL", Fx("MiyueQueue.GetPlayMode.xml", "GetPlayMode").Get("PlayMode"));
        }

        [Fact]
        public void Parses_group_info_with_chinese_name()
        {
            var r = Fx("MiyueGroup.GetGroupInfo.xml", "GetGroupInfo");
            Assert.True(r.Ok);
            Assert.Equal("None", r.Get("Role"));
            Assert.Equal("064c69619ceb4fe393a795de075f", r.Get("GroupID"));
            Assert.Equal("", r.Get("MasterIp"));
            Assert.Equal("239.10.100.214", r.Get("MulticastAddr"));
            Assert.Equal("餐厅", r.Get("DeviceName"));
        }

        [Fact]
        public void Position_info_metadata_comes_back_unescaped()
        {
            var r = Fx("AVT.GetPositionInfo.xml", "GetPositionInfo");
            Assert.StartsWith("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\"", r.Get("TrackMetaData"));
            Assert.Equal("00:01:25", r.Get("RelTime"));
            Assert.Equal("00:04:20", r.Get("TrackDuration"));
        }

        [Fact]
        public void Queue_fixture_has_counts_and_index()
        {
            var r = Fx("android.MiyueQueue.GetQueue.xml", "GetQueue");
            Assert.Equal(3, r.GetInt("Count", 0));
            Assert.Equal(53, r.GetInt("Total", 0));
            Assert.Equal(2, r.GetInt("CurrentIndex", -1));
        }

        [Theory]
        [InlineData(501, "Action Failed")]  // Android (pupnp rewrites unknown actions)
        [InlineData(401, "Invalid Action")] // Linux
        public void Unknown_action_faults_are_recognised(int code, string desc)
        {
            string body = Encoding.UTF8.GetString(FakeMiyue.Fault(code, desc).Body);
            var r = SoapResponse.Parse("GetTimeline", 500, body);
            Assert.True(r.IsFault);
            Assert.True(r.IsUnsupportedAction);
            Assert.Equal(code, r.FaultCode);
            Assert.Equal(desc, r.Message);
        }

        [Fact]
        public void Empty_response_element_and_empty_200_are_success()
        {
            var r = SoapResponse.Parse("RemoveTrack", 200,
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:RemoveTrackResponse xmlns:u=\"urn:miyue-hk:service:MiyueQueue:1\"/></s:Body></s:Envelope>");
            Assert.True(r.Ok);
            Assert.Empty(r.Args);
            Assert.True(SoapResponse.Parse("Play", 200, "").Ok);
        }

        [Fact]
        public void Http_404_without_fault_is_not_a_genuine_fault()
        {
            var r = SoapResponse.Parse("GetVolume", 404, "");
            Assert.Equal(SoapOutcome.HttpError, r.Outcome);
            Assert.False(r.IsFault);
            Assert.Equal(404, r.HttpStatus);
        }

        [Fact]
        public void Cdata_and_self_closing_children()
        {
            var r = SoapResponse.Parse("GetX", 200,
                "<s:Envelope><s:Body><u:GetXResponse xmlns:u=\"x\"><A><![CDATA[<raw>&amp;]]></A><B/><C>1 &amp; 2</C></u:GetXResponse></s:Body></s:Envelope>");
            Assert.Equal("<raw>&amp;", r.Get("A"));
            Assert.Equal("", r.Get("B"));
            Assert.Equal("1 & 2", r.Get("C"));
        }

        [Fact]
        public void Mixed_utf8_and_raw_gbk_bytes_decode()
        {
            Fixtures.EnsureGbk();
            var gbk = Encoding.GetEncoding(936).GetBytes("音乐");
            var pre = Encoding.UTF8.GetBytes("<Result>中文 ");
            var post = Encoding.UTF8.GetBytes("</Result>");
            var all = new byte[pre.Length + gbk.Length + post.Length];
            pre.CopyTo(all, 0);
            gbk.CopyTo(all, pre.Length);
            post.CopyTo(all, pre.Length + gbk.Length);
            Assert.Equal("<Result>中文 音乐</Result>", TextDecoder.DecodeMixed(all));
            Assert.Equal("plain", TextDecoder.DecodeMixed(Encoding.UTF8.GetBytes("plain")));
        }
    }
}
