"""The MiYue Multi-Room Speakers integration."""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from urllib.parse import urlparse

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import Platform
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .const import CONF_HOST, CONF_LOCATION, CONF_PORT, CONF_UDN, DOMAIN
from .coordinator import MiyueAuxCoordinator, MiyueCoordinator
from .device import MiyueDevice

_LOGGER = logging.getLogger(__name__)

PLATFORMS = [Platform.MEDIA_PLAYER, Platform.SCENE, Platform.SWITCH]


@dataclass
class MiyueRuntimeData:
    """Per-entry runtime, also registered in the shared registry for grouping."""

    device: MiyueDevice
    coordinator: MiyueCoordinator
    aux_coordinator: MiyueAuxCoordinator
    entity_id: str | None = None  # filled by the media_player entity
    unsub_ssdp: list = field(default_factory=list)


@dataclass
class MiyueData:
    """Shared across all entries so grouping can cluster by GroupID."""

    registry: dict[str, MiyueRuntimeData] = field(default_factory=dict)


# HA's ConfigEntry is generic in its runtime_data type (ConfigEntry[_DataT]).
MiyueConfigEntry = ConfigEntry[MiyueRuntimeData]


def _get_shared(hass: HomeAssistant) -> MiyueData:
    return hass.data.setdefault(DOMAIN, MiyueData())


async def async_setup_entry(hass: HomeAssistant, entry: MiyueConfigEntry) -> bool:
    """Set up MiYue from a config entry."""
    session = async_get_clientsession(hass)
    udn = entry.data[CONF_UDN]
    host = entry.data[CONF_HOST]
    port = entry.data[CONF_PORT]

    device = MiyueDevice(session, udn, host, port)
    coordinator = MiyueCoordinator(hass, entry, device)
    aux_coordinator = MiyueAuxCoordinator(hass, entry, device)
    await coordinator.async_config_entry_first_refresh()
    # Aux (scenes/alarms) is non-critical: don't fail setup if it can't load.
    await aux_coordinator.async_refresh()

    runtime = MiyueRuntimeData(
        device=device, coordinator=coordinator, aux_coordinator=aux_coordinator
    )
    entry.runtime_data = runtime
    _get_shared(hass).registry[udn] = runtime

    await _register_ssdp_rediscovery(hass, entry, runtime)

    _drop_legacy_scene_buttons(hass, entry)
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    return True


@callback
def _drop_legacy_scene_buttons(hass: HomeAssistant, entry: MiyueConfigEntry) -> None:
    """Delete the button.* entities scenes used to be exposed as.

    Scenes are `scene.*` entities now. Nothing re-creates the old button rows,
    so without this they linger in the registry forever as unavailable and
    every scene appears twice in the pickers.
    """
    registry = er.async_get(hass)
    for ent in er.async_entries_for_config_entry(registry, entry.entry_id):
        if ent.domain == "button" and "_scene_" in (ent.unique_id or ""):
            registry.async_remove(ent.entity_id)


async def async_unload_entry(hass: HomeAssistant, entry: MiyueConfigEntry) -> bool:
    """Unload a config entry."""
    unloaded = await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
    if unloaded:
        runtime = entry.runtime_data
        for unsub in runtime.unsub_ssdp:
            unsub()
        _get_shared(hass).registry.pop(entry.data[CONF_UDN], None)
    return unloaded


async def _register_ssdp_rediscovery(
    hass: HomeAssistant, entry: MiyueConfigEntry, runtime: MiyueRuntimeData
) -> None:
    """Re-locate the device when it reboots onto a new HTTP port.

    Devices reboot often (RTC->2000->NTP jump) and pupnp re-picks its port
    (49495 climbs to 4950x). Identity is the UDN; the volatile host:port is
    rewritten in place whenever SSDP sees this UDN at a new location.
    """
    from homeassistant.components import ssdp

    udn = entry.data[CONF_UDN]

    async def _on_ssdp(info, change) -> None:
        # Belt-and-suspenders: the match_dict below already filters by UDN,
        # but never rebase onto another device's location.
        if getattr(info, "ssdp_udn", None) != udn:
            return
        location = getattr(info, "ssdp_location", None)
        if not location:
            return
        parsed = urlparse(location)
        new_host, new_port = parsed.hostname, parsed.port
        if not new_host or not new_port:
            return
        if (new_host, new_port) == (runtime.device.host, runtime.device.port):
            return
        _LOGGER.info(
            "MiYue %s moved to %s:%s (SSDP %s) -- rebasing",
            udn, new_host, new_port, getattr(change, "name", change),
        )
        runtime.device.rebase(new_host, new_port)
        runtime.coordinator.invalidate_static()
        # Persist the new location so a restart reconnects to the right port.
        hass.config_entries.async_update_entry(
            entry,
            data={**entry.data, CONF_HOST: new_host, CONF_PORT: new_port,
                  CONF_LOCATION: location},
        )
        await runtime.coordinator.async_request_refresh()

    # match_dict keys are matched against the RAW combined SSDP headers, not
    # SsdpServiceInfo attribute names. The UDN lives in the synthesized header
    # "_udn" (derived from USN) — "ssdp_udn" would never match anything.
    unsub = await ssdp.async_register_callback(
        hass, _on_ssdp, match_dict={"_udn": udn}
    )
    runtime.unsub_ssdp.append(unsub)
