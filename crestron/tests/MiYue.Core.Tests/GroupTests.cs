using System.Linq;
using MiYue.Core.Engine;
using MiYue.Core.Tests.Support;
using Xunit;
using S = MiYue.Core.Engine.PlayerSignals;

namespace MiYue.Core.Tests
{
    /// <summary>Sync groups: HA group.py semantics (tests/test_group.py ported + the join/unjoin sequences).</summary>
    public class GroupTests
    {
        private const string A = "192.168.1.47";
        private const string B = "192.168.1.37";
        private const string C = "192.168.1.170";

        private static (Rig rig, FakeMiyue da, FakeMiyue db, PlayerEngine pa, PlayerEngine pb, RecordingSink sa, RecordingSink sb) TwoPlayers()
        {
            var rig = new Rig();
            var da = rig.AddDevice(A, name: "餐厅");
            var db = rig.AddDevice(B, name: "书房");
            var (pa, sa) = rig.StartPlayer(A);
            var (pb, sb) = rig.StartPlayer(B);
            return (rig, da, db, pa, pb, sa, sb);
        }

        [Fact]
        public void Join_as_slave_configures_master_first_with_its_own_values()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            string gid = da.GroupId, mcast = da.Multicast;
            rig.Net.Calls.Clear();

            pb.Serial(S.SiGroupJoinMaster, A);
            rig.Clock.Advance(GroupManager.ApplySettleMs + 100);

            var seq = rig.Net.Calls.Where(c => c.Service == "MiyueGroup").Select(c => c.Ip + " " + c.Action).ToList();
            Assert.Equal(A + " GetGroupInfo", seq[0]);
            Assert.Equal(A + " SetGroupConfig", seq[1]);
            Assert.Equal(B + " SetGroupConfig", seq[2]);

            var m = rig.Net.Named("SetGroupConfig").First();
            Assert.Equal(new[] { "TargetUUID", "GroupID", "MasterIp", "MulticastAddr", "Role" }, m.Args.Select(a => a.Key));
            Assert.Equal(da.Udn, m.Arg("TargetUUID"));
            Assert.Equal("Master", m.Arg("Role"));
            Assert.Equal(A, m.Arg("MasterIp"));      // MasterIp empty on a standalone -> the master's own host
            var s = rig.Net.Named("SetGroupConfig").Last();
            Assert.Equal(db.Udn, s.Arg("TargetUUID"));
            Assert.Equal("Slave", s.Arg("Role"));
            Assert.Equal(gid, s.Arg("GroupID"));      // never fabricated: read from the master
            Assert.Equal(mcast, s.Arg("MulticastAddr"));
            Assert.Equal(A, s.Arg("MasterIp"));

            // settle -> re-read, topology visible on both players
            Assert.True(sa.Dig(S.DoGroupMaster));
            Assert.True(sb.Dig(S.DoGroupSlave));
            Assert.True(sa.Dig(S.DoGrouped));
            Assert.True(sb.Dig(S.DoGrouped));
            Assert.Equal(2, sa.Ana(S.AoGroupMemberCount));
            Assert.Equal("餐厅,书房", sb.Ser(S.SoGroupMembers)); // leader first
            Assert.Equal(A, sb.Ser(S.SoGroupMasterIp));
            Assert.False(sa.Dig(S.DoGroupBusy));
        }

        [Fact]
        public void Add_slave_from_the_master_side()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            pa.Serial(S.SiGroupAddSlave, B);
            rig.Clock.Advance(1000);
            Assert.Equal("Master", da.Role);
            Assert.Equal("Slave", db.Role);
            Assert.Equal(da.GroupId, db.GroupId);
        }

        [Fact]
        public void Master_without_valid_group_identity_is_refused()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            da.GroupId = "null";
            pb.Serial(S.SiGroupJoinMaster, A);
            rig.Clock.Advance(1000);
            Assert.Empty(rig.Net.Named("SetGroupConfig"));
            Assert.Contains("group identity", sb.Ser(S.SoLastError));
        }

        [Fact]
        public void Unknown_master_is_reported_not_sent()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            pb.Serial(S.SiGroupJoinMaster, "192.168.1.250");
            Assert.Empty(rig.Net.Named("SetGroupConfig"));
            Assert.Contains("192.168.1.250", sb.Ser(S.SoLastError));
        }

        [Fact]
        public void Leaving_a_two_member_group_dissolves_it()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            pb.Serial(S.SiGroupJoinMaster, A);
            rig.Clock.Advance(1000);
            rig.Net.Calls.Clear();
            pb.Digital(S.DiGroupLeave, true);
            rig.Clock.Advance(1000);
            Assert.Equal(new[] { A, B }, rig.Net.Named("LeaveGroup").Select(c => c.Ip).OrderByDescending(x => x == A).ToArray());
            Assert.Equal("None", da.Role);
            Assert.Equal("None", db.Role);
            Assert.False(sa.Dig(S.DoGrouped));
            Assert.Equal("None", sb.Ser(S.SoGroupRole));
        }

        [Fact]
        public void Leaving_a_three_member_group_only_removes_that_player()
        {
            var rig = new Rig();
            var da = rig.AddDevice(A);
            var db = rig.AddDevice(B);
            var dc = rig.AddDevice(C);
            var (pa, _) = rig.StartPlayer(A);
            var (pb, _) = rig.StartPlayer(B);
            var (pc, sc) = rig.StartPlayer(C);
            pb.Serial(S.SiGroupJoinMaster, A);
            pc.Serial(S.SiGroupJoinMaster, A);   // queued behind the first join, never dropped
            rig.Clock.Advance(3000);
            Assert.Equal("Slave", db.Role);
            Assert.Equal("Slave", dc.Role);
            Assert.Equal(3, sc.Ana(S.AoGroupMemberCount));

            rig.Net.Calls.Clear();
            pc.Digital(S.DiGroupLeave, true);
            rig.Clock.Advance(1000);
            Assert.Equal(new[] { C }, rig.Net.Named("LeaveGroup").Select(c => c.Ip).ToArray());
            Assert.Equal("Master", da.Role);
            Assert.Equal("Slave", db.Role);
        }

        [Fact]
        public void Dissolve_leaves_every_member()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            pb.Serial(S.SiGroupJoinMaster, A);
            rig.Clock.Advance(1000);
            rig.Net.Calls.Clear();
            pa.Digital(S.DiGroupDissolve, true);
            rig.Clock.Advance(1000);
            Assert.Equal(2, rig.Net.Named("LeaveGroup").Count());
        }

        [Fact]
        public void Cast_on_a_slave_goes_to_the_group_leader()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            pb.Serial(S.SiGroupJoinMaster, A);
            rig.Clock.Advance(1000);
            rig.Net.Calls.Clear();
            pb.Serial(S.SiPlayUrl, "http://example.com/stream.mp3");
            var set = rig.Net.Named("SetAVTransportURI").Single();
            Assert.Equal(A, set.Ip);
            Assert.Equal("http://example.com/stream.mp3", set.Arg("CurrentURI"));
            Assert.Equal("", set.Arg("CurrentURIMetaData"));
            Assert.Equal(A, rig.Net.Named("Play").Single().Ip);

            // not a slave -> casts to itself
            rig.Net.Calls.Clear();
            pa.Serial(S.SiPlayUrl, "http://example.com/2.mp3");
            Assert.Equal(A, rig.Net.Named("SetAVTransportURI").Single().Ip);
        }

        [Fact]
        public void Tts_to_group_fans_out_to_every_member_at_their_own_volume()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            da.Volume = 40;
            db.Volume = 12;
            pb.Serial(S.SiGroupJoinMaster, A);
            rig.Clock.Advance(4000);
            rig.Net.Calls.Clear();
            pb.Digital(S.DiTtsToGroup, true);
            pb.Serial(S.SiTtsText, "开饭了");
            var calls = rig.Net.Named("PlayTTS").ToList();
            Assert.Equal(2, calls.Count);
            Assert.Equal("12", calls.Single(c => c.Ip == B).Arg("Volume"));
            Assert.Equal("40", calls.Single(c => c.Ip == A).Arg("Volume"));
        }

        [Fact]
        public void Role_none_vetoes_gid_and_a_lone_master_is_standalone()
        {
            var (rig, da, db, pa, pb, sa, sb) = TwoPlayers();
            // both report the same stale gid but Role None -> no cluster
            db.GroupId = da.GroupId;
            rig.Clock.Advance(7000);
            PlayerEngine leader;
            Assert.Empty(rig.Rt.Groups.Cluster(pa, out leader));
            Assert.Null(leader);

            pa.Digital(S.DiGroupBecomeMaster, true);
            rig.Clock.Advance(1000);
            Assert.Equal("Master", da.Role);
            Assert.True(sa.Dig(S.DoGroupMaster));
            Assert.False(sa.Dig(S.DoGrouped));      // cluster of 1 -> standalone
            Assert.Empty(rig.Rt.Groups.Cluster(pa, out leader));
        }
    }
}
