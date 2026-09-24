using System;
using System.Collections.Generic;
using MiYue.Core.Model;
using MiYue.Core.Soap;
using MiYue.Core.Upnp;
using MiYue.Core.Util;

namespace MiYue.Core.Engine
{
    /// <summary>
    /// Multi-room sync groups, HA group.py semantics (mirror model). There is no whole-group query:
    /// every player in this program polls its own GetGroupInfo and players are clustered by GroupID.
    ///
    ///   JOIN  : read the master's authoritative GroupID / MulticastAddr / MasterIp -> SetGroupConfig the
    ///           master FIRST (Role=Master) -> then each slave (Role=Slave) with the master's values ->
    ///           settle ~900 ms -> re-read GetGroupInfo (SOAP success is not proof).
    ///   UNJOIN: LeaveGroup() on the leaving player; if that would leave &lt;2 members, dissolve the group by
    ///           LeaveGroup() on EVERY member (the firmware does not cascade).
    ///
    /// Invariants: MasterIp is the master's own IPv4 (never a foreign value); GroupID / MulticastAddr are
    /// never fabricated (read from the master). Operations are serialized (one at a time, FIFO) - a command
    /// arriving mid-operation is queued, never dropped (C4 group.lua pendingOp).
    /// Strand only.
    /// </summary>
    public sealed class GroupManager
    {
        public const int ApplySettleMs = 900;
        public const int RetryDelayMs = 400;

        private readonly Runtime _rt;
        private readonly Queue<Action<Action>> _ops = new Queue<Action<Action>>();
        private bool _busy;

        public GroupManager(Runtime rt)
        {
            _rt = rt;
        }

        public bool Busy
        {
            get { return _busy; }
        }

        // -- topology -------------------------------------------------------------------------------
        /// <summary>
        /// Members of the group <paramref name="me"/> belongs to (leader first) and the leader. A cluster of
        /// &lt;2 is presented as standalone (empty list). Role==None vetoes any GroupID.
        /// </summary>
        public List<PlayerEngine> Cluster(PlayerEngine me, out PlayerEngine leader)
        {
            leader = null;
            var members = new List<PlayerEngine>();
            if (me == null || !me.GroupRead) return members;
            string gid = me.Group.EffectiveGroupId;
            if (gid.Length == 0) return members;
            foreach (var p in _rt.Players)
            {
                if (!p.GroupRead || p.Group.IsStandalone) continue;
                if (p.Group.EffectiveGroupId != gid) continue;
                members.Add(p);
                if (p.Group.IsMaster && leader == null) leader = p;
            }
            if (members.Count < 2)
            {
                leader = null;
                return new List<PlayerEngine>();
            }
            if (leader != null)
            {
                members.Remove(leader);
                members.Insert(0, leader);
            }
            return members;
        }

        /// <summary>This player, or its group leader when it is a slave (HA _cast_target).</summary>
        public PlayerEngine CastTarget(PlayerEngine p)
        {
            if (!p.Group.IsSlave) return p;
            PlayerEngine leader;
            Cluster(p, out leader);
            return leader ?? p;
        }

        /// <summary>Re-publish group outputs of every player (a membership change affects them all).</summary>
        public void TopologyChanged()
        {
            foreach (var p in _rt.Players) p.PublishGroup();
        }

        // -- operations -----------------------------------------------------------------------------
        /// <summary>Form / extend a group with <paramref name="master"/> as leader. An empty slave list just makes
        /// the master a Master (HA async_join with no members). Errors are reported on <paramref name="requester"/>.</summary>
        public void Join(PlayerEngine master, IList<PlayerEngine> slaves, PlayerEngine requester)
        {
            var slaveCopy = new List<PlayerEngine>(slaves ?? new List<PlayerEngine>());
            Enqueue(done => DoJoin(master, slaveCopy, requester ?? master, done));
        }

        /// <summary>Remove <paramref name="p"/> from its group; dissolve when fewer than 2 would remain.</summary>
        public void Unjoin(PlayerEngine p)
        {
            Enqueue(done =>
            {
                PlayerEngine leader;
                var members = Cluster(p, out leader);
                List<PlayerEngine> toLeave;
                if (members.Count <= 2) toLeave = members.Count > 0 ? members : new List<PlayerEngine> { p };
                else toLeave = new List<PlayerEngine> { p };
                LeaveAll(toLeave, p, done);
            });
        }

        /// <summary>LeaveGroup on every member of p's group (or just p when it is not in a known cluster).</summary>
        public void Dissolve(PlayerEngine p)
        {
            Enqueue(done =>
            {
                PlayerEngine leader;
                var members = Cluster(p, out leader);
                if (members.Count == 0) members.Add(p);
                LeaveAll(members, p, done);
            });
        }

        private void Enqueue(Action<Action> op)
        {
            _ops.Enqueue(op);
            if (!_busy) RunNext();
        }

        private void RunNext()
        {
            if (_ops.Count == 0)
            {
                _busy = false;
                TopologyChanged();
                return;
            }
            _busy = true;
            TopologyChanged();
            var op = _ops.Dequeue();
            bool finished = false;
            Action done = () =>
            {
                if (finished) return;
                finished = true;
                RunNext();
            };
            try
            {
                op(done);
            }
            catch (Exception e)
            {
                _rt.Log.Error("MiYue group op error: " + e);
                done();
            }
        }

        private void DoJoin(PlayerEngine master, List<PlayerEngine> slaves, PlayerEngine requester, Action done)
        {
            requester.ClearError();
            if (!master.Online)
            {
                requester.ReportError("主机离线 / Master " + master.Ip + " is offline");
                done();
                return;
            }
            // Step 1: authoritative transport values from the master (self-derived by the device).
            master.Call(Services.MiyueGroup, "GetGroupInfo", SoapArgs.None, r =>
            {
                if (!r.Ok)
                {
                    requester.ReportError("读取主机组信息失败 / GetGroupInfo on master failed: " + r);
                    done();
                    return;
                }
                var info = GroupInfo.From(r);
                string gid = info.GroupId;
                string mcast = info.MulticastAddr;
                string masterIp = Ipv4.IsValid(info.MasterIp) ? info.MasterIp : master.Ip;
                if (gid.Length == 0 || gid == "null" || mcast.Length == 0)
                {
                    requester.ReportError("主机没有有效的组标识 / Master " + master.Ip + " has no valid group identity");
                    done();
                    return;
                }
                if (!Ipv4.IsValid(masterIp))
                {
                    requester.ReportError("主机 IP 无效 / Master has no valid IPv4 (" + masterIp + ")");
                    done();
                    return;
                }
                string masterUuid = info.DeviceUuid.Length > 0 ? info.DeviceUuid : master.DeviceUuid;

                // Step 2: the master FIRST, then each slave with the master's values.
                SetConfigRetry(master, masterUuid, gid, masterIp, mcast, Roles.Master, ok =>
                {
                    if (!ok)
                    {
                        requester.ReportError("设置主机失败 / SetGroupConfig(Master) failed on " + master.Ip);
                        Settle(new List<PlayerEngine> { master }, done);
                        return;
                    }
                    ConfigureSlaves(slaves, 0, gid, masterIp, mcast, master, requester, () =>
                    {
                        var all = new List<PlayerEngine> { master };
                        all.AddRange(slaves);
                        Settle(all, done); // Step 3
                    });
                });
            });
        }

        private void ConfigureSlaves(List<PlayerEngine> slaves, int i, string gid, string masterIp, string mcast,
            PlayerEngine master, PlayerEngine requester, Action finished)
        {
            if (i >= slaves.Count)
            {
                finished();
                return;
            }
            var s = slaves[i];
            if (s == master || s.Ip == masterIp)
            {
                _rt.Log.Debug("MiYue join: slave " + s.Ip + " == master, skipped");
                ConfigureSlaves(slaves, i + 1, gid, masterIp, mcast, master, requester, finished);
                return;
            }
            if (!s.Online)
            {
                requester.ReportError("从机离线 / Slave " + s.Ip + " is offline");
                ConfigureSlaves(slaves, i + 1, gid, masterIp, mcast, master, requester, finished);
                return;
            }
            SetConfigRetry(s, s.DeviceUuid, gid, masterIp, mcast, Roles.Slave, ok =>
            {
                if (!ok) requester.ReportError("设置从机失败 / SetGroupConfig(Slave) failed on " + s.Ip);
                ConfigureSlaves(slaves, i + 1, gid, masterIp, mcast, master, requester, finished);
            });
        }

        /// <summary>SetGroupConfig(TargetUUID, GroupID, MasterIp, MulticastAddr, Role) - SCPD argument order -
        /// with one retry after 400 ms (HA _set_config_retry).</summary>
        private void SetConfigRetry(PlayerEngine p, string uuid, string gid, string masterIp, string mcast, string role, Action<bool> done)
        {
            var args = new SoapArgs()
                .Add("TargetUUID", uuid)
                .Add("GroupID", gid)
                .Add("MasterIp", masterIp)
                .Add("MulticastAddr", mcast)
                .Add("Role", role);
            p.Call(Services.MiyueGroup, "SetGroupConfig", args, r =>
            {
                if (r.Ok)
                {
                    done(true);
                    return;
                }
                _rt.Log.Debug("MiYue SetGroupConfig(" + role + ") on " + p.Ip + " retrying: " + r);
                _rt.After(RetryDelayMs, () =>
                    p.Call(Services.MiyueGroup, "SetGroupConfig", args, r2 => done(r2.Ok)));
            });
        }

        private void LeaveAll(List<PlayerEngine> players, PlayerEngine requester, Action done)
        {
            LeaveNext(players, 0, requester, () => Settle(players, done));
        }

        private void LeaveNext(List<PlayerEngine> players, int i, PlayerEngine requester, Action finished)
        {
            if (i >= players.Count)
            {
                finished();
                return;
            }
            var p = players[i];
            p.Call(Services.MiyueGroup, "LeaveGroup", SoapArgs.None, r =>
            {
                if (!r.Ok) requester.ReportError("退组失败 / LeaveGroup on " + p.Ip + " failed: " + r);
                LeaveNext(players, i + 1, requester, finished);
            });
        }

        /// <summary>Wait ~900 ms (group writes apply asynchronously), then re-read every involved player.</summary>
        private void Settle(List<PlayerEngine> players, Action done)
        {
            _rt.After(ApplySettleMs, () => RefreshNext(players, 0, done));
        }

        private void RefreshNext(List<PlayerEngine> players, int i, Action done)
        {
            if (i >= players.Count)
            {
                TopologyChanged();
                done();
                return;
            }
            players[i].RefreshGroup(() =>
            {
                players[i].PollSoon(PlayerEngine.FastPollMs);
                RefreshNext(players, i + 1, done);
            });
        }
    }
}
