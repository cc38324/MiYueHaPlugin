"""Device scenes (情景 / MiyueSensor) as HA scene entities: activate = ExecuteSensor.

Scenes are authored ON THE SPEAKER (app / KNX panel) — HA only reads the list
and fires them. That is why these are `scene` entities and not `button` ones:
an integration-provided scene is activate-only (HA's scene editor only edits
its own `homeassistant`-platform scenes), so the automation editor offers
"激活场景 / Activate scene" instead of burying the action behind button.press.
"""

from __future__ import annotations

from typing import Any

from homeassistant.components.scene import Scene
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from . import MiyueConfigEntry, MiyueRuntimeData
from .const import DOMAIN, MANUFACTURER
from .coordinator import MiyueAuxCoordinator


def _is_open(scene: dict) -> bool:
    return scene.get("isOpen") in (1, "1")


async def async_setup_entry(
    hass: HomeAssistant,
    entry: MiyueConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """One scene entity per enabled device scene; add new, prune deleted."""
    runtime = entry.runtime_data
    coordinator = runtime.aux_coordinator
    known: dict[str, MiyueScene] = {}

    @callback
    def _sync() -> None:
        # On a failed poll the coordinator keeps its last good data, so this
        # re-runs against the same list and prunes nothing — entities just go
        # unavailable. Only an authoritative read can add or remove a scene.
        data = coordinator.data or {}
        current = {
            str(s.get("id")): s
            for s in data.get("sensors", [])
            if str(s.get("id", "")) and _is_open(s)
        }
        # Prune entities whose scene was deleted or disabled on the device.
        registry = er.async_get(hass)
        for sid in [s for s in known if s not in current]:
            ent = known.pop(sid)
            if ent.entity_id and registry.async_get(ent.entity_id):
                registry.async_remove(ent.entity_id)
        new = []
        for sid in current:
            if sid not in known:
                ent = MiyueScene(runtime, sid)
                known[sid] = ent
                new.append(ent)
        if new:
            async_add_entities(new)

    _sync()
    entry.async_on_unload(coordinator.async_add_listener(_sync))


class MiyueScene(CoordinatorEntity[MiyueAuxCoordinator], Scene):
    """Fire a scene stored on the speaker via ExecuteSensor."""

    _attr_has_entity_name = True
    _attr_translation_key = "scene"

    def __init__(self, runtime: MiyueRuntimeData, sensor_id: str) -> None:
        super().__init__(runtime.aux_coordinator)
        self._runtime = runtime
        self._device = runtime.device
        self._sensor_id = sensor_id
        self._attr_unique_id = f"{self._device.udn}_scene_{sensor_id}"
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, self._device.udn)},
            manufacturer=MANUFACTURER,
        )

    @property
    def _scene(self) -> dict | None:
        for scene in (self.coordinator.data or {}).get("sensors", []):
            if str(scene.get("id", "")) == self._sensor_id:
                return scene
        return None

    @property
    def available(self) -> bool:
        return super().available and self._scene is not None

    @property
    def name(self) -> str | None:
        scene = self._scene or {}
        base = scene.get("cmdName") or f"#{self._sensor_id}"
        return f"情景 {base}"

    @property
    def extra_state_attributes(self) -> dict:
        scene = self._scene or {}
        attrs = {"scene_id": self._sensor_id}
        for key in ("cmd", "volume", "songlistInfoId", "ttsText", "ringName"):
            if key in scene:
                attrs[key] = scene[key]
        if "startHour" in scene:
            attrs["window"] = (
                f"{scene.get('startHour', 0):02d}:{scene.get('startMin', 0):02d}"
                f"-{scene.get('endHour', 23):02d}:{scene.get('endMin', 59):02d}"
            )
        return attrs

    async def async_activate(self, **kwargs: Any) -> None:
        await self._device.execute_sensor(self._sensor_id)
