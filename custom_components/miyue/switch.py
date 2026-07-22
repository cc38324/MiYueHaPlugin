"""Alarm (闹钟 / MiyueAlarmClock) switches: on/off = EnableAlarm."""

from __future__ import annotations

from homeassistant.components.switch import SwitchEntity
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from . import MiyueConfigEntry, MiyueRuntimeData
from .const import DOMAIN, MANUFACTURER
from .coordinator import MiyueAuxCoordinator


async def async_setup_entry(
    hass: HomeAssistant,
    entry: MiyueConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """One switch per alarm; add new ones and prune deleted ones."""
    runtime = entry.runtime_data
    coordinator = runtime.aux_coordinator
    known: dict[str, MiyueAlarmSwitch] = {}

    @callback
    def _sync() -> None:
        data = coordinator.data or {}
        if not data.get("alarms_fresh", True):
            return  # stale carry-over — never add/prune on it
        current = {
            str(a.get("id")): a
            for a in data.get("alarms", [])
            if str(a.get("id", ""))
        }
        registry = er.async_get(hass)
        for aid in [a for a in known if a not in current]:
            ent = known.pop(aid)
            if ent.entity_id and registry.async_get(ent.entity_id):
                registry.async_remove(ent.entity_id)
        new = []
        for aid in current:
            if aid not in known:
                ent = MiyueAlarmSwitch(runtime, aid)
                known[aid] = ent
                new.append(ent)
        if new:
            async_add_entities(new)

    _sync()
    entry.async_on_unload(coordinator.async_add_listener(_sync))


class MiyueAlarmSwitch(CoordinatorEntity[MiyueAuxCoordinator], SwitchEntity):
    """Enable/disable a device alarm."""

    _attr_has_entity_name = True
    _attr_translation_key = "alarm"

    def __init__(self, runtime: MiyueRuntimeData, alarm_id: str) -> None:
        super().__init__(runtime.aux_coordinator)
        self._runtime = runtime
        self._device = runtime.device
        self._alarm_id = alarm_id
        self._attr_unique_id = f"{self._device.udn}_alarm_{alarm_id}"
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, self._device.udn)},
            manufacturer=MANUFACTURER,
        )

    @property
    def _alarm(self) -> dict | None:
        for alarm in (self.coordinator.data or {}).get("alarms", []):
            if str(alarm.get("id", "")) == self._alarm_id:
                return alarm
        return None

    @property
    def available(self) -> bool:
        return super().available and self._alarm is not None

    @property
    def name(self) -> str | None:
        alarm = self._alarm or {}
        time = alarm.get("time", "")
        # Include the id so two alarms at the same time stay distinguishable.
        return f"闹钟 {time} #{self._alarm_id}".strip()

    @property
    def is_on(self) -> bool | None:
        alarm = self._alarm
        if alarm is None:
            return None
        return alarm.get("enabled") in (1, "1")

    @property
    def extra_state_attributes(self) -> dict:
        alarm = self._alarm or {}
        attrs = {"alarm_id": self._alarm_id}
        for key in ("time", "recurrence", "volume", "duration", "songlistInfoId",
                    "musicInfoId", "directoryPath", "ttsText", "ringName"):
            if key in alarm:
                attrs[key] = alarm[key]
        return attrs

    async def async_turn_on(self, **kwargs) -> None:
        await self._device.enable_alarm(self._alarm_id, True)
        await self.coordinator.async_request_refresh()

    async def async_turn_off(self, **kwargs) -> None:
        await self._device.enable_alarm(self._alarm_id, False)
        await self.coordinator.async_request_refresh()
