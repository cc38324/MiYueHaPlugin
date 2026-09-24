using System;
using System.Collections.Generic;
using MiYue.Core.Soap;
using MiYue.Core.Util;

namespace MiYue.Core.Model
{
    /// <summary>
    /// miyue:songSrc semantics and GetQueue CurrentIndex sentinels (port of HA song_src.py, which mirrors
    /// the Flutter controller's song_src.dart).
    /// </summary>
    public static class SongSrc
    {
        public const int Local = 0;
        public const int Aux = 1;
        public const int Spdif = 2;
        public const int DlnaCast = 3;
        public const int Ximalaya = 4;
        public const int Douban = 5;
        public const int Radio = 8;
        public const int Bluetooth = 9;
        public const int Ergeduoduo = 10;
        public const int Kugou = 11;
        public const int KugouBrowse = 12;
        public const int SyncSlave = 13;
        public const int Lava = 14;
        public const int Netease = 15;
        public const int NeteaseFm = 16;
        public const int AirPlay = 20;
        public const int NasDms = 21;
        public const int Unknown = -1;

        /// <summary>Sources the device drives as a true on-demand queue.</summary>
        public static readonly int[] QueueSrcs = { Local, Ximalaya, Ergeduoduo, Kugou, KugouBrowse, Netease, NasDms };

        /// <summary>Queue sources + Bluetooth/AirPlay (reverse control) + sync slave (Linux forwards Next).</summary>
        public static readonly int[] SkipSrcs = { Local, Ximalaya, Ergeduoduo, Kugou, KugouBrowse, Netease, NasDms, Bluetooth, AirPlay, SyncSlave };

        /// <summary>The Linux sync-slave sentinel (-105): a Linux slave keeps its renderer and forwards Next.</summary>
        public const int SentinelSyncSlave = -105;

        public static bool IsQueueSrc(int src)
        {
            return Array.IndexOf(QueueSrcs, src) >= 0;
        }

        /// <summary>Whether next/previous makes sense for this source (unknown / negative -> yes).</summary>
        public static bool CanSkip(int src)
        {
            return src < 0 || Array.IndexOf(SkipSrcs, src) >= 0;
        }

        /// <summary>GetQueue CurrentIndex owner sentinel -> songSrc, or null when unmapped.</summary>
        public static int? FromSentinel(int index)
        {
            switch (index)
            {
                case -100: return Radio;
                case -101: return DlnaCast;
                case -102: return AirPlay;
                case -103: return Aux;
                case -104: return Spdif;
                case -105: return SyncSlave;
                case -106: return Bluetooth;
                default: return null;
            }
        }

        /// <summary>Best-effort songSrc of what is sounding now. Precedence: an OPEN external input, then a
        /// queue owner sentinel, then the current track's own tag; Unknown stays permissive.
        /// -107..-109 (TTS / voice / intercom overlays) fall through to the track tag so the verdict does not
        /// flap during an announcement.</summary>
        public static int Effective(string activeInput, int queueIndex, int? trackSrc)
        {
            if (activeInput == Sources.Aux) return Aux;
            if (activeInput == Sources.Spdif) return Spdif;
            if (activeInput == Sources.Bluetooth) return Bluetooth;
            if (queueIndex <= -100)
            {
                var s = FromSentinel(queueIndex);
                if (s.HasValue) return s.Value;
            }
            if (trackSrc.HasValue) return trackSrc.Value;
            return Unknown;
        }
    }

    /// <summary>SelectSource / SetInputOpen source names (contract: SPDIF|Coaxial, Bluetooth|BT, AUX, Music).</summary>
    public static class Sources
    {
        public const string Music = "Music";
        public const string Spdif = "SPDIF";
        public const string Bluetooth = "Bluetooth";
        public const string Aux = "AUX";
    }

    /// <summary>MiyueAudioSource.GetExternalInputs.</summary>
    public sealed class ExternalInputs
    {
        public bool WithAux, OpenAux, WithSpdif, OpenSpdif, WithBluetooth, OpenBluetooth;
        public int BluetoothStatus;

        public static ExternalInputs From(SoapResult r)
        {
            return new ExternalInputs
            {
                WithAux = r.Get("WithAux") == "1",
                OpenAux = r.Get("OpenAux") == "1",
                WithSpdif = r.Get("WithSpdif") == "1",
                OpenSpdif = r.Get("OpenSpdif") == "1",
                WithBluetooth = r.Get("WithBluetooth") == "1",
                OpenBluetooth = r.Get("OpenBluetooth") == "1",
                BluetoothStatus = r.GetInt("BluetoothStatus", 0),
            };
        }

        /// <summary>The external input that is switched on (HA parse_active_source order AUX, SPDIF, Bluetooth), or null.</summary>
        public string ActiveInput
        {
            get
            {
                if (OpenAux) return Sources.Aux;
                if (OpenSpdif) return Sources.Spdif;
                if (OpenBluetooth) return Sources.Bluetooth;
                return null;
            }
        }

        /// <summary>Active source for display: the open external input, else "Music".</summary>
        public string ActiveSource
        {
            get { return ActiveInput ?? Sources.Music; }
        }
    }

    /// <summary>MiyueQueue play modes.</summary>
    public static class PlayModes
    {
        public const string Normal = "NORMAL";
        public const string RepeatAll = "REPEAT_ALL";
        public const string RepeatOne = "REPEAT_ONE";
        public const string Shuffle = "SHUFFLE";

        /// <summary>SetPlayMode is case-insensitive and maps anything unknown to NORMAL - mirror that.</summary>
        public static string Normalize(string mode)
        {
            var m = (mode ?? string.Empty).Trim().ToUpperInvariant();
            switch (m)
            {
                case RepeatAll:
                case RepeatOne:
                case Shuffle:
                    return m;
                default:
                    return Normal;
            }
        }

        /// <summary>Cycle order NORMAL -> REPEAT_ALL -> REPEAT_ONE -> SHUFFLE -> NORMAL (C4 dashboard order).</summary>
        public static string Next(string mode)
        {
            switch (Normalize(mode))
            {
                case Normal: return RepeatAll;
                case RepeatAll: return RepeatOne;
                case RepeatOne: return Shuffle;
                default: return Normal;
            }
        }
    }

    /// <summary>MiyueGroup roles (firmware spelling).</summary>
    public static class Roles
    {
        public const string None = "None";
        public const string Master = "Master";
        public const string Slave = "Slave";
    }

    /// <summary>MiyueGroup.GetGroupInfo.</summary>
    public sealed class GroupInfo
    {
        public string Role = Roles.None;
        public string GroupId = string.Empty;
        public string MasterIp = string.Empty;
        public string MulticastAddr = string.Empty;
        public string DeviceUuid = string.Empty;
        public string DeviceName = string.Empty;

        public static GroupInfo From(SoapResult r)
        {
            var role = r.Get("Role", Roles.None).Trim();
            if (role.Length == 0) role = Roles.None;
            return new GroupInfo
            {
                Role = role,
                GroupId = r.Get("GroupID").Trim(),
                MasterIp = r.Get("MasterIp").Trim(),
                MulticastAddr = r.Get("MulticastAddr").Trim(),
                DeviceUuid = r.Get("DeviceUUID").Trim(),
                DeviceName = r.Get("DeviceName").Trim(),
            };
        }

        /// <summary>Role==None vetoes any GroupID a standalone device still reports.</summary>
        public bool IsStandalone
        {
            get { return Role == Roles.None; }
        }

        public bool IsMaster
        {
            get { return Role == Roles.Master; }
        }

        public bool IsSlave
        {
            get { return Role == Roles.Slave; }
        }

        /// <summary>GroupID, but empty when standalone or "null".</summary>
        public string EffectiveGroupId
        {
            get
            {
                if (IsStandalone || GroupId.Length == 0 || GroupId == "null") return string.Empty;
                return GroupId;
            }
        }
    }

    /// <summary>A device scene (情景, MiyueSensor.ListSensors). Activate-only: scenes are authored on the speaker.</summary>
    public sealed class Scene
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Cmd = string.Empty;

        /// <summary>Parse ListSensors JSON; only isOpen==1 scenes are exposed (Linux still honours isOpen on
        /// ExecuteSensor, HA scene.py _is_open). Order is the device's order.</summary>
        public static List<Scene> ParseOpen(string json)
        {
            var list = new List<Scene>();
            foreach (var o in MiniJson.ParseObjectList(json))
            {
                var id = MiniJson.Str(o, "id");
                if (id.Length == 0) continue;
                if (MiniJson.Int(o, "isOpen", 0) != 1) continue;
                var name = MiniJson.Str(o, "cmdName");
                list.Add(new Scene { Id = id, Name = name.Length > 0 ? name : "#" + id, Cmd = MiniJson.Str(o, "cmd") });
            }
            return list;
        }
    }

    /// <summary>A collected songlist or board (GetCollectedSonglists / GetCollectedBoards JSON; id is a STRING).</summary>
    public sealed class Songlist
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Icon = string.Empty;
        public int Count;

        public static List<Songlist> Parse(string json)
        {
            var list = new List<Songlist>();
            foreach (var o in MiniJson.ParseObjectList(json))
            {
                var id = MiniJson.Str(o, "id");
                if (id.Length == 0) continue;
                var name = MiniJson.Str(o, "name");
                list.Add(new Songlist
                {
                    Id = id,
                    Name = name.Length > 0 ? name : "?",
                    Icon = MiniJson.Str(o, "icon"),
                    Count = MiniJson.Int(o, "count", 0),
                });
            }
            return list;
        }
    }

    /// <summary>AVTransport CurrentTransportState values.</summary>
    public static class TransportStates
    {
        public const string Playing = "PLAYING";
        public const string Paused = "PAUSED_PLAYBACK";
        public const string Stopped = "STOPPED";
        public const string Transitioning = "TRANSITIONING";
        public const string NoMedia = "NO_MEDIA_PRESENT";
    }
}
