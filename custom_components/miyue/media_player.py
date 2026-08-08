"""MiYue speaker media_player entity."""

from __future__ import annotations

import asyncio
import logging
from urllib.parse import quote, unquote

from homeassistant.components.media_player import (
    BrowseMedia,
    MediaPlayerDeviceClass,
    MediaPlayerEntity,
    MediaPlayerEntityFeature,
    MediaPlayerState,
    MediaType,
    RepeatMode,
)
from homeassistant.core import HomeAssistant, ServiceCall
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import entity_platform
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from . import MiyueConfigEntry, MiyueRuntimeData
from . import services
from .const import (
    DOMAIN,
    MANUFACTURER,
    PLAYMODE_NORMAL,
    PLAYMODE_REPEAT_ALL,
    PLAYMODE_REPEAT_ONE,
    PLAYMODE_SHUFFLE,
    SOURCE_MUSIC,
    TRANSPORT_NO_MEDIA,
    TRANSPORT_PAUSED,
    TRANSPORT_PLAYING,
    TRANSPORT_STOPPED,
    TRANSPORT_TRANSITIONING,
)
from .const import ROLE_SLAVE
from .coordinator import MiyueCoordinator
from .device import MiyueDevice
from .didl import duration_to_seconds
from .soap import SoapError
from .song_src import (
    QUEUE_SONGSRCS,
    SENTINEL_SYNC_SLAVE,
    UNKNOWN,
    can_skip,
    effective_song_src,
)
from . import browse as browsing
from . import group as grouping

_LOGGER = logging.getLogger(__name__)

_STATE_MAP = {
    TRANSPORT_PLAYING: MediaPlayerState.PLAYING,
    TRANSPORT_PAUSED: MediaPlayerState.PAUSED,
    TRANSPORT_STOPPED: MediaPlayerState.IDLE,
    TRANSPORT_NO_MEDIA: MediaPlayerState.IDLE,
    TRANSPORT_TRANSITIONING: MediaPlayerState.BUFFERING,
}

_SUPPORTED = (
    MediaPlayerEntityFeature.PLAY
    | MediaPlayerEntityFeature.PAUSE
    | MediaPlayerEntityFeature.STOP
    | MediaPlayerEntityFeature.NEXT_TRACK
    | MediaPlayerEntityFeature.PREVIOUS_TRACK
    | MediaPlayerEntityFeature.SEEK
    | MediaPlayerEntityFeature.VOLUME_SET
    | MediaPlayerEntityFeature.VOLUME_MUTE
    | MediaPlayerEntityFeature.SELECT_SOURCE
    | MediaPlayerEntityFeature.SHUFFLE_SET
    | MediaPlayerEntityFeature.REPEAT_SET
    | MediaPlayerEntityFeature.GROUPING
    | MediaPlayerEntityFeature.PLAY_MEDIA
    | MediaPlayerEntityFeature.BROWSE_MEDIA
)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: MiyueConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the MiYue media_player from a config entry."""
    async_add_entities([MiyueMediaPlayer(entry.runtime_data)])

    platform = entity_platform.async_get_current_platform()
    platform.async_register_entity_service(
        services.SERVICE_PLAY_TTS, services.PLAY_TTS_SCHEMA, _svc_play_tts)


# Entity-service handlers receive (entity, ServiceCall) — version-stable form.
async def _svc_play_tts(entity: "MiyueMediaPlayer", call: ServiceCall) -> None:
    await entity.async_play_tts(
        call.data["message"], call.data.get("volume"),
        call.data["announce_group"])


class MiyueMediaPlayer(CoordinatorEntity[MiyueCoordinator], MediaPlayerEntity):
    """A MiYue multi-room speaker."""

    _attr_has_entity_name = True
    _attr_name = None  # entity carries the device's name
    _attr_device_class = MediaPlayerDeviceClass.SPEAKER

    def __init__(self, runtime: MiyueRuntimeData) -> None:
        super().__init__(runtime.coordinator)
        self._runtime = runtime
        self._device = runtime.device
        self._attr_unique_id = self._device.udn

    async def async_added_to_hass(self) -> None:
        await super().async_added_to_hass()
        # Publish our entity_id into the shared registry so grouping can map
        # UDN <-> entity_id across config entries.
        self._runtime.entity_id = self.entity_id

    # -- identity -----------------------------------------------------------
    @property
    def device_info(self) -> DeviceInfo:
        data = self.coordinator.data
        return DeviceInfo(
            identifiers={(DOMAIN, self._device.udn)},
            manufacturer=MANUFACTURER,
            name=data.name if data else None,
            model=data.model if data else None,
            sw_version=data.software_version if data else None,
            connections=set(),
        )

    @property
    def supported_features(self) -> MediaPlayerEntityFeature:
        """Gray out next/prev for sources that cannot skip (Flutter parity).

        Radio, DLNA cast-in and AUX/SPDIF are single streams the device will
        not skip within -- showing the buttons only teaches users they're
        broken. Queue sources, Bluetooth/AirPlay (device-side AVRCP/AP2
        reverse control) and a Linux sync slave (Next forwards to the master)
        keep them.
        """
        if self._skip_allowed():
            return _SUPPORTED
        return _SUPPORTED & ~(
            MediaPlayerEntityFeature.NEXT_TRACK
            | MediaPlayerEntityFeature.PREVIOUS_TRACK
        )

    def _skip_allowed(self) -> bool:
        data = self.coordinator.data
        idx = getattr(self.coordinator, "current_index", UNKNOWN)
        if data and data.group.role == ROLE_SLAVE:
            # A slave may skip only when the forwarding path provably exists.
            # The LINUX firmware keeps the slave's renderer up and forwards
            # Next/Previous to the master (whole-group skip) -- and it is the
            # firmware that emits the -105 sentinel. An ANDROID slave hides
            # its renderer (Next would 404) while GetQueue leaks a stale raw
            # index; a SeekToTrack there rips the device out of the group.
            return idx == SENTINEL_SYNC_SLAVE
        track = data.current_track if data else None
        src = effective_song_src(
            data.active_source if data else None,
            idx,
            track.song_src if track else None,
        )
        return can_skip(src)

    # -- transport state ----------------------------------------------------
    @property
    def state(self) -> MediaPlayerState:
        if not self.coordinator.last_update_success:
            return MediaPlayerState.OFF
        ts = getattr(self.coordinator, "transport_state", TRANSPORT_STOPPED)
        return _STATE_MAP.get(ts, MediaPlayerState.IDLE)

    # -- volume -------------------------------------------------------------
    @property
    def volume_level(self) -> float | None:
        vol = getattr(self.coordinator, "volume", None)
        return None if vol is None else vol / 100

    @property
    def is_volume_muted(self) -> bool | None:
        return getattr(self.coordinator, "muted", None)

    # -- now playing --------------------------------------------------------
    @property
    def media_title(self) -> str | None:
        track = self._track
        return track.title if track else None

    @property
    def media_artist(self) -> str | None:
        track = self._track
        return track.artist or None if track else None

    @property
    def media_album_name(self) -> str | None:
        track = self._track
        return track.album or None if track else None

    @property
    def media_content_type(self) -> MediaType | None:
        return MediaType.MUSIC if self._track else None

    @property
    def media_image_url(self) -> str | None:
        track = self._track
        return track.album_art or None if track else None

    @property
    def media_image_remotely_accessible(self) -> bool:
        """Public CDN art (netease/kugou) is remotely reachable; let the
        frontend fetch it directly with a browser UA -- that also sidesteps the
        netease CDN's dart:io/non-browser UA 403 filter. Device-hosted art
        (served from the speaker's own IP) is not, so HA must proxy that."""
        track = self._track
        art = track.album_art if track else ""
        if not art or not art.startswith("http"):
            return False
        return self._device.host not in art

    @property
    def media_duration(self) -> int | None:
        return duration_to_seconds(getattr(self.coordinator, "track_duration", ""))

    @property
    def media_position(self) -> int | None:
        return duration_to_seconds(getattr(self.coordinator, "rel_time", ""))

    @property
    def media_position_updated_at(self):
        return getattr(self.coordinator, "position_updated", None)

    # -- source -------------------------------------------------------------
    @property
    def source_list(self) -> list[str]:
        data = self.coordinator.data
        sources = [SOURCE_MUSIC]
        if data:
            sources += data.available_sources
        return sources

    @property
    def source(self) -> str | None:
        data = self.coordinator.data
        if data and data.active_source:
            return data.active_source
        return SOURCE_MUSIC

    # -- play mode ----------------------------------------------------------
    @property
    def shuffle(self) -> bool | None:
        data = self.coordinator.data
        return data.play_mode == PLAYMODE_SHUFFLE if data else None

    @property
    def repeat(self) -> RepeatMode | None:
        data = self.coordinator.data
        if not data:
            return None
        return {
            PLAYMODE_REPEAT_ONE: RepeatMode.ONE,
            PLAYMODE_REPEAT_ALL: RepeatMode.ALL,
        }.get(data.play_mode, RepeatMode.OFF)

    # -- grouping -----------------------------------------------------------
    @property
    def group_members(self) -> list[str] | None:
        registry = self._shared_registry()
        member_udns, _leader = grouping.cluster_members(registry, self._device.udn)
        if not member_udns:
            return None
        # Map UDNs -> entity_ids (leader first, order preserved).
        result = []
        for udn in member_udns:
            rt = registry.get(udn)
            if rt and rt.entity_id:
                result.append(rt.entity_id)
        return result or None

    async def async_join_players(self, group_members: list[str]) -> None:
        registry = self._shared_registry()
        slave_udns = [
            udn
            for udn in (self._entity_to_udn(eid, registry) for eid in group_members)
            if udn and udn != self._device.udn
        ]
        await grouping.async_join(registry, self._device.udn, slave_udns)
        await self.coordinator.async_request_refresh()

    async def async_unjoin_player(self) -> None:
        await grouping.async_unjoin(self._shared_registry(), self._device.udn)
        await self.coordinator.async_request_refresh()

    # -- commands -----------------------------------------------------------
    async def async_media_play(self) -> None:
        await self._device.play()
        await self.coordinator.async_request_refresh()

    async def async_media_pause(self) -> None:
        await self._device.pause()
        await self.coordinator.async_request_refresh()

    async def async_media_stop(self) -> None:
        await self._device.stop()
        await self.coordinator.async_request_refresh()

    async def async_media_next_track(self) -> None:
        await self._skip(forward=True)

    async def async_media_previous_track(self) -> None:
        await self._skip(forward=False)

    async def _skip(self, *, forward: bool) -> None:
        """Skip via AVTransport Next/Previous, like the Flutter controller.

        The firmware's button arbiter owns wrap, shuffle order and the
        Bluetooth/AirPlay reverse control -- client-side index math cannot
        reproduce that. Firmware predating Next/Previous answers a SOAP
        fault; emulate a plain queue step there (no wrap) from a FRESH
        GetQueue index -- the polled one may be a cycle stale.
        """
        if not self._skip_allowed():
            return  # non-skippable source / unsafe slave: mirror the gray-out
        dev = self._device
        try:
            await (dev.next() if forward else dev.previous())
        except SoapError as err:
            # Only a genuine SOAP fault proves "this firmware has no Next".
            # On a timeout/transport loss the action may HAVE executed (a
            # blind SeekToTrack would double-skip), and a hidden renderer
            # 404s with no fault body -- surface those instead of guessing.
            if err.fault_code is None:
                # Clean frontend toast + single-line log instead of
                # unknown_error with a full stack.
                raise HomeAssistantError(str(err)) from err
            _LOGGER.debug(
                "%s AVTransport %s unsupported (%s); queue-step fallback",
                dev.udn, "Next" if forward else "Previous", err,
            )
            _didl, total, idx = await dev.get_queue_didl(0, 1)
            if idx < 0 or total <= 0:
                return  # queue is not what's sounding; nothing sane to do
            data = self.coordinator.data
            track = data.current_track if data else None
            src = track.song_src if track else None
            if src is not None and src not in QUEUE_SONGSRCS:
                # An external single-stream is sounding (its queue index can
                # be a stale leftover): client-side emulation would hijack
                # it. The Flutter fallback stays silent here too.
                return
            target = idx + (1 if forward else -1)
            if not 0 <= target < total:
                return
            await dev.seek_to_track(target)
        await self.coordinator.async_request_refresh()

    async def async_media_seek(self, position: float) -> None:
        await self._device.seek_rel_time(_hms(int(position)))
        await self.coordinator.async_request_refresh()

    async def async_set_volume_level(self, volume: float) -> None:
        await self._device.set_volume(round(volume * 100))
        await self.coordinator.async_request_refresh()

    async def async_mute_volume(self, mute: bool) -> None:
        await self._device.set_mute(mute)
        await self.coordinator.async_request_refresh()

    async def async_select_source(self, source: str) -> None:
        await self._device.select_source(source)
        await self.coordinator.async_request_refresh()

    async def async_set_shuffle(self, shuffle: bool) -> None:
        await self._device.set_play_mode(
            PLAYMODE_SHUFFLE if shuffle else PLAYMODE_NORMAL
        )
        await self.coordinator.async_request_refresh()

    async def async_set_repeat(self, repeat: RepeatMode) -> None:
        mode = {
            RepeatMode.ONE: PLAYMODE_REPEAT_ONE,
            RepeatMode.ALL: PLAYMODE_REPEAT_ALL,
        }.get(repeat, PLAYMODE_NORMAL)
        await self._device.set_play_mode(mode)
        await self.coordinator.async_request_refresh()

    async def async_browse_media(
        self, media_content_type=None, media_content_id=None
    ) -> BrowseMedia:
        tree = await browsing.async_browse(self.hass, self._device, media_content_id)
        self._proxy_device_thumbnails(tree)
        return tree

    def _proxy_device_thumbnails(self, node: BrowseMedia) -> None:
        """Route device-hosted art (http://<device-ip>:18181/...) through HA.

        Off-LAN frontends (mobile app via cloud) can't reach the speaker's IP
        directly; public CDN art is left untouched.
        """
        prefix = f"http://{self._device.host}:"
        if node.thumbnail and node.thumbnail.startswith(prefix):
            node.thumbnail = self.get_browse_image_url(
                node.media_content_type,
                node.media_content_id,
                media_image_id=quote(node.thumbnail, safe=""),
            )
        for child in node.children or []:
            self._proxy_device_thumbnails(child)

    async def async_get_browse_image(
        self,
        media_content_type: str,
        media_content_id: str,
        media_image_id: str | None = None,
    ) -> tuple[bytes | None, str | None]:
        if not media_image_id:
            return None, None
        url = unquote(media_image_id)
        # Only ever proxy this device's own art server — never arbitrary URLs.
        if not url.startswith(f"http://{self._device.host}:"):
            return None, None
        return await self._async_fetch_image(url)

    async def async_play_media(
        self, media_type: str, media_id: str, **kwargs
    ) -> None:
        try:
            if media_id.startswith(("http://", "https://")):
                # Cast an arbitrary stream URL via the standard DLNA path.
                # A group SLAVE is not a valid cast target: it renders the
                # master's multicast stream, so "play this here" can only mean
                # the whole group -- cast to the leader instead. The firmware
                # agrees from the other side: it now rejects an untagged
                # SetAVTransportURI on a slave (SOAP 705) precisely so a stray
                # DLNA push can't rip one speaker out of the group.
                dev = self._cast_target()
                await dev.set_av_transport_uri(media_id)
                await dev.play()
            else:
                # A browsed device-local item (songlist/track/local/queue/NAS).
                await browsing.async_play(self.hass, self._device, media_id)
        except SoapError as err:
            # Clean toast instead of unknown_error + a full stack.
            raise HomeAssistantError(str(err)) from err
        await self.coordinator.async_request_refresh()

    def _cast_target(self) -> MiyueDevice:
        """This speaker, or its group leader when we are a slave."""
        data = self.coordinator.data
        if not data or data.group.role != ROLE_SLAVE:
            return self._device
        registry = self._shared_registry()
        _members, leader = grouping.cluster_members(registry, self._device.udn)
        leader_rt = registry.get(leader) if leader else None
        # No leader resolved (mid-regroup, or the master isn't a config entry):
        # fall back to ourselves rather than silently dropping the request --
        # the firmware's 705 then surfaces as a readable error.
        return leader_rt.device if leader_rt else self._device

    # -- services -----------------------------------------------------------
    async def async_play_tts(
        self, message: str, volume: int | None, announce_group: bool
    ) -> None:
        """Speak on this speaker; optionally fan out to its group members.

        There is no group-announce action in the firmware, and a slave does not
        hear the master's TTS, so a room announcement must hit every member.
        volume=None -> each speaker uses its own current volume (the Linux
        firmware never restores volume after TTS, so matching current avoids
        permanently changing the room level).
        """
        registry = self._shared_registry()
        runtimes = [self._runtime]
        if announce_group:
            members, _ = grouping.cluster_members(registry, self._device.udn)
            for udn in members:
                rt = registry.get(udn)
                if rt is not None:
                    runtimes.append(rt)
        seen: set[str] = set()
        calls = []
        for rt in runtimes:
            if rt.device.udn in seen:
                continue
            seen.add(rt.device.udn)
            vol = volume
            if vol is None:
                current = getattr(rt.coordinator, "volume", None)
                vol = current if isinstance(current, int) and current >= 1 else 30
            calls.append(rt.device.play_tts(message, vol))
        await asyncio.gather(*calls, return_exceptions=True)

    # -- helpers ------------------------------------------------------------
    @property
    def _track(self):
        data = self.coordinator.data
        return data.current_track if data else None

    def _shared_registry(self) -> dict:
        return self.hass.data[DOMAIN].registry

    @staticmethod
    def _entity_to_udn(entity_id: str, registry: dict) -> str | None:
        for udn, rt in registry.items():
            if rt.entity_id == entity_id:
                return udn
        return None


def _hms(seconds: int) -> str:
    seconds = max(0, seconds)
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}"
