"""Polling coordinator for one MiYue speaker.

v1 is pure verified SOAP over a polling DataUpdateCoordinator (iot_class
local_polling). Group topology uses the mirror model: every device is polled
for GetGroupInfo and the media_player entity clusters by GroupID across the
shared registry (see MiyueData). GENA push (-> local_push) is a later phase.
"""

from __future__ import annotations

import asyncio
import logging

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed
from homeassistant.util import dt as dt_util

from .const import (
    CONF_HOST,
    CONF_LOCATION,
    CONF_PORT,
    DOMAIN,
    MR_UDN_SUFFIX,
    POLL_INTERVAL,
    TRANSPORT_PLAYING,
)
from .device import DeviceState, GroupInfo, MiyueDevice
from .didl import parse_didl
from .soap import SoapError

_LOGGER = logging.getLogger(__name__)


class MiyueCoordinator(DataUpdateCoordinator[DeviceState]):
    """Polls one device and publishes a DeviceState snapshot."""

    def __init__(
        self,
        hass: HomeAssistant,
        entry: ConfigEntry,
        device: MiyueDevice,
    ) -> None:
        super().__init__(
            hass,
            _LOGGER,
            name=f"{DOMAIN} {device.udn}",
            update_interval=_interval(POLL_INTERVAL),
            config_entry=entry,
        )
        self.device = device
        self._static: dict[str, str] | None = None  # cached GetDeviceInfo
        self._fail_count = 0  # consecutive both-anchors-down poll failures
        self._reported_failures: set[str] = set()  # per-call warn-once state

    async def _async_update_data(self) -> DeviceState:
        dev = self.device
        # Fan the reads out concurrently and tolerate per-call failures. A group
        # SLAVE hides its embedded MediaRenderer, so AVTransport/RenderingControl
        # 404 for it -- those must degrade to defaults, not knock the whole
        # device (and hence the slave's media_player) offline.
        results = await asyncio.gather(
            self._get_static(),
            dev.get_transport_info(),
            dev.get_position_info(),
            dev.get_queue_state(),
            dev.get_group_info(),
            dev.get_external_inputs(),
            dev.get_play_mode(),
            dev.get_volume(),
            dev.get_mute(),
            return_exceptions=True,
        )
        static, transport, position, queue, group, inputs, play_mode, \
            volume, mute = results

        # Liveness anchor: the private services (device info / group) are served
        # by every device incl. slaves. If BOTH are down, the device is really
        # unreachable (rebooting / port drifted) -> unavailable, and the SSDP
        # rediscovery callback will rebase us.
        if isinstance(static, Exception) and isinstance(group, Exception):
            self._fail_count += 1
            # Active self-recovery for 24/7 LAN operation: SSDP alive may never
            # arrive on multicast-hostile routers, so after ~3 failed cycles
            # (and periodically after) hunt the device down ourselves.
            if self._fail_count >= 3 and self._fail_count % 3 == 0:
                if await self._async_relocate():
                    raise UpdateFailed(
                        f"{dev.udn} relocated to {dev.host}:{dev.port}; retrying"
                    )
            raise UpdateFailed(f"{dev.udn} poll failed: {static}")
        self._fail_count = 0

        # A degraded sub-call must be VISIBLE: the year of GetTimeline 401-ing
        # every 3 s with zero log trace is why this exists. Warn once per call
        # name, then keep quiet (a group slave 404s AVTransport by design).
        for name, result in zip(
            ("GetDeviceInfo", "GetTransportInfo", "GetPositionInfo",
             "GetQueue", "GetGroupInfo", "GetExternalInputs", "GetPlayMode",
             "GetVolume", "GetMute"),
            results,
        ):
            if isinstance(result, Exception):
                self._log_degraded(name, result)

        static = {} if isinstance(static, Exception) else static
        transport = {} if isinstance(transport, Exception) else transport
        position = {} if isinstance(position, Exception) else position
        if isinstance(queue, Exception):
            # A lone GetQueue failure must not flip the skip gating: -1 also
            # means the legit "queue idle", and the Linux-slave gate
            # (idx == -105) would drop NEXT/PREV from supported_features for
            # a cycle and churn the entity registry. Keep last poll's values.
            queue = ([], getattr(self, "current_index", -1),
                     getattr(self, "track_total", 0), None)
        group = GroupInfo() if isinstance(group, Exception) else group
        inputs = {} if isinstance(inputs, Exception) else inputs
        play_mode = "NORMAL" if isinstance(play_mode, Exception) else play_mode
        volume = None if isinstance(volume, Exception) else volume
        mute = None if isinstance(mute, Exception) else mute

        tracks, current_index, queue_total, current = queue
        # AVTransport's TrackMetaData is authoritative for the track that is
        # ACTUALLY sounding: radio/BT/AirPlay single-streams never enter the
        # queue (and some firmware builds 500 GetTimeline entirely in that
        # state), while an idle device leaves it empty — hence the fallback
        # order: TrackMetaData first, queue timeline second.
        meta_tracks = parse_didl(position.get("TrackMetaData", "") or "")
        if meta_tracks:
            current = meta_tracks[0]

        state = DeviceState(
            name=static.get("Name", "") or group.device_name,
            ip_address=static.get("IPAddress", dev.host),
            software_version=static.get("SoftwareVersion", ""),
            model=static.get("Model", ""),
            group=group,
            current_track=current,
            play_mode=play_mode,
            available_sources=MiyueDevice.parse_available_sources(inputs),
            active_source=MiyueDevice.parse_active_source(inputs),
        )
        # Values outside DeviceState's dataclass shape are stashed on the
        # coordinator for the entity to read without re-querying.
        self.volume = volume
        self.muted = mute
        self.transport_state = transport.get("CurrentTransportState", "STOPPED")
        self.current_index = current_index
        self.track_total = queue_total
        self.rel_time = position.get("RelTime", "")
        self.track_duration = position.get("TrackDuration", "")
        # Freeze the sample time so the entity lets HA extrapolate the progress
        # bar between polls instead of it re-snapping every cycle.
        self.position_updated = dt_util.utcnow()
        return state

    def _log_degraded(self, name: str, err: Exception) -> None:
        if name in self._reported_failures:
            _LOGGER.debug("%s %s degraded: %s", self.device.udn, name, err)
            return
        self._reported_failures.add(name)
        _LOGGER.warning(
            "miyue %s: poll call %s failed, degrading to defaults "
            "(repeats logged at debug): %s",
            self.device.udn, name, err,
        )

    async def _async_relocate(self) -> bool:
        """Find a device that stopped answering (rebooted onto a new port/IP).

        Two hunting grounds, cheapest first:
        1. HA's SSDP cache — covers IP *and* port changes, if any announce got
           through since the reboot.
        2. A concurrent sweep of the pupnp port range on the last-known host —
           covers the common case (port drift, same IP) with zero multicast.
        On a hit: rebase the device and persist the new location so an HA
        restart reconnects to the right place.
        """
        dev = self.device
        udn_bare = dev.udn
        if udn_bare.endswith(MR_UDN_SUFFIX):
            udn_bare = udn_bare[: -len(MR_UDN_SUFFIX)]

        # 1) SSDP cache (both identity forms).
        try:
            from homeassistant.components import ssdp

            infos = list(await ssdp.async_get_discovery_info_by_udn(
                self.hass, udn_bare))
            infos += await ssdp.async_get_discovery_info_by_udn(
                self.hass, udn_bare + MR_UDN_SUFFIX)
            for info in infos:
                location = getattr(info, "ssdp_location", None)
                hit = await self._try_location(location, udn_bare)
                if hit:
                    return True
        except Exception:  # noqa: BLE001 - cache lookup must never break polls
            pass

        # 2) Port sweep on the last-known host.
        session = async_get_clientsession(self.hass)

        async def _probe(port: int) -> int | None:
            url = f"http://{dev.host}:{port}/description.xml"
            try:
                async with session.get(
                    url, timeout=aiohttp_timeout(2)
                ) as resp:
                    if resp.status != 200:
                        return None
                    body = await resp.text(errors="replace")
            except Exception:  # noqa: BLE001
                return None
            return port if udn_bare in body else None

        results = await asyncio.gather(
            *(_probe(p) for p in range(49495, 49521))
        )
        for port in results:
            if port is not None and port != dev.port:
                self._rebase_and_persist(dev.host, port)
                return True
        return False

    async def _try_location(self, location: str | None, udn_bare: str) -> bool:
        if not location:
            return False
        from urllib.parse import urlparse

        parsed = urlparse(location)
        host, port = parsed.hostname, parsed.port
        if not host or not port or (host, port) == (self.device.host,
                                                    self.device.port):
            return False
        session = async_get_clientsession(self.hass)
        try:
            async with session.get(location, timeout=aiohttp_timeout(3)) as resp:
                if resp.status != 200:
                    return False
                body = await resp.text(errors="replace")
        except Exception:  # noqa: BLE001
            return False
        if udn_bare not in body:
            return False
        self._rebase_and_persist(host, port)
        return True

    def _rebase_and_persist(self, host: str, port: int) -> None:
        dev = self.device
        _LOGGER.info("miyue %s self-recovered to %s:%s", dev.udn, host, port)
        dev.rebase(host, port)
        self.invalidate_static()
        self._fail_count = 0
        entry = self.config_entry
        if entry is not None:
            self.hass.config_entries.async_update_entry(
                entry,
                data={**entry.data, CONF_HOST: host, CONF_PORT: port,
                      CONF_LOCATION: f"http://{host}:{port}/description.xml"},
            )

    async def _get_static(self) -> dict[str, str]:
        """GetDeviceInfo changes rarely; fetch once, refresh lazily on miss."""
        if self._static is None:
            self._static = await self.device.get_device_info()
        return self._static

    def invalidate_static(self) -> None:
        self._static = None

    @property
    def is_playing(self) -> bool:
        return getattr(self, "transport_state", "STOPPED") == TRANSPORT_PLAYING


class MiyueAuxCoordinator(DataUpdateCoordinator[dict]):
    """Polls the rarely-changing scene + alarm lists (no GENA push for these)."""

    def __init__(self, hass: HomeAssistant, entry: ConfigEntry, device) -> None:
        super().__init__(
            hass,
            _LOGGER,
            name=f"{DOMAIN} aux {device.udn}",
            update_interval=_interval(30.0),
            config_entry=entry,
        )
        self.device = device

    async def _async_update_data(self) -> dict:
        # Tolerate one half failing (old firmware may lack a service -> SOAP
        # 401): keep the previous data for that half instead of wiping it —
        # an empty list would otherwise cascade into entity removal.
        sensors: list | None = None
        alarms: list | None = None
        try:
            sensors = await self.device.list_sensors()
        except SoapError as err:
            _LOGGER.debug("%s ListSensors failed: %s", self.device.udn, err)
        try:
            alarms = await self.device.list_alarms()
        except SoapError as err:
            _LOGGER.debug("%s ListAlarms failed: %s", self.device.udn, err)
        if sensors is None and alarms is None:
            raise UpdateFailed(f"{self.device.udn} aux poll failed")
        prev = self.data or {}
        return {
            "sensors": sensors if sensors is not None else prev.get("sensors", []),
            "alarms": alarms if alarms is not None else prev.get("alarms", []),
            # Flags so platforms only prune entities on authoritative reads.
            "sensors_fresh": sensors is not None,
            "alarms_fresh": alarms is not None,
        }


def _interval(seconds: float):
    from datetime import timedelta

    return timedelta(seconds=seconds)


def aiohttp_timeout(total: float):
    import aiohttp

    return aiohttp.ClientTimeout(total=total)
