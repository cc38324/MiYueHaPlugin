"""MiyueDevice: one speaker's control surface for the HA integration.

Wraps the raw SoapClient with typed methods. Read paths here are all verified
against live firmware (M330B / M100). Group *write* sequencing (join/unjoin)
is confirmed from the Flutter controller in docs/CONTRACT.md and implemented in
group.py; this object exposes the primitives it uses.

The HTTP port drifts across reboots, so a MiyueDevice can be re-based onto a new
(host, port) via `rebase()` without losing identity (UDN is stable).
"""

from __future__ import annotations

import json
import logging
from dataclasses import dataclass, field

import aiohttp

from .const import (
    AVTRANSPORT_CONTROL,
    EXTERNAL_INPUT_FLAGS,
    RENDERING_CONTROL,
    ROLE_NONE,
    ST_ALARM,
    ST_AUDIOSOURCE,
    ST_AVTRANSPORT,
    ST_GROUP,
    ST_LIBRARY,
    ST_QUEUE,
    ST_RENDERING,
    ST_SENSOR,
    ST_SYSTEM,
    miyue_control,
)
from .didl import MiyueTrack, parse_didl
from .soap import SoapClient, SoapError

_LOGGER = logging.getLogger(__name__)


@dataclass
class GroupInfo:
    """MiyueGroup.GetGroupInfo result."""

    role: str = ROLE_NONE
    group_id: str = ""
    master_ip: str = ""
    multicast_addr: str = ""
    multicast_port: str = ""
    device_uuid: str = ""
    device_name: str = ""

    @property
    def is_standalone(self) -> bool:
        # Invariant: role=="None" vetoes any GroupID a standalone may still emit.
        return self.role == ROLE_NONE

    @property
    def is_master(self) -> bool:
        return self.role == "Master"

    @property
    def is_slave(self) -> bool:
        return self.role == "Slave"

    @property
    def effective_group_id(self) -> str:
        """GroupID, but empty when standalone (role vetoes gid)."""
        return "" if self.is_standalone else self.group_id


@dataclass
class DeviceState:
    """A full snapshot the coordinator hands to entities each update cycle."""

    # identity / system
    name: str = ""
    ip_address: str = ""
    software_version: str = ""
    model: str = ""
    # transport (from AVTransport, filled by media_player via DmrDevice)
    group: GroupInfo = field(default_factory=GroupInfo)
    member_uuids: list[str] = field(default_factory=list)
    member_names: list[str] = field(default_factory=list)
    # now-playing (from MiyueQueue.GetTimeline current item)
    current_track: MiyueTrack | None = None
    play_mode: str = "NORMAL"
    # external inputs (MiyueAudioSource.GetExternalInputs)
    available_sources: list[str] = field(default_factory=list)
    active_source: str | None = None


class MiyueDevice:
    """Control + read one MiYue speaker over its private + standard services."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        udn: str,
        host: str,
        port: int,
    ) -> None:
        self.udn = udn  # stable identity, e.g. "uuid:782d9562-..."
        self.host = host
        self.port = port
        self._soap = SoapClient(session, f"http://{host}:{port}")

    @property
    def base_url(self) -> str:
        return self._soap.base_url

    def rebase(self, host: str, port: int) -> None:
        """Point at a new location after a reboot changed the HTTP port."""
        if (host, port) == (self.host, self.port):
            return
        _LOGGER.debug("miyue %s rebased %s:%s -> %s:%s", self.udn, self.host,
                      self.port, host, port)
        self.host, self.port = host, port
        self._soap = self._soap.with_base(f"http://{host}:{port}")

    # -- MiyueGroup ---------------------------------------------------------
    async def get_group_info(self) -> GroupInfo:
        r = await self._soap.call(
            miyue_control("MiyueGroup"), ST_GROUP, "GetGroupInfo"
        )
        return GroupInfo(
            role=r.get("Role", ROLE_NONE),
            group_id=r.get("GroupID", ""),
            master_ip=r.get("MasterIp", ""),
            multicast_addr=r.get("MulticastAddr", ""),
            multicast_port=r.get("MulticastPort", ""),
            device_uuid=r.get("DeviceUUID", ""),
            device_name=r.get("DeviceName", ""),
        )

    async def get_member_list(self) -> tuple[list[str], list[str]]:
        r = await self._soap.call(
            miyue_control("MiyueGroup"), ST_GROUP, "GetMemberList"
        )
        uuids = _split_members(r.get("MemberUUIDs", ""))
        names = _split_members(r.get("MemberNames", ""))
        return uuids, names

    async def set_group_config(
        self,
        target_uuid: str,
        group_id: str,
        master_ip: str,
        multicast_addr: str,
        role: str,
    ) -> None:
        """Low-level MiyueGroup.SetGroupConfig. Orchestrated by group.py.

        Invariant: master_ip must equal the master's current IPv4 — devices
        self-derive it; never push a foreign value. multicast_addr must be the
        master's GetGroupInfo.MulticastAddr, never the legacy default
        239.10.10.230 (that collision cross-talks the whole LAN).
        """
        await self._soap.call(
            miyue_control("MiyueGroup"),
            ST_GROUP,
            "SetGroupConfig",
            {
                "TargetUUID": target_uuid,
                "GroupID": group_id,
                "MasterIp": master_ip,
                "MulticastAddr": multicast_addr,
                "Role": role,
            },
        )

    async def leave_group(self) -> None:
        await self._soap.call(miyue_control("MiyueGroup"), ST_GROUP, "LeaveGroup")

    async def set_group_volume(self, volume: int) -> None:
        await self._soap.call(
            miyue_control("MiyueGroup"), ST_GROUP, "SetVolume",
            {"Volume": str(volume)},
        )

    # -- MiyueSystem --------------------------------------------------------
    async def get_device_info(self) -> dict[str, str]:
        return await self._soap.call(
            miyue_control("MiyueSystem"), ST_SYSTEM, "GetDeviceInfo"
        )

    async def reboot(self) -> None:
        await self._soap.call(miyue_control("MiyueSystem"), ST_SYSTEM, "Reboot")

    # -- MiyueAudioSource ---------------------------------------------------
    async def get_external_inputs(self) -> dict[str, str]:
        return await self._soap.call(
            miyue_control("MiyueAudioSource"), ST_AUDIOSOURCE, "GetExternalInputs"
        )

    async def select_source(self, source: str) -> None:
        await self._soap.call(
            miyue_control("MiyueAudioSource"), ST_AUDIOSOURCE, "SelectSource",
            {"Source": source},
        )

    @staticmethod
    def parse_available_sources(inputs: dict[str, str]) -> list[str]:
        """Which external inputs the hardware physically has (With* flags)."""
        available = []
        for source, (with_flag, _open_flag) in EXTERNAL_INPUT_FLAGS.items():
            if inputs.get(with_flag) == "1":
                available.append(source)
        return available

    @staticmethod
    def parse_active_source(inputs: dict[str, str]) -> str | None:
        """Which external input is currently switched on, if any."""
        for source, (_with_flag, open_flag) in EXTERNAL_INPUT_FLAGS.items():
            if inputs.get(open_flag) == "1":
                return source
        return None

    # -- MiyueQueue ---------------------------------------------------------
    async def get_queue_state(
        self,
    ) -> tuple[list[MiyueTrack], int, int, MiyueTrack | None]:
        """Return (tracks, current_index, total, current_track).

        Reads GetQueue -- the universal queue read: both firmwares implement
        it, and its CurrentIndex is the QUEUE index (SeekToTrack's space).
        GetTimeline exists only on the Android firmware (the Linux one answers
        a SOAP fault) and its index is a timeline position that diverges from
        the queue index under SHUFFLE -- never use it for control.
        CurrentIndex < 0 means the queue is not what is sounding on the LINUX
        firmware (-1 idle, <= -100 owner sentinels); the ANDROID firmware
        returns the RAW index (-100 only from the radio path), so a stale
        positive index may persist while an external source owns playback.
        The first page is capped at 500 items; when the current track lies
        beyond it, fetch that one item so now-playing survives long queues.
        """
        r = await self._soap.call(
            miyue_control("MiyueQueue"), ST_QUEUE, "GetQueue",
            {"StartIndex": "0", "RequestedCount": "500"},
        )
        tracks = parse_didl(r.get("Result", ""))
        total = _to_int(r.get("Total", "0")) or len(tracks)
        try:
            current = int(r.get("CurrentIndex", "-1"))
        except ValueError:
            current = -1
        current_track: MiyueTrack | None = None
        if 0 <= current < len(tracks):
            current_track = tracks[current]
        elif len(tracks) <= current < total:
            try:
                page = await self._soap.call(
                    miyue_control("MiyueQueue"), ST_QUEUE, "GetQueue",
                    {"StartIndex": str(current), "RequestedCount": "1"},
                )
            except SoapError:
                # Optional enhancement only -- never discard the successful
                # first page over it (TrackMetaData still backfills the card).
                pass
            else:
                items = parse_didl(page.get("Result", ""))
                current_track = items[0] if items else None
        return tracks, current, total, current_track

    async def get_current_track(self) -> MiyueTrack | None:
        _tracks, _current, _total, current_track = await self.get_queue_state()
        return current_track

    async def get_play_mode(self) -> str:
        r = await self._soap.call(
            miyue_control("MiyueQueue"), ST_QUEUE, "GetPlayMode"
        )
        return r.get("PlayMode", "NORMAL")

    async def set_play_mode(self, mode: str) -> None:
        await self._soap.call(
            miyue_control("MiyueQueue"), ST_QUEUE, "SetPlayMode",
            {"PlayMode": mode},
        )

    async def seek_to_track(self, index: int) -> None:
        await self._soap.call(
            miyue_control("MiyueQueue"), ST_QUEUE, "SeekToTrack",
            {"Index": str(index)},
        )

    # -- Standard AVTransport (embedded MediaRenderer) ----------------------
    async def get_transport_info(self) -> dict[str, str]:
        return await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "GetTransportInfo",
            {"InstanceID": 0},
        )

    async def get_position_info(self) -> dict[str, str]:
        return await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "GetPositionInfo",
            {"InstanceID": 0},
        )

    async def play(self) -> None:
        await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "Play",
            {"InstanceID": 0, "Speed": "1"},
        )

    async def pause(self) -> None:
        await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "Pause", {"InstanceID": 0}
        )

    async def next(self) -> None:
        """AVTransport Next: the firmware's single button arbiter -- queue
        advance with wrap+shuffle, Bluetooth AVRCP / AirPlay AP2 reverse
        control, radio/AUX gating -- same path as the physical key. Both
        firmwares handle it (the Android SCPD just doesn't declare it);
        firmware that predates it answers a SOAP fault."""
        await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "Next", {"InstanceID": 0}
        )

    async def previous(self) -> None:
        await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "Previous", {"InstanceID": 0}
        )

    async def stop(self) -> None:
        await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "Stop", {"InstanceID": 0}
        )

    async def seek_rel_time(self, position_hms: str) -> None:
        """Seek within the current track. position_hms is 'H:MM:SS'."""
        await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "Seek",
            {"InstanceID": 0, "Unit": "REL_TIME", "Target": position_hms},
        )

    async def set_av_transport_uri(self, uri: str, metadata: str = "") -> None:
        await self._soap.call(
            AVTRANSPORT_CONTROL, ST_AVTRANSPORT, "SetAVTransportURI",
            {"InstanceID": 0, "CurrentURI": uri, "CurrentURIMetaData": metadata},
        )

    # -- Standard RenderingControl (embedded MediaRenderer) -----------------
    async def get_volume(self) -> int | None:
        """0..100, or None if this device hides its renderer (a slave 404s)."""
        try:
            r = await self._soap.call(
                RENDERING_CONTROL, ST_RENDERING, "GetVolume",
                {"InstanceID": 0, "Channel": "Master"},
            )
        except SoapError:
            return None
        try:
            return int(r.get("CurrentVolume", ""))
        except ValueError:
            return None

    async def set_volume(self, volume: int) -> None:
        volume = max(0, min(100, int(volume)))
        await self._soap.call(
            RENDERING_CONTROL, ST_RENDERING, "SetVolume",
            {"InstanceID": 0, "Channel": "Master", "DesiredVolume": volume},
        )

    async def get_mute(self) -> bool | None:
        try:
            r = await self._soap.call(
                RENDERING_CONTROL, ST_RENDERING, "GetMute",
                {"InstanceID": 0, "Channel": "Master"},
            )
        except SoapError:
            return None
        return r.get("CurrentMute", "0") == "1"

    async def set_mute(self, mute: bool) -> None:
        await self._soap.call(
            RENDERING_CONTROL, ST_RENDERING, "SetMute",
            {"InstanceID": 0, "Channel": "Master", "DesiredMute": 1 if mute else 0},
        )

    async def replace_queue(self, items_didl: str, starting_index: int = 0) -> None:
        """Replace the queue with a DIDL-Lite item list and auto-play index N.

        The firmware auto-starts playback at starting_index, so no separate Play
        is needed. items_didl may be fed back verbatim from GetSonglistTracks /
        GetCollected* / GetLocal* Result (they share the item format).
        """
        await self._soap.call(
            miyue_control("MiyueQueue"), ST_QUEUE, "ReplaceQueue",
            {"Items": items_didl, "StartingIndex": str(starting_index)},
        )

    # -- MiyueLibrary (browse; all login-free device-local SQLite reads) -----
    async def get_collected_songlists(self) -> list[dict]:
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetCollectedSonglists"
        )
        return _load_json_list(r.get("Songlists", ""))

    async def get_collected_boards(self) -> list[dict]:
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetCollectedBoards"
        )
        return _load_json_list(r.get("Boards", ""))

    async def get_local_folders(self) -> list[dict]:
        """List local folders [{name,path,count}] (live-verified action).

        NOTE: `path` is the directoryPath value used when saving a folder as an
        alarm/scene music source — NOT a browse drill-down key. Folder browsing
        goes through get_local_categories("folder") instead.
        """
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetLocalFolders"
        )
        return _load_json_list(r.get("Folders", ""))

    async def get_local_categories(self, group_by: str) -> list[dict]:
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetLocalCategories",
            {"GroupBy": group_by},
        )
        return _load_json_list(r.get("Categories", ""))

    async def get_songlist_tracks_didl(self, songlist_id: str) -> str:
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetSonglistTracks",
            {"SonglistId": songlist_id},
        )
        return r.get("Result", "")

    async def get_collected_music_didl(self) -> str:
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetCollectedMusic"
        )
        return r.get("Result", "")

    async def get_collected_radios_didl(self) -> str:
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetCollectedRadios"
        )
        return r.get("Result", "")

    async def get_local_songs_didl(
        self, start_index: int = 0, count: int = 500
    ) -> tuple[str, int]:
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetLocalSongs",
            {"StartIndex": str(start_index), "Count": str(count)},
        )
        return r.get("Result", ""), _to_int(r.get("Total", "0"))

    async def get_local_category_tracks_didl(
        self, group_by: str, key: str, start_index: int = 0, count: int = 500
    ) -> tuple[str, int]:
        r = await self._soap.call(
            miyue_control("MiyueLibrary"), ST_LIBRARY, "GetLocalCategoryTracks",
            {"GroupBy": group_by, "Key": key,
             "StartIndex": str(start_index), "Count": str(count)},
        )
        return r.get("Result", ""), _to_int(r.get("Total", "0"))

    async def get_queue_didl(
        self, start_index: int = 0, count: int = 500
    ) -> tuple[str, int, int]:
        """Return (raw DIDL, total, current_index) for the play queue."""
        r = await self._soap.call(
            miyue_control("MiyueQueue"), ST_QUEUE, "GetQueue",
            {"StartIndex": str(start_index), "RequestedCount": str(count)},
        )
        # default=-1 also catches an EMPTY <CurrentIndex/> (int("") fails);
        # 0 would sneak past _skip's idx<0 guard as "first track is current".
        return (r.get("Result", ""), _to_int(r.get("Total", "0")),
                _to_int(r.get("CurrentIndex", "-1"), -1))

    # -- MiyueAlarmClock ----------------------------------------------------
    async def list_alarms(self) -> list[dict]:
        r = await self._soap.call(
            miyue_control("MiyueAlarmClock"), ST_ALARM, "ListAlarms"
        )
        return _load_json_list(r.get("Alarms", ""))

    async def create_alarm(self, alarm: dict) -> str:
        r = await self._soap.call(
            miyue_control("MiyueAlarmClock"), ST_ALARM, "CreateAlarm",
            {"Json": json.dumps(alarm, ensure_ascii=False)},
        )
        return r.get("NewAlarmId", "")

    async def update_alarm(self, alarm: dict) -> None:
        await self._soap.call(
            miyue_control("MiyueAlarmClock"), ST_ALARM, "UpdateAlarm",
            {"Json": json.dumps(alarm, ensure_ascii=False)},
        )

    async def delete_alarm(self, alarm_id: str) -> None:
        await self._soap.call(
            miyue_control("MiyueAlarmClock"), ST_ALARM, "DeleteAlarm",
            {"AlarmId": str(alarm_id)},
        )

    async def enable_alarm(self, alarm_id: str, enabled: bool) -> None:
        await self._soap.call(
            miyue_control("MiyueAlarmClock"), ST_ALARM, "EnableAlarm",
            {"AlarmId": str(alarm_id), "Enabled": "1" if enabled else "0"},
        )

    # -- MiyueSensor (scenes / 情景) -----------------------------------------
    async def list_sensors(self) -> list[dict]:
        r = await self._soap.call(
            miyue_control("MiyueSensor"), ST_SENSOR, "ListSensors"
        )
        return _load_json_list(r.get("Sensors", ""))

    async def create_sensor(self, scene: dict) -> str:
        r = await self._soap.call(
            miyue_control("MiyueSensor"), ST_SENSOR, "CreateSensor",
            {"Json": json.dumps(scene, ensure_ascii=False)},
        )
        return r.get("NewSensorId", "")

    async def update_sensor(self, scene: dict) -> None:
        await self._soap.call(
            miyue_control("MiyueSensor"), ST_SENSOR, "UpdateSensor",
            {"Json": json.dumps(scene, ensure_ascii=False)},
        )

    async def execute_sensor(self, sensor_id: str) -> None:
        await self._soap.call(
            miyue_control("MiyueSensor"), ST_SENSOR, "ExecuteSensor",
            {"SensorId": str(sensor_id)},
        )

    async def delete_sensor(self, sensor_id: str) -> None:
        await self._soap.call(
            miyue_control("MiyueSensor"), ST_SENSOR, "DeleteSensor",
            {"SensorId": str(sensor_id)},
        )

    # -- MiyueSystem.PlayTTS ------------------------------------------------
    async def play_tts(self, text: str, volume: int) -> None:
        """Speak text on THIS device. Volume MUST be 1..100 -- on the Linux
        firmware an empty/0 volume mutes the speaker and is never restored."""
        vol = max(1, min(100, int(volume)))
        await self._soap.call(
            miyue_control("MiyueSystem"), ST_SYSTEM, "PlayTTS",
            {"Text": text, "Volume": str(vol)},
        )

    async def probe(self) -> bool:
        """Cheap liveness check used after a rebase. True if the device answers."""
        try:
            await self._soap.call(
                miyue_control("MiyueSystem"), ST_SYSTEM, "GetDeviceInfo"
            )
            return True
        except SoapError:
            return False


def _split_members(value: str) -> list[str]:
    """Member lists are delimiter-joined; tolerate comma / space / newline."""
    if not value:
        return []
    for sep in (",", "\n", " "):
        if sep in value:
            return [p.strip() for p in value.split(sep) if p.strip()]
    return [value.strip()]


def _load_json_list(value: str) -> list[dict]:
    """Parse a JSON-array out-arg (GetCollectedSonglists/ListAlarms/...)."""
    if not value or not value.strip():
        return []
    try:
        data = json.loads(value)
    except (ValueError, TypeError):
        return []
    return data if isinstance(data, list) else []


def _to_int(value: str, default: int = 0) -> int:
    try:
        return int(value)
    except (ValueError, TypeError):
        return default


__all__ = ["MiyueDevice", "GroupInfo", "DeviceState"]
