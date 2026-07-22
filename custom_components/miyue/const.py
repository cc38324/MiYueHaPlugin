"""Constants for the MiYue multi-room speaker integration.

Everything here was verified against live firmware (M330B eng.miyue.20260712,
Android M100 sw 1.0) by dumping description.xml + every SCPD and firing
read-only SOAP calls. See docs/CONTRACT.md for the captured wire dumps.
"""

from __future__ import annotations

DOMAIN = "miyue"
MANUFACTURER = "MiYue Intelligent Electronics Co., Ltd."

# --- Discovery -------------------------------------------------------------
# Root device type advertised by both the Linux (MiYue) and Android
# (Miyuemusic) firmware. SSDP announces this as the rootdevice.
ROOT_DEVICE_TYPE = "urn:schemas-upnp-org:device:MultiRoomMusicPlayer:1"
# Private metadata namespace used in description.xml and DIDL-Lite.
MIYUE_METADATA_NS = "urn:miyue-hk:metadata"

# The embedded MediaRenderer's UDN is the root UDN + this suffix (Linux fw).
MR_UDN_SUFFIX = "-mr"

# --- Standard MediaRenderer services (async_upnp_client DmrDevice) ----------
ST_AVTRANSPORT = "urn:schemas-upnp-org:service:AVTransport:1"
ST_RENDERING = "urn:schemas-upnp-org:service:RenderingControl:1"
ST_CONNECTION = "urn:schemas-upnp-org:service:ConnectionManager:1"

AVTRANSPORT_CONTROL = "/MediaRenderer/AVTransport/Control"
AVTRANSPORT_EVENT = "/MediaRenderer/AVTransport/Event"
RENDERING_CONTROL = "/MediaRenderer/RenderingControl/Control"
RENDERING_EVENT = "/MediaRenderer/RenderingControl/Event"

# --- Private urn:miyue-hk services -----------------------------------------
# serviceType -> (control path, event path). Control/event paths are fixed
# (/_control/<Name>, /_event/<Name>); only the HTTP port drifts across reboots.
ST_GROUP = "urn:miyue-hk:service:MiyueGroup:1"
ST_SYSTEM = "urn:miyue-hk:service:MiyueSystem:1"
ST_AUDIOSOURCE = "urn:miyue-hk:service:MiyueAudioSource:1"
ST_LIBRARY = "urn:miyue-hk:service:MiyueLibrary:1"
ST_QUEUE = "urn:miyue-hk:service:MiyueQueue:1"
ST_ALARM = "urn:miyue-hk:service:MiyueAlarmClock:1"
ST_SENSOR = "urn:miyue-hk:service:MiyueSensor:1"
ST_ACCOUNT = "urn:miyue-hk:service:MiyueAccount:1"
ST_INTERCOM = "urn:miyue-hk:service:MiyueIntercom:1"
# Android-only; absent on the Linux firmware. Present for completeness.
ST_UPDATE = "urn:miyue-hk:service:MiyueUpdate:1"


def miyue_control(name: str) -> str:
    """Control URL path for a private MiyueXxx service (name without prefix)."""
    return f"/_control/{name}"


def miyue_event(name: str) -> str:
    """Event (GENA) URL path for a private MiyueXxx service."""
    return f"/_event/{name}"


# --- AVTransport transport states -> HA MediaPlayerState -------------------
# (imported lazily in media_player to avoid a hard dep here)
TRANSPORT_PLAYING = "PLAYING"
TRANSPORT_PAUSED = "PAUSED_PLAYBACK"
TRANSPORT_STOPPED = "STOPPED"
TRANSPORT_TRANSITIONING = "TRANSITIONING"
TRANSPORT_NO_MEDIA = "NO_MEDIA_PRESENT"

# --- MiyueQueue play modes -------------------------------------------------
PLAYMODE_NORMAL = "NORMAL"
PLAYMODE_REPEAT_ONE = "REPEAT_ONE"
PLAYMODE_REPEAT_ALL = "REPEAT_ALL"
PLAYMODE_SHUFFLE = "SHUFFLE"

# --- MiyueGroup roles ------------------------------------------------------
# NOTE: role=="None" means standalone. An invariant from the firmware audit:
# role=="None" must VETO any non-null GroupID (a standalone device may still
# report a self-derived GroupID). Never treat such a device as grouped.
ROLE_NONE = "None"
ROLE_MASTER = "Master"
ROLE_SLAVE = "Slave"

# --- Audio sources (MiyueAudioSource) --------------------------------------
# SelectSource(Source) argument values, and the GetExternalInputs flags that
# gate their availability. "Music" returns control to the streaming/queue path.
SOURCE_MUSIC = "Music"
SOURCE_AUX = "AUX"
SOURCE_SPDIF = "SPDIF"
SOURCE_BLUETOOTH = "Bluetooth"

# GetExternalInputs output flag -> (source id, "with" flag, "open" flag)
EXTERNAL_INPUT_FLAGS = {
    SOURCE_AUX: ("WithAux", "OpenAux"),
    SOURCE_SPDIF: ("WithSpdif", "OpenSpdif"),
    SOURCE_BLUETOOTH: ("WithBluetooth", "OpenBluetooth"),
}

# --- miyue:songSrc enum (DIDL-Lite <miyue:songSrc>) ------------------------
# Source id carried per queue item. Drives display + whether the device can
# self-resolve a stream URL. Values confirmed across the repos' memory notes:
SONGSRC_LOCAL = 0
SONGSRC_DLNA_QPLAY = 3  # inbound QPlay/DLNA push (e.g. Kugou) -- not DMS browse
SONGSRC_XIMALAYA = 4
SONGSRC_AIRPLAY = 8
SONGSRC_ERGEDUODUO = 10
SONGSRC_KUGOU = 13
SONGSRC_BLUETOOTH = 14
SONGSRC_NETEASE = 15
SONGSRC_KUGOU_RADIO = 16
SONGSRC_NAS_DMS = 21  # NAS direct-link browse

# --- DIDL-Lite namespaces (for building/parsing metadata) ------------------
DIDL_NS = {
    "": "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/",
    "dc": "http://purl.org/dc/elements/1.1/",
    "upnp": "urn:schemas-upnp-org:metadata-1-0/upnp/",
    "miyue": MIYUE_METADATA_NS,
}

# --- Config entry keys -----------------------------------------------------
CONF_UDN = "udn"
CONF_HOST = "host"
CONF_PORT = "port"
CONF_NAME = "name"
CONF_LOCATION = "location"  # full description.xml URL from SSDP

# --- Timeouts / cadence ----------------------------------------------------
SOAP_TIMEOUT = 6.0
GENA_SUBSCRIBE_TIMEOUT = 300  # seconds; renew at ~half
# Devices reboot often (RTC -> 2000 -> NTP jump) and re-pick their HTTP port;
# an active M-SEARCH fallback re-locates them when GENA renew fails.
REDISCOVER_INTERVAL = 30
# Poll cadence for the DataUpdateCoordinator. Group topology uses the mirror
# model (poll every device, cluster by GroupID) so a modest interval keeps
# grouping responsive without hammering the LAN.
POLL_INTERVAL = 3.0
# Settle delay before re-reading GetGroupInfo to confirm a group mutation --
# SetGroupConfig/LeaveGroup return success asynchronously and cannot be trusted.
GROUP_APPLY_SETTLE = 0.9
GROUP_RETRY_DELAY = 0.4
