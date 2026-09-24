using System;
using System.Collections.Generic;
using MiYue.Core.Didl;
using MiYue.Core.Model;
using MiYue.Core.Soap;
using MiYue.Core.Upnp;
using MiYue.Core.Util;

namespace MiYue.Core.Engine
{
    /// <summary>
    /// Poll loop (C4 state.lua order, HA coordinator data model). One cycle is a chain of SOAP reads
    /// that NEVER overlaps the next: the next cycle is armed only when this one finishes (or its watchdog
    /// fires). Every step tolerates failure and continues; a transport failure on the first read ends the
    /// cycle early. Commands request a fast re-poll (800 ms) through <see cref="PollSoon"/>.
    ///
    ///   GetTransportInfo -> GetVolume -> GetMute -> GetPositionInfo -> GetQueue(cur,1)
    ///   -> GetPlayMode (every 5th / dirty) -> GetExternalInputs (every 4th / dirty)
    ///   -> GetGroupInfo (every 2nd / dirty) -> GetDeviceInfo (once) -> ListSensors (every 30 s / dirty)
    /// </summary>
    public sealed partial class PlayerEngine
    {
        public const int CycleWatchdogMs = 60000;
        public const int RcRetryCycles = 10;
        public const int ModeEvery = 5;
        public const int InputsEvery = 4;
        public const int GroupEvery = 2;
        public const int ScenesEveryMs = 30000;

        /// <summary>True while a poll cycle is in flight (tests use it to prove cycles never overlap).</summary>
        public bool Polling
        {
            get { return _polling; }
        }

        public int CycleCount
        {
            get { return _cycle; }
        }

        /// <summary>Request a poll soon (after a command). Coalesces with a running cycle. Strand only.</summary>
        public void PollSoon(int ms)
        {
            if (!_running) return;
            if (_polling)
            {
                if (!_pendingMs.HasValue || ms < _pendingMs.Value) _pendingMs = ms;
                return;
            }
            ArmPoll(ms);
        }

        private void ArmPoll(int ms)
        {
            Cancel(ref _pollTimer);
            if (!_running) return;
            _pollTimer = _rt.After(Math.Max(20, ms), () =>
            {
                _pollTimer = null;
                PollNow();
            });
        }

        private void PollNow()
        {
            if (!_running) return;
            if (_polling)
            {
                if (_rt.NowMs - _cycleStartedMs <= CycleWatchdogMs) return;
                _rt.Log.Error("MiYue " + Ip + ": poll cycle stuck, resetting");
                _cycleId++;
                _polling = false;
            }
            if (!_link.Online)
            {
                // DeviceLink owns (re)resolve with backoff; just look again later.
                ArmPoll(_pollMs);
                return;
            }
            _polling = true;
            _cycle++;
            int cid = ++_cycleId;
            _cycleStartedMs = _rt.NowMs;
            _rcSkippedThisCycle = false;
            Cancel(ref _watchdog);
            _watchdog = _rt.After(CycleWatchdogMs, () =>
            {
                if (_polling && cid == _cycleId)
                {
                    _rt.Log.Error("MiYue " + Ip + ": poll watchdog fired");
                    _cycleId++;
                    FinishCycle();
                }
            });

            var steps = new List<Action<int, Action>>
            {
                StepTransport, StepVolume, StepMute, StepPosition, StepQueue,
            };
            if (_modeDirty || _playMode == null || _cycle % ModeEvery == 1) steps.Add(StepMode);
            if (_inputsDirty || _inputs == null || _cycle % InputsEvery == 1) steps.Add(StepInputs);
            if (_groupDirty || !_groupRead || _cycle % GroupEvery == 1) steps.Add(StepGroup);
            if (!_deviceInfoRead) steps.Add(StepDeviceInfo);
            if (_scenesDirty || _scenesReadAt < 0 || _rt.NowMs - _scenesReadAt >= ScenesEveryMs) steps.Add(StepScenes);
            RunSteps(cid, steps, 0);
        }

        private void RunSteps(int cid, List<Action<int, Action>> steps, int i)
        {
            if (cid != _cycleId) return; // stale callback of an aborted cycle
            if (i >= steps.Count)
            {
                FinishCycle();
                return;
            }
            steps[i](cid, () =>
            {
                if (cid != _cycleId) return;
                Publish();
                RunSteps(cid, steps, i + 1);
            });
        }

        private void FinishCycle()
        {
            Cancel(ref _watchdog);
            _polling = false;
            Publish();
            int ms = _pendingMs ?? _pollMs;
            _pendingMs = null;
            if (_running) ArmPoll(ms);
        }

        /// <summary>End the running cycle now (transport failure on its first read).</summary>
        private void AbortCycle(int cid)
        {
            if (cid != _cycleId) return;
            _cycleId++;
            FinishCycle();
        }

        // ------------------------------------------------------------------------------------------
        private void StepTransport(int cid, Action next)
        {
            _link.Call(Services.AVTransport, "GetTransportInfo", SoapArgs.Instance(), r =>
            {
                if (r.Ok)
                {
                    var state = r.Get("CurrentTransportState").Trim().ToUpperInvariant();
                    if (_ttsHold > 0)
                    {
                        _ttsHold--; // TTS flips the state to PLAYING briefly - not a user play
                    }
                    else if (state.Length > 0)
                    {
                        _transport = state;
                        var mapped = MapTransport(state);
                        if (mapped != null) _transportMapped = mapped;
                    }
                }
                else if (r.Outcome == SoapOutcome.Transport || r.Outcome == SoapOutcome.Offline)
                {
                    AbortCycle(cid);
                    return;
                }
                next();
            });
        }

        /// <summary>PLAYING / PAUSED / STOPPED; TRANSITIONING (or unknown) keeps the previous value (null).</summary>
        public static string MapTransport(string state)
        {
            switch ((state ?? string.Empty).ToUpperInvariant())
            {
                case TransportStates.Playing: return "PLAYING";
                case TransportStates.Paused: return "PAUSED";
                case TransportStates.Stopped:
                case TransportStates.NoMedia: return "STOPPED";
                default: return null;
            }
        }

        private void StepVolume(int cid, Action next)
        {
            if (_rcSkip > 0)
            {
                _rcSkip--;
                _rcSkippedThisCycle = true;
                next();
                return;
            }
            int sent = _tick;
            _link.Call(Services.RenderingControl, "GetVolume", SoapArgs.Master(), r =>
            {
                if (r.Ok)
                {
                    int v = r.GetInt("CurrentVolume", -1);
                    if (_volCmd > sent) { /* answered after a SetVolume: keep the optimistic value */ }
                    else if (v >= 0) _volume = Math.Min(100, v);
                }
                else if (r.HttpStatus == 404)
                {
                    // older firmware hides the MediaRenderer on a slave: skip RC for a while (C4 / contract)
                    _rt.Log.Debug("MiYue " + Ip + ": RenderingControl 404, skipping " + RcRetryCycles + " cycles");
                    _rcSkip = RcRetryCycles;
                    _rcSkippedThisCycle = true;
                }
                next();
            });
        }

        private void StepMute(int cid, Action next)
        {
            if (_rcSkippedThisCycle)
            {
                next();
                return;
            }
            int sent = _tick;
            _link.Call(Services.RenderingControl, "GetMute", SoapArgs.Master(), r =>
            {
                if (r.Ok)
                {
                    if (_muteCmd <= sent) _mute = r.Get("CurrentMute", "0").Trim() == "1";
                }
                else if (r.HttpStatus == 404)
                {
                    _rcSkip = RcRetryCycles;
                }
                next();
            });
        }

        private void StepPosition(int cid, Action next)
        {
            _link.Call(Services.AVTransport, "GetPositionInfo", SoapArgs.Instance(), r =>
            {
                if (r.Ok)
                {
                    var items = DidlDoc.Parse(r.Get("TrackMetaData"));
                    _metaTrack = items.Count > 0 ? items[0] : null;
                    _posSec = TimeText.ToSeconds(r.Get("RelTime"));
                    _durSec = TimeText.ToSeconds(r.Get("TrackDuration"));
                    _posSampleMs = _rt.NowMs;
                }
                next();
            });
        }

        private void StepQueue(int cid, Action next)
        {
            int start = _queueIndex >= 0 ? _queueIndex : 0;
            ReadQueueItem(start, (ok, cur, total, item) =>
            {
                if (!ok)
                {
                    next(); // keep last poll's values (HA): -1 also means "idle", must not flap skip gating
                    return;
                }
                if (cur >= 0 && cur != start && cur < total)
                {
                    // the index moved: fetch the item actually at CurrentIndex
                    ReadQueueItem(cur, (ok2, cur2, total2, item2) =>
                    {
                        if (ok2) ApplyQueue(cur2, total2, cur2 == cur ? item2 : null);
                        else ApplyQueue(cur, total, null);
                        next();
                    });
                    return;
                }
                ApplyQueue(cur, total, cur >= 0 && cur == start ? item : null);
                next();
            });
        }

        /// <summary>GetQueue(StartIndex, RequestedCount=1): CurrentIndex, Total and the item at StartIndex.
        /// RequestedCount 0 means "all" on the firmware, so it is never sent.</summary>
        private void ReadQueueItem(int start, Action<bool, int, int, DidlItem> done)
        {
            var args = new SoapArgs().Add("StartIndex", start).Add("RequestedCount", 1);
            _link.Call(Services.MiyueQueue, "GetQueue", args, r =>
            {
                if (!r.Ok)
                {
                    done(false, _queueIndex, _queueTotal, null);
                    return;
                }
                int total = r.GetInt("Total", 0);
                int cur = r.GetInt("CurrentIndex", -1); // empty <CurrentIndex/> must read as -1, not 0
                var items = DidlDoc.Parse(r.Get("Result"));
                if (total <= 0) total = Math.Max(total, 0);
                done(true, cur, total, items.Count > 0 ? items[0] : null);
            });
        }

        private void ApplyQueue(int cur, int total, DidlItem item)
        {
            bool changed = !_queueKnown || cur != _queueIndex || total != _queueTotal;
            _queueIndex = cur;
            _queueTotal = total;
            _queueItem = cur >= 0 ? item : null;
            _queueKnown = true;
            if (changed)
            {
                var h = QueueStateChanged;
                if (h != null)
                {
                    try
                    {
                        h();
                    }
                    catch (Exception e)
                    {
                        _rt.Log.Error("MiYue: QueueStateChanged handler: " + e.Message);
                    }
                }
            }
        }

        private void StepMode(int cid, Action next)
        {
            int sent = _tick;
            _link.Call(Services.MiyueQueue, "GetPlayMode", SoapArgs.None, r =>
            {
                if (r.Ok && _modeCmd <= sent)
                {
                    _playMode = PlayModes.Normalize(r.Get("PlayMode"));
                    _modeDirty = false;
                }
                next();
            });
        }

        private void StepInputs(int cid, Action next)
        {
            _link.Call(Services.MiyueAudioSource, "GetExternalInputs", SoapArgs.None, r =>
            {
                if (r.Ok)
                {
                    _inputs = ExternalInputs.From(r);
                    _inputsDirty = false;
                }
                next();
            });
        }

        private void StepGroup(int cid, Action next)
        {
            if (_rt.Groups.Busy)
            {
                next(); // a group operation is running; it re-reads GetGroupInfo itself when done
                return;
            }
            RefreshGroup(next);
        }

        /// <summary>Read MiyueGroup.GetGroupInfo (the only authoritative role source) and apply it. Strand only.</summary>
        public void RefreshGroup(Action done)
        {
            _link.Call(Services.MiyueGroup, "GetGroupInfo", SoapArgs.None, r =>
            {
                if (r.Ok)
                {
                    var g = GroupInfo.From(r);
                    bool changed = !_groupRead || g.Role != _group.Role || g.EffectiveGroupId != _group.EffectiveGroupId
                                   || g.MasterIp != _group.MasterIp;
                    _group = g;
                    _groupRead = true;
                    _groupDirty = false;
                    if (changed) _rt.Groups.TopologyChanged();
                }
                if (done != null) done();
            });
        }

        private void StepDeviceInfo(int cid, Action next)
        {
            _link.Call(Services.MiyueSystem, "GetDeviceInfo", SoapArgs.None, r =>
            {
                if (r.Ok)
                {
                    _deviceInfoRead = true;
                    var name = r.Get("Name").Trim();
                    if (name.Length > 0) _deviceName = name;
                    var model = r.Get("Model").Trim();
                    if (model.Length > 0) _model = model;
                    var fw = r.Get("SoftwareVersion").Trim();
                    if (fw.Length > 0) _firmware = fw;
                    var uuid = r.Get("DeviceUUID").Trim();
                    if (uuid.Length > 0) _udn = uuid;
                    _rt.Groups.TopologyChanged(); // member names may have changed
                }
                next();
            });
        }

        private void StepScenes(int cid, Action next)
        {
            _link.Call(Services.MiyueSensor, "ListSensors", SoapArgs.None, r =>
            {
                if (r.Ok)
                {
                    // A failed read must NOT wipe the list (HA aux coordinator); only an answer replaces it.
                    _scenes = Scene.ParseOpen(r.Get("Sensors"));
                    _scenesDirty = false;
                    _scenesReadAt = _rt.NowMs;
                }
                next();
            });
        }

        // ------------------------------------------------------------------------------------------
        // 1 s position ticker: interpolates the position between polls while PLAYING
        private void ArmPositionTick()
        {
            Cancel(ref _positionTimer);
            if (!_running) return;
            _positionTimer = _rt.After(PositionTickMs, () =>
            {
                _positionTimer = null;
                if (!_running) return;
                if (_transportMapped == "PLAYING") PublishPosition();
                ArmPositionTick();
            });
        }

        /// <summary>Current position in seconds, extrapolated from the last sample while PLAYING.</summary>
        public int CurrentPositionSeconds
        {
            get
            {
                int pos = _posSec;
                if (_transportMapped == "PLAYING" && _posSampleMs > 0)
                {
                    long elapsed = (_rt.NowMs - _posSampleMs) / 1000;
                    if (elapsed > 0) pos += (int)Math.Min(elapsed, 24 * 3600);
                }
                if (_durSec > 0 && pos > _durSec) pos = _durSec;
                return pos;
            }
        }
    }
}
