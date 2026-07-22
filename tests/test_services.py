"""Pure-logic tests for alarm/scene JSON builders + weekday mapping."""

from custom_components.miyue.services import (
    build_alarm_json,
    build_scene_json,
    days_to_recurrence,
)


def test_days_to_recurrence():
    # Omitted -> explicit daily (never "" — that means one-shot on Linux).
    assert days_to_recurrence(None) == "0,1,2,3,4,5,6"
    assert days_to_recurrence([]) == "0,1,2,3,4,5,6"
    # Sunday=0, Monday=1 ... sorted & de-duplicated.
    assert days_to_recurrence(["mon", "fri", "mon"]) == "1,5"
    assert days_to_recurrence(["sun", "sat"]) == "0,6"


def test_build_alarm_json_songlist():
    j = build_alarm_json(
        {"time": "7:5", "days": ["mon", "tue"], "volume": 30, "songlist_id": 5}
    )
    assert j["time"] == "07:05"  # zero-padded
    assert j["recurrence"] == "1,2"
    assert j["isMusicOpen"] == 1
    assert j["songlistInfoId"] == 5
    assert j["isTextOpen"] == 0
    assert j["enabled"] == 1


def test_build_alarm_json_tts_only():
    j = build_alarm_json({"time": "08:00", "tts_text": "起床啦", "enabled": False})
    assert j["isTextOpen"] == 1
    assert j["ttsText"] == "起床啦"
    assert j["isMusicOpen"] == 0
    assert j["songlistInfoId"] == 0
    assert j["enabled"] == 0
    assert j["recurrence"] == "0,1,2,3,4,5,6"  # explicit daily default


def test_build_scene_json():
    j = build_scene_json({"name": "观影模式", "songlist_id": 12, "volume": 20})
    assert j["cmdName"] == "观影模式"
    assert j["cmd"].startswith("HA_") and len(j["cmd"]) > 3  # unique non-empty
    assert j["textEndType"] == 4  # replace-queue & play
    assert j["isMusicOpen"] == 1
    assert j["songlistInfoId"] == 12
    assert j["isOpen"] == 1


def test_build_scene_json_custom_cmd():
    j = build_scene_json({"name": "x", "cmd": "KNX_1_2_3"})
    assert j["cmd"] == "KNX_1_2_3"
    assert j["textEndType"] == 1  # no songlist -> not a replace-play scene
