using System.Linq;
using MiYue.Core.Engine;
using MiYue.Core.Tests.Support;
using Xunit;
using S = MiYue.Core.Engine.PlayerSignals;

namespace MiYue.Core.Tests
{
    public class PlayerEngineTests
    {
        private const string Ip = "192.168.1.47";

        [Fact]
        public void Resolves_a_drifted_port_and_publishes_state()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip, 49498);
            dev.CurrentIndex = 2;
            dev.Transport = "PLAYING";
            var (p, sink) = rig.StartPlayer(Ip);

            Assert.Equal(new[] { 49495, 49496, 49497, 49498 }.Select(x => "http://" + Ip + ":" + x + "/description.xml"), rig.Net.Gets);
            Assert.True(sink.Dig(S.DoOnline));
            Assert.Equal(49498, sink.Ana(S.AoPort));
            Assert.Equal("餐厅", sink.Ser(S.SoDeviceName));
            Assert.Equal("M100", sink.Ser(S.SoModel));
            Assert.True(sink.Dig(S.DoPlaying));
            Assert.False(sink.Dig(S.DoPaused));
            Assert.Equal("PLAYING", sink.Ser(S.SoTransportState));
            Assert.Equal(25, sink.Ana(S.AoVolume));
            Assert.Equal((ushort)System.Math.Round(25 * 655.35), sink.Ana(S.AoVolumeRaw));
            Assert.Equal("The Look Of Love", sink.Ser(S.SoTitle));
            Assert.Equal("Diana Krall", sink.Ser(S.SoArtist));
            Assert.Equal("http://art/2.jpg", sink.Ser(S.SoCoverUrl));
            Assert.Equal(3, sink.Ana(S.AoQueueIndex)); // 1-based
            Assert.Equal(3, sink.Ana(S.AoQueueTotal));
            Assert.Equal(260, sink.Ana(S.AoDuration));
            Assert.Equal("4:20", sink.Ser(S.SoDurationText));
            Assert.True(sink.Dig(S.DoModeRepeatAll));
            Assert.Equal("REPEAT_ALL", sink.Ser(S.SoPlayMode));
            Assert.True(sink.Dig(S.DoSourceMusic));
            Assert.True(sink.Dig(S.DoHasSpdif));
            Assert.False(sink.Dig(S.DoHasAux));
            Assert.Equal("None", sink.Ser(S.SoGroupRole));
            Assert.True(sink.Ana(S.AoSceneCount) >= 4);
            Assert.Equal("以后还会", sink.Ser(S.SoSceneNameBase + 1));
            Assert.True(sink.Dig(S.DoCanSkip));
        }

        [Fact]
        public void Remembers_the_last_good_port_across_restarts()
        {
            var rig = new Rig();
            rig.AddDevice(Ip, 49503);
            var (p, _) = rig.StartPlayer(Ip);
            p.Stop();
            rig.Net.Gets.Clear();
            var (p2, sink2) = rig.StartPlayer(Ip);
            Assert.Equal("http://" + Ip + ":49503/description.xml", rig.Net.Gets.First());
            Assert.Single(rig.Net.Gets);
            Assert.True(sink2.Dig(S.DoOnline));
        }

        [Fact]
        public void Poll_cycles_never_overlap()
        {
            var rig = new Rig();
            rig.AddDevice(Ip);
            var (p, _) = rig.StartPlayer(Ip, pollSeconds: 1);
            int before = rig.Net.Named("GetTransportInfo").Count();

            rig.Net.Hold.Add("GetQueue");     // the next cycle stalls on GetQueue
            rig.Clock.Advance(1100);
            Assert.True(p.Polling);
            int during = rig.Net.Named("GetTransportInfo").Count();
            Assert.Equal(before + 1, during);
            rig.Clock.Advance(12_000);          // many poll intervals, still stuck -> no new cycle
                                                // (under the 16 s transfer watchdog)
            Assert.Equal(during, rig.Net.Named("GetTransportInfo").Count());
            Assert.True(rig.Net.MaxConcurrent <= RequestQueue.DefaultMaxInflight);

            rig.Net.Hold.Clear();
            rig.Net.ReleaseAll();
            Assert.False(p.Polling);
            rig.Clock.Advance(1100);
            Assert.Equal(during + 1, rig.Net.Named("GetTransportInfo").Count());
        }

        [Fact]
        public void Stuck_cycle_is_reset_by_the_watchdog()
        {
            var rig = new Rig();
            rig.AddDevice(Ip);
            var (p, _) = rig.StartPlayer(Ip, pollSeconds: 1);
            rig.Net.Hold.Add("GetPositionInfo");
            rig.Clock.Advance(1100);
            Assert.True(p.Polling);
            int n = rig.Net.Named("GetTransportInfo").Count();
            rig.Clock.Advance(PlayerEngine.CycleWatchdogMs + 2000);
            Assert.True(rig.Net.Named("GetTransportInfo").Count() > n);
        }

        [Fact]
        public void Goes_offline_after_transport_failures_and_re_resolves_on_a_new_port()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip, pollSeconds: 1);
            Assert.True(sink.Dig(S.DoOnline));

            dev.TransportDown = true;           // requests time out
            rig.Clock.Advance(5000);
            Assert.False(sink.Dig(S.DoOnline));
            Assert.Equal("OFFLINE", sink.Ser(S.SoTransportState));
            Assert.Equal("", sink.Ser(S.SoTitle));

            dev.TransportDown = false;
            dev.Port = 49501;                   // came back on a drifted port
            rig.Clock.Advance(6000);            // first backoff is 5 s
            Assert.True(sink.Dig(S.DoOnline));
            Assert.Equal(49501, sink.Ana(S.AoPort));
        }

        [Fact]
        public void Failed_first_resolve_retries_with_backoff()
        {
            var rig = new Rig();
            var (p, sink) = rig.StartPlayer(Ip);
            Assert.False(sink.Dig(S.DoOnline));
            Assert.Contains("Not found", sink.Ser(S.SoStatus));
            rig.AddDevice(Ip, 49500);
            rig.Clock.Advance(5500);
            Assert.True(sink.Dig(S.DoOnline));
        }

        [Fact]
        public void Outputs_are_only_sent_when_they_change()
        {
            var rig = new Rig();
            rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip, pollSeconds: 1);
            rig.Clock.Advance(3000);
            sink.Events.Clear();
            rig.Clock.Advance(5000);            // several identical cycles (device PAUSED -> no position ticks)
            Assert.Empty(sink.Events);
            p.Digital(S.DiRefresh, true);       // Refresh re-sends everything once
            Assert.Contains("D" + S.DoOnline + "=1", sink.Events);
        }

        [Fact]
        public void Next_uses_AVTransport_Next()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (p, _) = rig.StartPlayer(Ip);
            p.Digital(S.DiNext, true);
            Assert.Single(rig.Net.Named("Next"));
            Assert.Empty(rig.Net.Named("SeekToTrack"));
            Assert.Equal(1, dev.CurrentIndex);
        }

        [Fact]
        public void Next_fault_falls_back_to_SeekToTrack_from_a_fresh_index()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            dev.SupportsNext = false;
            dev.CurrentIndex = 1;
            var (p, _) = rig.StartPlayer(Ip);
            p.Digital(S.DiNext, true);
            Assert.Equal("2", rig.Net.Named("SeekToTrack").Single().Arg("Index"));
            p.Digital(S.DiPrevious, true);      // now at 2 -> 1
            Assert.Equal("1", rig.Net.Named("SeekToTrack").Last().Arg("Index"));
        }

        [Fact]
        public void Next_fallback_does_not_wrap_past_the_end()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            dev.SupportsNext = false;
            dev.CurrentIndex = 2;
            var (p, _) = rig.StartPlayer(Ip);
            p.Digital(S.DiNext, true);
            Assert.Empty(rig.Net.Named("SeekToTrack"));
        }

        [Fact]
        public void Next_transport_error_never_blind_seeks()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip);
            dev.TransportDown = true;
            p.Digital(S.DiNext, true);
            Assert.Single(rig.Net.Named("Next"));
            Assert.Empty(rig.Net.Named("SeekToTrack"));
            Assert.Contains("Next", sink.Ser(S.SoLastError));
        }

        [Fact]
        public void Slave_skip_is_gated_unless_linux_sentinel()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            dev.Role = "Slave";
            dev.CurrentIndex = 1;              // Android slave: stale raw index
            var (p, sink) = rig.StartPlayer(Ip);
            Assert.False(sink.Dig(S.DoCanSkip));
            p.Digital(S.DiNext, true);
            Assert.Empty(rig.Net.Named("Next"));

            dev.CurrentIndex = -105;           // Linux slave forwards Next to its master
            rig.Clock.Advance(4000);
            Assert.True(sink.Dig(S.DoCanSkip));
            p.Digital(S.DiNext, true);
            Assert.Single(rig.Net.Named("Next"));
        }

        [Fact]
        public void Radio_sentinel_grays_out_skip()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            dev.CurrentIndex = -100;
            var (p, sink) = rig.StartPlayer(Ip);
            Assert.False(sink.Dig(S.DoCanSkip));
            Assert.Equal(0, sink.Ana(S.AoQueueIndex));
        }

        [Fact]
        public void Tts_always_sends_volume_1_to_100()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            dev.Volume = 0;
            var (p, _) = rig.StartPlayer(Ip);
            p.Serial(S.SiTtsText, "测试");                 // volume 0 -> fallback 30
            Assert.Equal("30", rig.Net.Named("PlayTTS").Last().Arg("Volume"));
            p.Analog(S.AiTtsVolume, 150);
            p.Serial(S.SiTtsText, "测试二");
            Assert.Equal("100", rig.Net.Named("PlayTTS").Last().Arg("Volume"));
            p.Analog(S.AiTtsVolume, 0);
            p.Analog(S.AiVolumeSet, 42);
            p.Serial(S.SiTtsText, "三");                   // current volume
            var last = rig.Net.Named("PlayTTS").Last();
            Assert.Equal("42", last.Arg("Volume"));
            Assert.Equal("三", last.Arg("Text"));
            Assert.Equal(new[] { "Text", "Volume" }, last.Args.Select(a => a.Key));
        }

        [Fact]
        public void Tts_is_held_out_of_transport_state()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip, pollSeconds: 1);
            Assert.True(sink.Dig(S.DoPaused));
            p.Serial(S.SiTtsText, "hello");
            dev.Transport = "PLAYING";         // the announcement flips the state briefly
            rig.Clock.Advance(1000);
            Assert.True(sink.Dig(S.DoPaused));
        }

        [Fact]
        public void Volume_commands_are_clamped_integers_and_coalesced()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip);
            rig.Net.Hold.Add("SetVolume");
            p.Analog(S.AiVolumeSet, 10);
            p.Analog(S.AiVolumeSet, 20);
            p.Analog(S.AiVolumeSet, 250);       // clamps to 100
            Assert.Single(rig.Net.Named("SetVolume"));   // one in flight, the rest coalesced
            Assert.Equal(100, sink.Ana(S.AoVolume));     // optimistic feedback
            rig.Net.Hold.Clear();
            rig.Net.ReleaseAll();
            Assert.Equal(new[] { "10", "100" }, rig.Net.Named("SetVolume").Select(c => c.Arg("DesiredVolume")));
            Assert.Equal(new[] { "InstanceID", "Channel", "DesiredVolume" }, rig.Net.Named("SetVolume").First().Args.Select(a => a.Key));
            p.Analog(S.AiVolumeSetRaw, 32768);
            Assert.Equal("50", rig.Net.Named("SetVolume").Last().Arg("DesiredVolume"));
        }

        [Fact]
        public void Volume_up_ramps_while_held_and_stops_on_release()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            dev.Volume = 20;
            var (p, sink) = rig.StartPlayer(Ip);
            p.Digital(S.DiVolUp, true);
            Assert.Equal(25, sink.Ana(S.AoVolume));
            rig.Clock.Advance(PlayerEngine.VolumeRampMs * 2 + 10);
            Assert.Equal(35, sink.Ana(S.AoVolume));
            p.Digital(S.DiVolUp, false);
            rig.Clock.Advance(3000);
            Assert.Equal(35, dev.Volume);
            Assert.Equal(35, sink.Ana(S.AoVolume));
        }

        [Fact]
        public void Mute_mode_source_and_input_commands()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip);
            p.Digital(S.DiMuteToggle, true);
            Assert.Equal("1", rig.Net.Named("SetMute").Last().Arg("DesiredMute"));
            Assert.True(sink.Dig(S.DoMuted));
            p.Digital(S.DiModeCycle, true);                 // REPEAT_ALL -> REPEAT_ONE
            Assert.Equal("REPEAT_ONE", rig.Net.Named("SetPlayMode").Last().Arg("PlayMode"));
            Assert.True(sink.Dig(S.DoModeRepeatOne));
            p.Digital(S.DiSourceSpdif, true);
            Assert.Equal("SPDIF", rig.Net.Named("SelectSource").Last().Arg("Source"));
            p.Digital(S.DiInputBluetoothOn, true);
            var io = rig.Net.Named("SetInputOpen").Last();
            Assert.Equal("Bluetooth", io.Arg("Source"));
            Assert.Equal("1", io.Arg("On"));
            p.Digital(S.DiInputSpdifOff, true);
            Assert.Equal("0", rig.Net.Named("SetInputOpen").Last().Arg("On"));
            p.Analog(S.AiSeekSeconds, 75);
            var seek = rig.Net.Named("Seek").Last();
            Assert.Equal("REL_TIME", seek.Arg("Unit"));
            Assert.Equal("0:01:15", seek.Arg("Target"));
            p.Digital(S.DiStop, true);
            Assert.Single(rig.Net.Named("Stop"));
            p.Digital(S.DiPlayPause, true);                 // paused -> Play
            Assert.Equal("1", rig.Net.Named("Play").Last().Arg("Speed"));
        }

        [Fact]
        public void Spdif_open_is_reflected_after_the_poll()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip);
            p.Digital(S.DiInputSpdifOn, true);
            dev.OpenSpdif = true;
            rig.Clock.Advance(1000);
            Assert.True(sink.Dig(S.DoSourceSpdif));
            Assert.False(sink.Dig(S.DoSourceMusic));
            Assert.Equal("SPDIF", sink.Ser(S.SoSource));
        }

        [Fact]
        public void Scenes_execute_by_slot_and_by_name()
        {
            var rig = new Rig();
            rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip);
            p.Digital(S.DiSceneBase + 2, true);
            Assert.Equal("2", rig.Net.Named("ExecuteSensor").Last().Arg("SensorId"));
            p.Serial(S.SiSceneExecute, "以后还会");
            Assert.Equal("1", rig.Net.Named("ExecuteSensor").Last().Arg("SensorId"));
            p.Serial(S.SiSceneExecute, "bffgh");          // trigger code
            Assert.Equal("2", rig.Net.Named("ExecuteSensor").Last().Arg("SensorId"));
            p.Digital(S.DiSceneBase + 16, true);
            Assert.Equal(3, rig.Net.Named("ExecuteSensor").Count());
            Assert.Contains("16", sink.Ser(S.SoLastError));
        }

        [Fact]
        public void Hidden_renderer_404_skips_rendering_control_for_a_while()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            dev.RendererHidden = true;
            var (p, sink) = rig.StartPlayer(Ip, pollSeconds: 1);
            Assert.True(sink.Dig(S.DoOnline));   // a 404 is not a transport failure
            int n = rig.Net.Named("GetVolume").Count();
            rig.Clock.Advance(5000);
            Assert.Equal(n, rig.Net.Named("GetVolume").Count());
            Assert.True(sink.Dig(S.DoOnline));
        }

        [Fact]
        public void Empty_queue_clears_stale_now_playing()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            var (p, sink) = rig.StartPlayer(Ip, pollSeconds: 1);
            Assert.Equal("Go Your Own Way", sink.Ser(S.SoTitle));
            dev.Queue.Clear();
            dev.CurrentIndex = -1;
            rig.Clock.Advance(2000);
            Assert.Equal("", sink.Ser(S.SoTitle));
            Assert.Equal(0, sink.Ana(S.AoQueueTotal));
        }

        [Fact]
        public void Position_is_interpolated_while_playing()
        {
            var rig = new Rig();
            var dev = rig.AddDevice(Ip);
            dev.Transport = "PLAYING";
            var (p, sink) = rig.StartPlayer(Ip, pollSeconds: 10);
            int pos = sink.Ana(S.AoPosition);
            rig.Clock.Advance(3000);
            Assert.InRange(sink.Ana(S.AoPosition), pos + 2, pos + 4);
            Assert.True(sink.Ana(S.AoProgressRaw) > 0);
        }
    }
}
