using System;
using System.Collections.Generic;
using MiYue.Core.Didl;
using MiYue.Core.Model;
using MiYue.Core.Util;
using S = MiYue.Core.Engine.PlayerSignals;

namespace MiYue.Core.Engine
{
    /// <summary>State -> outputs. Publish() writes the complete desired output state; the OutputCache
    /// forwards only changed values. Strand only.</summary>
    public sealed partial class PlayerEngine
    {
        /// <summary>
        /// Now playing: AVTransport TrackMetaData first (authoritative for what is actually sounding, incl.
        /// radio/BT/AirPlay single streams), the GetQueue item at CurrentIndex second (HA coordinator order).
        /// Cleared when the queue is empty and nothing is PLAYING (GetPositionInfo keeps stale metadata).
        /// </summary>
        public DidlItem CurrentTrack
        {
            get
            {
                if (_queueKnown && _queueTotal == 0 && _transport != TransportStates.Playing) return null;
                if (_metaTrack != null && (_metaTrack.Title.Length > 0 || _metaTrack.Artist.Length > 0)) return _metaTrack;
                if (_queueIndex >= 0 && _queueItem != null) return _queueItem;
                return _metaTrack;
            }
        }

        public string PlayMode
        {
            get { return _playMode; }
        }

        internal void Publish()
        {
            var o = _out;
            bool online = _link.Online;
            o.Digital(S.DoOnline, online);
            o.Analog(S.AoPort, online ? _link.Port : 0);
            o.Serial(S.SoStatus, _link.Status ?? string.Empty);
            o.Serial(S.SoLastError, _lastError);
            o.Serial(S.SoDeviceName, DeviceName);
            o.Serial(S.SoModel, _model);
            o.Serial(S.SoFirmware, _firmware);
            o.Serial(S.SoUdn, DeviceUuid);

            // transport
            string mapped = online ? _transportMapped : null;
            o.Digital(S.DoPlaying, mapped == "PLAYING");
            o.Digital(S.DoPaused, mapped == "PAUSED");
            o.Digital(S.DoStopped, mapped == "STOPPED");
            o.Serial(S.SoTransportState, online ? (_transport ?? string.Empty) : "OFFLINE");

            // volume / mute
            int vol = _volume ?? 0;
            o.Analog(S.AoVolume, vol);
            o.Analog(S.AoVolumeRaw, (int)Math.Round(vol * 65535.0 / 100.0));
            o.Digital(S.DoMuted, _mute ?? false);

            // play mode
            string mode = _playMode ?? string.Empty;
            o.Serial(S.SoPlayMode, mode);
            o.Digital(S.DoModeNormal, mode == PlayModes.Normal);
            o.Digital(S.DoModeRepeatAll, mode == PlayModes.RepeatAll);
            o.Digital(S.DoModeRepeatOne, mode == PlayModes.RepeatOne);
            o.Digital(S.DoModeShuffle, mode == PlayModes.Shuffle);

            // now playing
            var t = online ? CurrentTrack : null;
            o.Serial(S.SoTitle, t != null ? t.Title : string.Empty);
            o.Serial(S.SoArtist, t != null ? t.Artist : string.Empty);
            o.Serial(S.SoAlbum, t != null ? t.Album : string.Empty);
            o.Serial(S.SoCoverUrl, t != null ? t.AlbumArt : string.Empty);
            PublishPosition();
            o.Analog(S.AoQueueIndex, _queueIndex >= 0 ? _queueIndex + 1 : 0);
            o.Analog(S.AoQueueTotal, _queueTotal);
            o.Digital(S.DoCanSkip, online && SkipAllowed);

            // sources
            var inp = _inputs;
            string active = inp != null ? inp.ActiveSource : Sources.Music;
            o.Serial(S.SoSource, inp != null ? active : string.Empty);
            o.Digital(S.DoSourceMusic, inp != null && active == Sources.Music);
            o.Digital(S.DoSourceSpdif, inp != null && inp.OpenSpdif);
            o.Digital(S.DoSourceBluetooth, inp != null && inp.OpenBluetooth);
            o.Digital(S.DoSourceAux, inp != null && inp.OpenAux);
            o.Digital(S.DoHasSpdif, inp != null && inp.WithSpdif);
            o.Digital(S.DoHasBluetooth, inp != null && inp.WithBluetooth);
            o.Digital(S.DoHasAux, inp != null && inp.WithAux);
            o.Analog(S.AoBluetoothStatus, inp != null ? inp.BluetoothStatus : 0);

            // group
            PublishGroup();

            // scenes
            o.Analog(S.AoSceneCount, _scenes.Count);
            for (int i = 0; i < S.SceneSlots; i++)
                o.Serial((ushort)(S.SoSceneNameBase + 1 + i), i < _scenes.Count ? _scenes[i].Name : string.Empty);
        }

        internal void PublishGroup()
        {
            var o = _out;
            var g = _group;
            PlayerEngine leader;
            var members = _rt.Groups.Cluster(this, out leader);
            o.Serial(S.SoGroupRole, _groupRead ? g.Role : string.Empty);
            o.Serial(S.SoGroupMasterIp, g.IsStandalone ? string.Empty : g.MasterIp);
            o.Serial(S.SoGroupId, g.EffectiveGroupId);
            o.Digital(S.DoGroupMaster, g.IsMaster);
            o.Digital(S.DoGroupSlave, g.IsSlave);
            o.Digital(S.DoGrouped, members.Count >= 2 || g.IsSlave);
            o.Digital(S.DoGroupBusy, _rt.Groups.Busy);
            o.Analog(S.AoGroupMemberCount, members.Count);
            var names = new List<string>();
            foreach (var m in members) names.Add(m.DeviceName);
            o.Serial(S.SoGroupMembers, string.Join(",", names.ToArray()));
        }

        private void PublishPosition()
        {
            bool online = _link.Online;
            int pos = online ? CurrentPositionSeconds : 0;
            int dur = online ? _durSec : 0;
            var o = _out;
            o.Analog(S.AoPosition, pos);
            o.Analog(S.AoDuration, dur);
            o.Analog(S.AoProgressRaw, dur > 0 ? (int)Math.Round(Math.Min(pos, dur) * 65535.0 / dur) : 0);
            o.Serial(S.SoPositionText, online && (dur > 0 || pos > 0) ? TimeText.ToDisplay(pos) : string.Empty);
            o.Serial(S.SoDurationText, online && dur > 0 ? TimeText.ToDisplay(dur) : string.Empty);
        }
    }
}
