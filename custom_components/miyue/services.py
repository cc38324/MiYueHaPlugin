"""Service schemas (registered as entity services on the media_player)."""

from __future__ import annotations

import voluptuous as vol

from homeassistant.helpers import config_validation as cv

SERVICE_PLAY_TTS = "play_tts"

PLAY_TTS_SCHEMA = {
    vol.Required("message"): cv.string,
    # Omitted -> each speaker announces at ITS OWN current volume (the Linux
    # firmware never restores volume after TTS, so matching current is safest).
    vol.Optional("volume"): vol.All(vol.Coerce(int), vol.Range(1, 100)),
    vol.Optional("announce_group", default=True): cv.boolean,
}
