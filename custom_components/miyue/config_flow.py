"""Config flow for MiYue Multi-Room Speakers."""

from __future__ import annotations

import asyncio
import logging
from typing import Any
from urllib.parse import urlparse

import aiohttp
import voluptuous as vol

from homeassistant.config_entries import ConfigFlow, ConfigFlowResult
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.service_info.ssdp import (
    ATTR_UPNP_FRIENDLY_NAME,
    ATTR_UPNP_MANUFACTURER,
    ATTR_UPNP_UDN,
    SsdpServiceInfo,
)

from .const import (
    CONF_HOST,
    CONF_LOCATION,
    CONF_NAME,
    CONF_PORT,
    CONF_UDN,
    DOMAIN,
    MR_UDN_SUFFIX,
    ROOT_DEVICE_TYPE,
)
from .device import MiyueDevice

_LOGGER = logging.getLogger(__name__)


def _is_miyue(info: SsdpServiceInfo) -> bool:
    """Confirm a discovery is a MiYue speaker, not some other MultiRoomMusicPlayer.

    The manifest matches broadly (deviceType / MiyueGroup ST); verify here by
    the manufacturer string or the presence of a private urn:miyue-hk service.
    """
    manufacturer = str(info.upnp.get(ATTR_UPNP_MANUFACTURER, ""))
    if "MiYue" in manufacturer or "miyue" in manufacturer:
        return True
    if info.ssdp_st and "miyue-hk" in info.ssdp_st:
        return True
    services = info.upnp.get("serviceList") or {}
    return "miyue-hk" in str(services)


class MiyueConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle a config flow for MiYue speakers."""

    VERSION = 1

    def __init__(self) -> None:
        self._udn: str | None = None
        self._location: str | None = None
        self._name: str = "MiYue Speaker"

    async def async_step_ssdp(
        self, discovery_info: SsdpServiceInfo
    ) -> ConfigFlowResult:
        """Handle a device discovered over SSDP."""
        if not _is_miyue(discovery_info):
            return self.async_abort(reason="not_miyue")

        udn = discovery_info.ssdp_udn or discovery_info.upnp.get(ATTR_UPNP_UDN)
        location = discovery_info.ssdp_location
        if not udn or not location:
            return self.async_abort(reason="no_udn")
        # SSDP also announces the embedded MediaRenderer, whose USN carries the
        # root UDN + "-mr". Normalize so one physical speaker = one identity.
        if udn.endswith(MR_UDN_SUFFIX):
            udn = udn[: -len(MR_UDN_SUFFIX)]

        self._udn = udn
        self._location = location
        self._name = str(
            discovery_info.upnp.get(ATTR_UPNP_FRIENDLY_NAME, self._name)
        )

        host, port = _split_location(location)
        # Legacy entries created before UDN normalization may carry the "-mr"
        # identity: keep them working (update their location) and never offer
        # the same speaker twice.
        for entry in self._async_current_entries():
            if entry.unique_id == udn + MR_UDN_SUFFIX:
                self.hass.config_entries.async_update_entry(
                    entry,
                    data={**entry.data, CONF_HOST: host, CONF_PORT: port,
                          CONF_LOCATION: location},
                )
                return self.async_abort(reason="already_configured")

        await self.async_set_unique_id(udn)
        # Rewrite the stored host:port if this UDN already exists at a new
        # location (device rebooted onto a different pupnp port).
        self._abort_if_unique_id_configured(
            updates={CONF_HOST: host, CONF_PORT: port, CONF_LOCATION: location},
            reload_on_update=False,
        )

        self.context["title_placeholders"] = {"name": self._name}
        return await self.async_step_confirm()

    async def async_step_confirm(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Confirm adding a discovered device."""
        assert self._udn and self._location
        if user_input is not None:
            host, port = _split_location(self._location)
            return self.async_create_entry(
                title=self._name,
                data={
                    CONF_UDN: self._udn,
                    CONF_HOST: host,
                    CONF_PORT: port,
                    CONF_LOCATION: self._location,
                    CONF_NAME: self._name,
                },
            )
        return self.async_show_form(
            step_id="confirm",
            description_placeholders={"name": self._name},
        )

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Manual setup by host — probe it, read its UDN, converge on identity."""
        errors: dict[str, str] = {}
        if user_input is not None:
            host = user_input[CONF_HOST]
            port = int(user_input.get(CONF_PORT) or 0)
            location, udn, name = await self._probe_manual(host, port)
            if udn is None:
                errors["base"] = "cannot_connect"
            else:
                await self.async_set_unique_id(udn)
                p_host, p_port = _split_location(location)
                self._abort_if_unique_id_configured(
                    updates={CONF_HOST: p_host, CONF_PORT: p_port,
                             CONF_LOCATION: location}
                )
                return self.async_create_entry(
                    title=name,
                    data={
                        CONF_UDN: udn,
                        CONF_HOST: p_host,
                        CONF_PORT: p_port,
                        CONF_LOCATION: location,
                        CONF_NAME: name,
                    },
                )

        return self.async_show_form(
            step_id="user",
            data_schema=vol.Schema(
                {
                    vol.Required(CONF_HOST): str,
                    vol.Optional(CONF_PORT): int,
                }
            ),
            errors=errors,
        )

    async def _probe_manual(
        self, host: str, port: int
    ) -> tuple[str | None, str | None, str]:
        """Find the device's description.xml and read its UDN + name.

        The port drifts, so if the caller didn't give one, sweep the known
        pupnp range (49495-49520). Probes run CONCURRENTLY with a short
        timeout so an unreachable/firewalled host fails in ~3s, not ~100s.
        """
        session = async_get_clientsession(self.hass)
        candidates = [port] if port else list(range(49495, 49521))

        async def _try(cand: int):
            location = f"http://{host}:{cand}/description.xml"
            try:
                async with session.get(
                    location, timeout=aiohttp.ClientTimeout(total=3)
                ) as resp:
                    if resp.status != 200:
                        return None
                    body = await resp.text()
            except Exception:  # noqa: BLE001
                return None
            if "urn:miyue-hk" not in body and ROOT_DEVICE_TYPE not in body:
                return None
            udn = _extract(body, "UDN")
            if not udn:
                return None
            name = _extract(body, "friendlyName") or "MiYue Speaker"
            return location, udn, name

        results = await asyncio.gather(*(_try(c) for c in candidates))
        for hit in results:
            if hit is not None:
                return hit
        return None, None, "MiYue Speaker"


def _split_location(location: str) -> tuple[str, int]:
    parsed = urlparse(location)
    return parsed.hostname or "", parsed.port or 49495


def _extract(xml: str, tag: str) -> str:
    import re

    m = re.search(rf"<{tag}>([^<]+)</{tag}>", xml)
    return m.group(1).strip() if m else ""
