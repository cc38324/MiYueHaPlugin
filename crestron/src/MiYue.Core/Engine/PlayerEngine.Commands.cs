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
    /// Device commands. Every action and argument here appears in the C4 contract_verified.json and/or the
    /// HA integration (device.py / media_player.py / browse.py) - never invent SOAP actions.
    /// All members are strand only.
    /// </summary>
    public sealed partial class PlayerEngine
    {
        private void AfterCommand(SoapResult r, string what)
        {
            if (r.Ok) ClearError();
            else ReportError(what + " 失败 / failed: " + r);
            PollSoon(FastPollMs);
        }

        // -- transport ----------------------------------------------------------------------------
        public void Play()
        {
            _link.Call(Services.AVTransport, "Play", SoapArgs.Instance().Add("Speed", "1"), r => AfterCommand(r, "Play"));
        }

        public void Pause()
        {
            _link.Call(Services.AVTransport, "Pause", SoapArgs.Instance(), r => AfterCommand(r, "Pause"));
        }

        public void StopTransport()
        {
            _link.Call(Services.AVTransport, "Stop", SoapArgs.Instance(), r => AfterCommand(r, "Stop"));
        }

        /// <summary>Seek within the current track: AVTransport Seek(Unit=REL_TIME, Target="H:MM:SS").</summary>
        public void Seek(int seconds)
        {
            var args = SoapArgs.Instance().Add("Unit", "REL_TIME").Add("Target", TimeText.ToHms(seconds));
            _link.Call(Services.AVTransport, "Seek", args, r =>
            {
                if (r.Ok)
                {
                    _posSec = Math.Max(0, seconds);
                    _posSampleMs = _rt.NowMs;
                }
                AfterCommand(r, "Seek");
            });
        }

        // -- skip (HA media_player._skip) ----------------------------------------------------------
        /// <summary>
        /// Whether next/previous makes sense now (Flutter/HA parity). A SLAVE may skip only when the
        /// forwarding path provably exists: the Linux firmware keeps a slave's renderer up, forwards
        /// Next/Previous to the master and reports the -105 sentinel; an Android slave hides its renderer
        /// and a SeekToTrack pushed at it rips it out of the group.
        /// </summary>
        public bool SkipAllowed
        {
            get
            {
                if (_group.IsSlave) return _queueIndex == SongSrc.SentinelSyncSlave;
                var track = CurrentTrack;
                int src = SongSrc.Effective(_inputs != null ? _inputs.ActiveInput : null, _queueIndex,
                    track != null ? track.SongSrc : null);
                return SongSrc.CanSkip(src);
            }
        }

        /// <summary>
        /// AVTransport Next/Previous - the firmware's single button arbiter (wrap, shuffle order, Bluetooth
        /// AVRCP / AirPlay reverse control). Only a genuine SOAP fault proves the firmware lacks it; then
        /// emulate a plain queue step (no wrap) from a FRESH GetQueue index. A timeout / transport loss may
        /// mean Next already ran, so no blind SeekToTrack there (it would double-skip).
        /// (C4 difference: the C4 driver does SeekToTrack index math first and Next/Previous only when the
        /// SCPD lists them; the newer HA behaviour is followed here.)
        /// </summary>
        public void Skip(bool forward)
        {
            if (!SkipAllowed)
            {
                _rt.Log.Debug("MiYue " + Ip + ": skip ignored (source cannot skip / unsafe slave)");
                return;
            }
            string action = forward ? "Next" : "Previous";
            _link.Call(Services.AVTransport, action, SoapArgs.Instance(), r =>
            {
                if (r.Ok || !r.IsFault)
                {
                    AfterCommand(r, action);
                    return;
                }
                _rt.Log.Debug("MiYue " + Ip + ": AVTransport " + action + " unsupported (" + r + "), queue-step fallback");
                ReadQueueItem(0, (ok, idx, total, item) =>
                {
                    if (!ok || idx < 0 || total <= 0)
                    {
                        PollSoon(FastPollMs);
                        return; // the queue is not what is sounding
                    }
                    var track = CurrentTrack;
                    if (track != null && track.SongSrc.HasValue && !SongSrc.IsQueueSrc(track.SongSrc.Value))
                    {
                        PollSoon(FastPollMs);
                        return; // an external single-stream is sounding; emulation would hijack it
                    }
                    int target = idx + (forward ? 1 : -1);
                    if (target < 0 || target >= total)
                    {
                        PollSoon(FastPollMs);
                        return;
                    }
                    SeekToTrack(target);
                });
            });
        }

        /// <summary>MiyueQueue.SeekToTrack(Index) - 0-based; silently no-ops out of range.</summary>
        public void SeekToTrack(int index)
        {
            if (index < 0) return;
            _link.Call(Services.MiyueQueue, "SeekToTrack", new SoapArgs().Add("Index", index), r => AfterCommand(r, "SeekToTrack"));
        }

        /// <summary>MiyueQueue.ReplaceQueue(Items, StartingIndex): replaces the queue and auto-plays index N.
        /// StartingIndex must be explicit and non-negative (a negative one does not autoplay on Android).</summary>
        public void ReplaceQueue(string didl, int startIndex, Action<SoapResult> done)
        {
            var args = new SoapArgs().Add("Items", didl ?? string.Empty).Add("StartingIndex", Math.Max(0, startIndex));
            _link.Call(Services.MiyueQueue, "ReplaceQueue", args, r =>
            {
                AfterCommand(r, "ReplaceQueue");
                if (done != null) done(r);
            });
        }

        // -- volume / mute ----------------------------------------------------------------------------
        /// <summary>Set 0..100. Rapid changes are coalesced: only the latest target is sent after the
        /// in-flight SetVolume completes (a slider must not flood the 2-slot request queue).
        /// Always an integer string - the firmware parses non-numeric as 0.</summary>
        public void SetVolume(int level)
        {
            level = Math.Max(0, Math.Min(100, level));
            _volCmd = Bump();
            _volume = level; // optimistic
            _volTarget = level;
            Publish();
            SendVolume();
        }

        private void SendVolume()
        {
            if (_volSending || !_volTarget.HasValue) return;
            int v = _volTarget.Value;
            _volTarget = null;
            _volSending = true;
            var args = SoapArgs.Master().Add("DesiredVolume", v);
            _link.Call(Services.RenderingControl, "SetVolume", args, r =>
            {
                _volSending = false;
                if (_volTarget.HasValue)
                {
                    SendVolume(); // a newer target arrived meanwhile
                    return;
                }
                AfterCommand(r, "SetVolume");
            });
        }

        /// <summary>Step the volume by the configured step. Unknown volume -> ignored (C4: never start
        /// from 0 and blast / silence the room).</summary>
        public void StepVolume(int direction)
        {
            if (!_volume.HasValue)
            {
                PollSoon(100);
                return;
            }
            int v = Math.Max(0, Math.Min(100, _volume.Value + direction * _volumeStep));
            if (v == _volume.Value) return;
            SetVolume(v);
        }

        private void RampVolume(int dir, int which)
        {
            if (dir == 0)
            {
                if (_rampDir == which)
                {
                    _rampDir = 0;
                    Cancel(ref _rampTimer);
                }
                return;
            }
            _rampDir = dir;
            StepVolume(dir);
            ArmRamp();
        }

        private void ArmRamp()
        {
            Cancel(ref _rampTimer);
            if (_rampDir == 0) return;
            _rampTimer = _rt.After(VolumeRampMs, () =>
            {
                _rampTimer = null;
                if (_rampDir == 0) return;
                StepVolume(_rampDir);
                ArmRamp();
            });
        }

        public void SetMute(bool mute)
        {
            _muteCmd = Bump();
            _mute = mute;
            Publish();
            var args = SoapArgs.Master().Add("DesiredMute", mute ? "1" : "0");
            _link.Call(Services.RenderingControl, "SetMute", args, r => AfterCommand(r, "SetMute"));
        }

        // -- play mode / sources ------------------------------------------------------------------------
        public void SetPlayMode(string mode)
        {
            mode = PlayModes.Normalize(mode);
            _modeCmd = Bump();
            _modeDirty = true;
            _playMode = mode; // optimistic
            Publish();
            _link.Call(Services.MiyueQueue, "SetPlayMode", SoapArgs.Of("PlayMode", mode), r => AfterCommand(r, "SetPlayMode"));
        }

        /// <summary>MiyueAudioSource.SelectSource(Source). "Music" closes external inputs and returns to the
        /// local player; it does NOT resume the queue by itself.</summary>
        public void SelectSource(string source)
        {
            _inputsDirty = true;
            _link.Call(Services.MiyueAudioSource, "SelectSource", SoapArgs.Of("Source", source), r => AfterCommand(r, "SelectSource"));
        }

        /// <summary>MiyueAudioSource.SetInputOpen(Source, On="1"/"0"). Asynchronous on Android (can lag tens of
        /// seconds): sent once, the poller picks the new state up - never re-sent or toggled rapidly.</summary>
        public void SetInputOpen(string source, bool on)
        {
            _inputsDirty = true;
            var args = new SoapArgs().Add("Source", source).Add("On", on ? "1" : "0");
            _link.Call(Services.MiyueAudioSource, "SetInputOpen", args, r => AfterCommand(r, "SetInputOpen"));
        }

        // -- scenes ---------------------------------------------------------------------------------------
        /// <summary>MiyueSensor.ExecuteSensor(SensorId): run a device scene now (activate-only; editing
        /// stays on the speaker, like the HA integration).</summary>
        public void ExecuteScene(string sensorId)
        {
            if (string.IsNullOrEmpty(sensorId))
            {
                ReportError("无效的情景 / Bad scene id");
                return;
            }
            _link.Call(Services.MiyueSensor, "ExecuteSensor", SoapArgs.Of("SensorId", sensorId), r => AfterCommand(r, "ExecuteSensor"));
        }

        /// <summary>Execute by id, then name (cmdName), then trigger code (cmd).</summary>
        public void ExecuteSceneByKey(string key)
        {
            Scene hit = null;
            foreach (var s in _scenes)
                if (s.Id == key) { hit = s; break; }
            if (hit == null)
                foreach (var s in _scenes)
                    if (s.Name == key) { hit = s; break; }
            if (hit == null)
                foreach (var s in _scenes)
                    if (s.Cmd.Length > 0 && s.Cmd == key) { hit = s; break; }
            if (hit == null)
            {
                ReportError("找不到情景 / Scene not found: " + key);
                _scenesDirty = true;
                return;
            }
            ExecuteScene(hit.Id);
        }

        // -- TTS ----------------------------------------------------------------------------------------
        /// <summary>Clamp a TTS volume: 1..100 as given, otherwise this player's current volume (>= 1) or 30.
        /// PlayTTS must ALWAYS carry Volume 1..100 (older Linux builds mute on empty/0 and never restore).</summary>
        public int TtsVolumeFor(int requested)
        {
            if (requested >= 1) return Math.Min(100, requested);
            if (_volume.HasValue && _volume.Value >= 1) return _volume.Value;
            return DefaultTtsVolume;
        }

        /// <summary>Speak on this player; with toGroup, fan out to every member of its sync group (there is no
        /// group-announce action and a slave does not hear the master's TTS).</summary>
        public void PlayTts(string text, int volume, bool toGroup)
        {
            var targets = new List<PlayerEngine> { this };
            if (toGroup)
            {
                PlayerEngine leader;
                foreach (var m in _rt.Groups.Cluster(this, out leader))
                    if (!targets.Contains(m)) targets.Add(m);
            }
            foreach (var p in targets) p.SpeakOnThis(text, volume);
        }

        internal void SpeakOnThis(string text, int volume)
        {
            int v = TtsVolumeFor(volume);
            _ttsHold = 2;
            var args = new SoapArgs().Add("Text", text).Add("Volume", v);
            _link.Call(Services.MiyueSystem, "PlayTTS", args, r =>
            {
                if (r.Ok) ClearError();
                else ReportError("PlayTTS 失败 / failed: " + r);
            });
        }

        // -- cast a URL (HA async_play_media) -------------------------------------------------------------
        /// <summary>
        /// Cast an arbitrary stream URL: SetAVTransportURI + Play. A sync SLAVE is not a valid cast target -
        /// it renders the master's multicast stream, so "play this here" means the whole group: cast to the
        /// group leader instead (the firmware rejects an untagged SetAVTransportURI on a slave with 705).
        /// No leader resolved -> fall back to this player so the 705 surfaces as a readable error.
        /// </summary>
        public void PlayUrl(string url)
        {
            var target = _rt.Groups.CastTarget(this);
            var args = SoapArgs.Instance().Add("CurrentURI", url).Add("CurrentURIMetaData", string.Empty);
            target.Call(Services.AVTransport, "SetAVTransportURI", args, r =>
            {
                if (!r.Ok)
                {
                    ReportError("SetAVTransportURI 失败 / failed" + (target != this ? " (leader " + target.Ip + ")" : "") + ": " + r);
                    return;
                }
                target.Call(Services.AVTransport, "Play", SoapArgs.Instance().Add("Speed", "1"), r2 =>
                {
                    AfterCommand(r2, "Play");
                    if (target != this) target.PollSoon(FastPollMs);
                });
            });
        }

        // -- groups ---------------------------------------------------------------------------------------
        private void JoinMasterByIp(string masterIp)
        {
            var master = _rt.FindPlayer(masterIp);
            if (master == null)
            {
                ReportError("主机不在本程序中 / Master " + masterIp + " is not a MiYue player in this program");
                return;
            }
            if (master == this)
            {
                ReportError("不能加入自己 / Cannot join itself");
                return;
            }
            _rt.Groups.Join(master, new List<PlayerEngine> { this }, this);
        }

        private void AddSlaveByIp(string slaveIp)
        {
            var slave = _rt.FindPlayer(slaveIp);
            if (slave == null)
            {
                ReportError("从机不在本程序中 / Player " + slaveIp + " is not a MiYue player in this program");
                return;
            }
            if (slave == this)
            {
                ReportError("不能加入自己 / Cannot add itself");
                return;
            }
            _rt.Groups.Join(this, new List<PlayerEngine> { slave }, this);
        }

        // -- misc -----------------------------------------------------------------------------------------
        private void Refresh()
        {
            _out.Resync();
            _modeDirty = _inputsDirty = _groupDirty = _scenesDirty = true;
            _deviceInfoRead = false;
            Publish();
            if (_link.Online) PollSoon(50);
            else _link.Reresolve();
        }
    }
}
