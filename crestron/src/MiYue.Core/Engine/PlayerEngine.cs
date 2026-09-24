using System;
using System.Collections.Generic;
using MiYue.Core.Didl;
using MiYue.Core.Model;
using MiYue.Core.Soap;

namespace MiYue.Core.Engine
{
    /// <summary>
    /// One MiYue player: poll loop, change-detected outputs and every device command.
    /// Public entry points (Start/Stop/Digital/Analog/Serial) may be called from any thread - they only
    /// post onto the runtime strand and return, so the SIMPL+ thread is never blocked. Everything else
    /// (members marked "strand only") runs on the strand.
    ///
    /// Files: PlayerEngine.cs (API, dispatch) | .Poll.cs (poll chain) | .Commands.cs (device commands)
    ///        | .Publish.cs (state -> outputs).
    /// </summary>
    public sealed partial class PlayerEngine
    {
        public const int DefaultPollMs = 3000;
        public const int FastPollMs = 800;
        public const int PositionTickMs = 1000;
        public const int VolumeRampMs = 400;
        public const int DefaultTtsVolume = 30;

        private readonly Runtime _rt;
        private readonly DeviceLink _link;
        private readonly OutputCache _out;

        // configuration
        private int _pollMs = DefaultPollMs;
        private int _volumeStep = 5;
        private bool _running;

        // device identity (description.xml + GetDeviceInfo)
        private string _deviceName = string.Empty;
        private string _model = string.Empty;
        private string _firmware = string.Empty;
        private string _udn = string.Empty;
        private bool _deviceInfoRead;

        // polled state
        private string _transport;            // raw CurrentTransportState, null until read
        private string _transportMapped;      // PLAYING | PAUSED | STOPPED (TRANSITIONING keeps the previous)
        private int _ttsHold;                 // cycles to ignore transport after PlayTTS (it flips to PLAYING)
        private int? _volume;
        private bool? _mute;
        private int _rcSkip;                  // cycles to skip RenderingControl after a 404 (hidden renderer)
        private bool _rcSkippedThisCycle;
        private DidlItem _metaTrack;          // AVTransport TrackMetaData item
        private DidlItem _queueItem;          // GetQueue item at CurrentIndex
        private int _posSec;
        private int _durSec;
        private long _posSampleMs;
        private int _queueIndex = -1;
        private int _queueTotal;
        private bool _queueKnown;
        private string _playMode;
        private ExternalInputs _inputs;
        private GroupInfo _group = new GroupInfo();
        private bool _groupRead;
        private List<Scene> _scenes = new List<Scene>();
        private long _scenesReadAt = -1;
        private string _lastError = string.Empty;

        // poll machinery
        private bool _polling;
        private int _cycle;
        private int _cycleId;
        private long _cycleStartedMs;
        private int? _pendingMs;
        private ITimerHandle _pollTimer;
        private ITimerHandle _watchdog;
        private ITimerHandle _positionTimer;
        private bool _modeDirty = true;
        private bool _inputsDirty = true;
        private bool _groupDirty = true;
        private bool _scenesDirty = true;

        // stale-read guards: a Get* issued before a Set* must not overwrite the optimistic value
        private int _tick;
        private int _volCmd;
        private int _muteCmd;
        private int _modeCmd;

        // volume ramp / coalescing
        private int _rampDir;
        private ITimerHandle _rampTimer;
        private int? _volTarget;
        private bool _volSending;

        // TTS
        private int _ttsVolume;
        private bool _ttsToGroup;

        /// <summary>Raised (strand) when GetQueue's CurrentIndex / Total changed - the browse module refreshes its queue page.</summary>
        public event Action QueueStateChanged;

        public PlayerEngine(Runtime rt)
        {
            if (rt == null) throw new ArgumentNullException("rt");
            _rt = rt;
            _link = new DeviceLink(rt);
            _out = new OutputCache(rt.Log);
            _link.WentOnline += OnLinkOnline;
            _link.WentOffline += OnLinkOffline;
            _link.StatusChanged += s => Publish();
            Ip = string.Empty;
        }

        // ---------------------------------------------------------------------------------------
        // identity used by the registry / group manager (strand only for mutation)
        // ---------------------------------------------------------------------------------------
        /// <summary>The configured IP address (registry key).</summary>
        public string Ip { get; private set; }

        public string DeviceName
        {
            get
            {
                if (_deviceName.Length > 0) return _deviceName;
                if (_group.DeviceName.Length > 0) return _group.DeviceName;
                var d = _link.Description;
                if (d != null && d.FriendlyName.Length > 0) return d.FriendlyName;
                return Ip;
            }
        }

        /// <summary>TargetUUID for SetGroupConfig: GetGroupInfo DeviceUUID, else the description UDN.</summary>
        public string DeviceUuid
        {
            get
            {
                if (_group.DeviceUuid.Length > 0) return _group.DeviceUuid;
                if (_udn.Length > 0) return _udn;
                var d = _link.Description;
                return d != null ? d.Udn : string.Empty;
            }
        }

        public GroupInfo Group
        {
            get { return _group; }
        }

        public bool GroupRead
        {
            get { return _groupRead; }
        }

        public bool Online
        {
            get { return _link.Online; }
        }

        public int? Volume
        {
            get { return _volume; }
        }

        public int QueueIndex
        {
            get { return _queueIndex; }
        }

        public int QueueTotal
        {
            get { return _queueTotal; }
        }

        public string TransportState
        {
            get { return _transport; }
        }

        public IList<Scene> Scenes
        {
            get { return _scenes.AsReadOnly(); }
        }

        public DeviceLink Link
        {
            get { return _link; }
        }

        public OutputCache Outputs
        {
            get { return _out; }
        }

        public Runtime Runtime
        {
            get { return _rt; }
        }

        // ---------------------------------------------------------------------------------------
        // thread-safe public API (posts onto the strand)
        // ---------------------------------------------------------------------------------------
        /// <summary>Configure and start. pollSeconds 1..60 (0 = default 3), volumeStep 1..20 (0 = 5).</summary>
        public void Start(string ip, int pollSeconds, int volumeStep, IOutputSink sink)
        {
            _rt.Strand.Post(() => DoStart(ip, pollSeconds, volumeStep, sink));
        }

        public void Stop()
        {
            _rt.Strand.Post(DoStop);
        }

        public void Digital(ushort index, bool value)
        {
            _rt.Strand.Post(() => OnDigital(index, value));
        }

        public void Analog(ushort index, ushort value)
        {
            _rt.Strand.Post(() => OnAnalog(index, value));
        }

        public void Serial(ushort index, string value)
        {
            _rt.Strand.Post(() => OnSerial(index, value ?? string.Empty));
        }

        // ---------------------------------------------------------------------------------------
        // lifecycle (strand)
        // ---------------------------------------------------------------------------------------
        private void DoStart(string ip, int pollSeconds, int volumeStep, IOutputSink sink)
        {
            DoStop();
            Ip = (ip ?? string.Empty).Trim();
            _pollMs = (pollSeconds <= 0 ? DefaultPollMs / 1000 : Math.Min(60, pollSeconds)) * 1000;
            _volumeStep = volumeStep <= 0 ? 5 : Math.Min(20, volumeStep);
            _out.Sink = sink;
            _out.Resync();
            _running = true;
            _rt.Register(this);
            _rt.Log.Debug("MiYue " + Ip + ": start (poll " + _pollMs + " ms)");
            Publish();
            _link.Start(Ip);
            ArmPositionTick();
        }

        private void DoStop()
        {
            if (!_running) return;
            _running = false;
            _cycleId++;
            _polling = false;
            Cancel(ref _pollTimer);
            Cancel(ref _watchdog);
            Cancel(ref _positionTimer);
            Cancel(ref _rampTimer);
            _link.Stop();
            _rt.Unregister(this);
            _rt.Groups.TopologyChanged();
        }

        private void OnLinkOnline()
        {
            var d = _link.Description;
            if (d != null)
            {
                _udn = d.Udn;
                if (_model.Length == 0) _model = d.ModelName;
                if (_firmware.Length == 0) _firmware = d.SoftwareVersion;
            }
            _deviceInfoRead = false;
            _modeDirty = _inputsDirty = _groupDirty = _scenesDirty = true;
            _rcSkip = 0;
            _out.Resync();
            Publish();
            if (_running) PollSoon(50);
        }

        private void OnLinkOffline()
        {
            _cycleId++;
            _polling = false;
            Cancel(ref _watchdog);
            Publish();
            _rt.Groups.TopologyChanged();
            if (_running) ArmPoll(_pollMs);
        }

        // ---------------------------------------------------------------------------------------
        // input dispatch (strand)
        // ---------------------------------------------------------------------------------------
        private void OnDigital(ushort index, bool value)
        {
            // levels first
            switch (index)
            {
                case PlayerSignals.DiVolUp:
                    RampVolume(value ? +1 : 0, +1);
                    return;
                case PlayerSignals.DiVolDown:
                    RampVolume(value ? -1 : 0, -1);
                    return;
                case PlayerSignals.DiTtsToGroup:
                    _ttsToGroup = value;
                    return;
            }
            if (!value) return; // pulses act on the rising edge only

            if (index > PlayerSignals.DiSceneBase && index <= PlayerSignals.DiSceneBase + PlayerSignals.SceneSlots)
            {
                int n = index - PlayerSignals.DiSceneBase;
                if (n <= _scenes.Count) ExecuteScene(_scenes[n - 1].Id);
                else ReportError("情景 " + n + " 不存在 / Scene " + n + " not defined on the device");
                return;
            }

            switch (index)
            {
                case PlayerSignals.DiPlay: Play(); break;
                case PlayerSignals.DiPause: Pause(); break;
                case PlayerSignals.DiPlayPause:
                    if (_transport == TransportStates.Playing) Pause();
                    else Play();
                    break;
                case PlayerSignals.DiStop: StopTransport(); break;
                case PlayerSignals.DiNext: Skip(true); break;
                case PlayerSignals.DiPrevious: Skip(false); break;
                case PlayerSignals.DiMuteOn: SetMute(true); break;
                case PlayerSignals.DiMuteOff: SetMute(false); break;
                case PlayerSignals.DiMuteToggle: SetMute(!(_mute ?? false)); break;
                case PlayerSignals.DiModeNormal: SetPlayMode(PlayModes.Normal); break;
                case PlayerSignals.DiModeRepeatAll: SetPlayMode(PlayModes.RepeatAll); break;
                case PlayerSignals.DiModeRepeatOne: SetPlayMode(PlayModes.RepeatOne); break;
                case PlayerSignals.DiModeShuffle: SetPlayMode(PlayModes.Shuffle); break;
                case PlayerSignals.DiModeCycle: SetPlayMode(PlayModes.Next(_playMode)); break;
                case PlayerSignals.DiSourceMusic: SelectSource(Sources.Music); break;
                case PlayerSignals.DiSourceSpdif: SelectSource(Sources.Spdif); break;
                case PlayerSignals.DiSourceBluetooth: SelectSource(Sources.Bluetooth); break;
                case PlayerSignals.DiSourceAux: SelectSource(Sources.Aux); break;
                case PlayerSignals.DiInputSpdifOn: SetInputOpen(Sources.Spdif, true); break;
                case PlayerSignals.DiInputSpdifOff: SetInputOpen(Sources.Spdif, false); break;
                case PlayerSignals.DiInputBluetoothOn: SetInputOpen(Sources.Bluetooth, true); break;
                case PlayerSignals.DiInputBluetoothOff: SetInputOpen(Sources.Bluetooth, false); break;
                case PlayerSignals.DiInputAuxOn: SetInputOpen(Sources.Aux, true); break;
                case PlayerSignals.DiInputAuxOff: SetInputOpen(Sources.Aux, false); break;
                case PlayerSignals.DiGroupBecomeMaster: _rt.Groups.Join(this, new List<PlayerEngine>(), this); break;
                case PlayerSignals.DiGroupLeave: _rt.Groups.Unjoin(this); break;
                case PlayerSignals.DiGroupDissolve: _rt.Groups.Dissolve(this); break;
                case PlayerSignals.DiRefresh: Refresh(); break;
                case PlayerSignals.DiReconnect: _link.Start(Ip); break;
                default:
                    _rt.Log.Debug("MiYue " + Ip + ": unknown digital input " + index);
                    break;
            }
        }

        private void OnAnalog(ushort index, ushort value)
        {
            switch (index)
            {
                case PlayerSignals.AiVolumeSet:
                    SetVolume(Math.Min(100, (int)value));
                    break;
                case PlayerSignals.AiVolumeSetRaw:
                    SetVolume((int)Math.Round(value * 100.0 / 65535.0));
                    break;
                case PlayerSignals.AiSeekSeconds:
                    Seek(value);
                    break;
                case PlayerSignals.AiTtsVolume:
                    _ttsVolume = Math.Min(100, (int)value);
                    break;
                default:
                    _rt.Log.Debug("MiYue " + Ip + ": unknown analog input " + index);
                    break;
            }
        }

        private void OnSerial(ushort index, string value)
        {
            string v = value.Trim();
            switch (index)
            {
                case PlayerSignals.SiTtsText:
                    if (v.Length > 0) PlayTts(value, _ttsVolume, _ttsToGroup);
                    break;
                case PlayerSignals.SiPlayUrl:
                    if (v.Length > 0) PlayUrl(v);
                    break;
                case PlayerSignals.SiGroupJoinMaster:
                    if (v.Length > 0) JoinMasterByIp(v);
                    break;
                case PlayerSignals.SiGroupAddSlave:
                    if (v.Length > 0) AddSlaveByIp(v);
                    break;
                case PlayerSignals.SiSceneExecute:
                    if (v.Length > 0) ExecuteSceneByKey(v);
                    break;
                default:
                    _rt.Log.Debug("MiYue " + Ip + ": unknown serial input " + index);
                    break;
            }
        }

        // ---------------------------------------------------------------------------------------
        // helpers (strand)
        // ---------------------------------------------------------------------------------------
        /// <summary>SOAP call on this player's link (strand only).</summary>
        public void Call(string service, string action, SoapArgs args, Action<SoapResult> done)
        {
            _link.Call(service, action, args, done);
        }

        internal void ReportError(string message)
        {
            _lastError = message ?? string.Empty;
            _rt.Log.Error("MiYue " + Ip + ": " + _lastError);
            Publish();
        }

        internal void ClearError()
        {
            if (_lastError.Length == 0) return;
            _lastError = string.Empty;
            Publish();
        }

        private static void Cancel(ref ITimerHandle t)
        {
            if (t != null)
            {
                t.Cancel();
                t = null;
            }
        }

        private int Bump()
        {
            return ++_tick;
        }
    }
}
