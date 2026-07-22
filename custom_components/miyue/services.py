"""Service schemas + alarm/scene JSON builders (registered as entity services)."""

from __future__ import annotations

import uuid

import voluptuous as vol

from homeassistant.helpers import config_validation as cv

SERVICE_PLAY_TTS = "play_tts"
SERVICE_CREATE_ALARM = "create_alarm"
SERVICE_DELETE_ALARM = "delete_alarm"
SERVICE_CREATE_SCENE = "create_scene"
SERVICE_DELETE_SCENE = "delete_scene"

# Weekday name -> device recurrence digit (0=Sun .. 6=Sat).
_WEEKDAYS = {"sun": 0, "mon": 1, "tue": 2, "wed": 3, "thu": 4, "fri": 5, "sat": 6}
_ALL_DAYS = "0,1,2,3,4,5,6"

PLAY_TTS_SCHEMA = {
    vol.Required("message"): cv.string,
    # Omitted -> each speaker announces at ITS OWN current volume (the Linux
    # firmware never restores volume after TTS, so matching current is safest).
    vol.Optional("volume"): vol.All(vol.Coerce(int), vol.Range(1, 100)),
    vol.Optional("announce_group", default=True): cv.boolean,
}

CREATE_ALARM_SCHEMA = {
    vol.Required("time"): cv.string,  # "HH:MM"
    vol.Optional("days"): vol.All(cv.ensure_list, [vol.In(_WEEKDAYS)]),
    vol.Optional("volume", default=50): vol.All(vol.Coerce(int), vol.Range(1, 100)),
    vol.Optional("duration", default=0): vol.All(vol.Coerce(int), vol.Range(0, 1440)),
    vol.Optional("songlist_id"): vol.Coerce(int),
    vol.Optional("tts_text"): cv.string,
    vol.Optional("enabled", default=True): cv.boolean,
}

DELETE_ALARM_SCHEMA = {vol.Required("alarm_id"): cv.string}

CREATE_SCENE_SCHEMA = {
    vol.Required("name"): cv.string,
    vol.Optional("songlist_id"): vol.Coerce(int),
    vol.Optional("volume", default=30): vol.All(vol.Coerce(int), vol.Range(1, 100)),
    vol.Optional("tts_text"): cv.string,
    vol.Optional("cmd"): cv.string,
}

DELETE_SCENE_SCHEMA = {vol.Required("scene_id"): cv.string}


def days_to_recurrence(days: list[str] | None) -> str:
    """Weekday names -> device recurrence CSV. Omitted/empty -> explicit daily.

    Always emit an explicit set (never "") because an empty recurrence means
    one-shot on Linux firmware but every-day on Android — explicit is portable.
    """
    if not days:
        return _ALL_DAYS
    digits = sorted({_WEEKDAYS[d] for d in days})
    return ",".join(str(d) for d in digits)


def _hhmm(value: str) -> str:
    parts = str(value).split(":")
    try:
        h = max(0, min(23, int(parts[0])))
        m = max(0, min(59, int(parts[1]))) if len(parts) > 1 else 0
    except (ValueError, IndexError):
        return "07:30"
    return f"{h:02d}:{m:02d}"


def build_alarm_json(data: dict) -> dict:
    songlist_id = int(data.get("songlist_id") or 0)
    tts = data.get("tts_text") or ""
    return {
        "time": _hhmm(data["time"]),
        "recurrence": days_to_recurrence(data.get("days")),
        "enabled": 1 if data.get("enabled", True) else 0,
        "volume": int(data.get("volume", 50)),
        "duration": int(data.get("duration", 0)),
        "random": 0,
        "isTimeOpen": 0,
        "isWeatherOpen": 0,
        "isTextOpen": 1 if tts else 0,
        "isMusicOpen": 1 if songlist_id else 0,
        "ttsText": tts,
        "playOrder": "时间,天气,文字,音乐",
        "musicInfoId": 0,
        "songlistInfoId": songlist_id,
        "directoryPath": "",
        "ringName": "",
    }


def build_scene_json(data: dict) -> dict:
    songlist_id = int(data.get("songlist_id") or 0)
    tts = data.get("tts_text") or ""
    cmd = data.get("cmd") or f"HA_{uuid.uuid4().hex[:8]}"
    return {
        "cmdName": data["name"],
        "cmd": cmd,  # must be unique + non-empty (scene map is keyed by cmd)
        "isOpen": 1,
        "startHour": 0, "startMin": 0, "endHour": 23, "endMin": 59,
        "volume": int(data.get("volume", 30)),
        "duration": 0,
        "random": 0,
        "isWeatherOpen": 0,
        "isMusicOpen": 1 if songlist_id else 0,
        "isTextOpen": 1 if tts else 0,
        "isTimeOpen": 0,
        "ttsText": tts,
        "textEndType": 4 if songlist_id else 1,  # 4 = replace queue & play
        "musicInfoId": 0,
        "songlistInfoId": songlist_id,
        "isOpenAUX": 0,
        "isOpenSPDIF": 0,
        "playOrder": "时间,天气,文字,音乐",
        "directoryPath": "",
        "ringName": "",
    }
