using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MiYue.Core.Engine;
using MiYue.Core.Soap;
using MiYue.Core.Tests.Support;
using Xunit;
using B = MiYue.Core.Engine.BrowserSignals;
using S = MiYue.Core.Engine.PlayerSignals;

namespace MiYue.Core.Tests
{
    /// <summary>
    /// "Never invent SOAP actions": drive every feature of both engines against the simulated players and
    /// check that each emitted call - service, action AND in-argument names in order - exists in the SCPD
    /// dumps captured from the real Android (M100 .47) and Linux (M210B .170) firmware.
    /// </summary>
    public class ContractTests
    {
        private static Dictionary<string, List<string>> LoadScpd()
        {
            var map = new Dictionary<string, List<string>>();
            var line = new Regex(@"^(\w+)\.(\w+)\((.*)\)\s*$");
            foreach (var file in new[] { "scpd_android_M100_.47.txt", "scpd_linux_M210B_.170.txt" })
            {
                foreach (var raw in File.ReadAllLines(Path.Combine(Fixtures.Dir, file)))
                {
                    var m = line.Match(raw.Trim());
                    if (!m.Success) continue;
                    var ins = m.Groups[3].Value.Split(',').Select(a => a.Trim()).Where(a => a.EndsWith(":in"))
                        .Select(a => a.Substring(0, a.Length - 3)).ToList();
                    map[m.Groups[1].Value + "." + m.Groups[2].Value] = ins;
                }
            }
            return map;
        }

        [Fact]
        public void Every_emitted_call_exists_in_a_firmware_scpd_with_matching_arguments()
        {
            var scpd = LoadScpd();
            var rig = new Rig();
            var da = rig.AddDevice("10.0.0.1", name: "A");
            var db = rig.AddDevice("10.0.0.2", name: "B");
            var (pa, _) = rig.StartPlayer("10.0.0.1");
            var (pb, _) = rig.StartPlayer("10.0.0.2");

            for (ushort i = 1; i <= 32; i++)
            {
                pa.Digital(i, true);
                pa.Digital(i, false);
                rig.Clock.Advance(300);
            }
            pa.Digital(S.DiSceneBase + 1, true);
            pa.Analog(S.AiVolumeSet, 30);
            pa.Analog(S.AiSeekSeconds, 30);
            pa.Serial(S.SiTtsText, "hi");
            pa.Serial(S.SiPlayUrl, "http://x/y.mp3");
            pa.Serial(S.SiSceneExecute, "1");
            pb.Serial(S.SiGroupJoinMaster, "10.0.0.1");
            rig.Clock.Advance(3000);
            pa.Serial(S.SiGroupAddSlave, "10.0.0.2");
            rig.Clock.Advance(3000);
            pb.Digital(S.DiGroupLeave, true);
            rig.Clock.Advance(3000);
            da.SupportsNext = false;          // exercise the SeekToTrack fallback
            pa.Digital(S.DiNext, true);
            rig.Clock.Advance(3000);

            da.SonglistsJson = "[{\"id\":\"5\",\"name\":\"x\",\"count\":1}]";
            da.BoardsJson = "[{\"id\":\"6\",\"name\":\"y\",\"count\":1}]";
            da.SonglistTracks["5"] = da.QueueDidl(0, 2);
            da.SonglistTracks["6"] = da.QueueDidl(0, 2);
            var br = new BrowserEngine(rig.Rt);
            br.Start("10.0.0.1", 5, new RecordingSink());
            foreach (var list in new[] { B.DiListLiked, B.DiListRadios, B.DiListSonglists, B.DiListBoards, B.DiListQueue, B.DiListScenes })
            {
                br.Digital(list, true);
                br.Digital(B.DiItemBase + 1, true);
                br.Digital(B.DiPlayAll, true);
                rig.Clock.Advance(1000);
            }

            var seen = new HashSet<string>();
            foreach (var c in rig.Net.Calls)
            {
                string key = c.Service + "." + c.Action;
                Assert.True(scpd.ContainsKey(key), "not in any firmware SCPD: " + key);
                var names = c.Args.Select(a => a.Key).ToList();
                Assert.True(scpd[key].SequenceEqual(names),
                    key + " args [" + string.Join(",", names) + "] != SCPD [" + string.Join(",", scpd[key]) + "]");
                seen.Add(key);
            }

            // everything the driver is supposed to use was actually exercised
            foreach (var expected in new[]
            {
                "AVTransport.Play", "AVTransport.Pause", "AVTransport.Stop", "AVTransport.Next", "AVTransport.Previous",
                "AVTransport.Seek", "AVTransport.SetAVTransportURI", "AVTransport.GetTransportInfo", "AVTransport.GetPositionInfo",
                "RenderingControl.GetVolume", "RenderingControl.SetVolume", "RenderingControl.GetMute", "RenderingControl.SetMute",
                "MiyueQueue.GetQueue", "MiyueQueue.GetPlayMode", "MiyueQueue.SetPlayMode", "MiyueQueue.SeekToTrack", "MiyueQueue.ReplaceQueue",
                "MiyueAudioSource.GetExternalInputs", "MiyueAudioSource.SelectSource", "MiyueAudioSource.SetInputOpen",
                "MiyueSystem.GetDeviceInfo", "MiyueSystem.PlayTTS",
                "MiyueSensor.ListSensors", "MiyueSensor.ExecuteSensor",
                "MiyueGroup.GetGroupInfo", "MiyueGroup.SetGroupConfig", "MiyueGroup.LeaveGroup",
                "MiyueLibrary.GetCollectedMusic", "MiyueLibrary.GetCollectedRadios", "MiyueLibrary.GetCollectedSonglists",
                "MiyueLibrary.GetCollectedBoards", "MiyueLibrary.GetSonglistTracks",
            })
            {
                Assert.Contains(expected, seen);
            }
            // never touched: editing / alarms / collect (HA dropped them or never had them)
            Assert.DoesNotContain(seen, k => k.StartsWith("MiyueAlarmClock") || k.Contains("CreateSensor") || k.Contains("UpdateSensor")
                                             || k.Contains("DeleteSensor") || k.Contains("AddTrackToSonglist") || k.Contains("GetTimeline"));
        }
    }
}
