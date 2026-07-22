"""Browse third-party DLNA MediaServers (NAS) and enqueue their tracks on MiYue.

Servers come from HA's SSDP cache (the ssdp integration caches every device it
hears, whether or not any integration claimed it) — zero extra discovery.
Playing pushes a MiYue queue built with songSrc=21 (SRC_DMS, direct-link) and
<miyue:sourceName> so the speaker shows where the music came from. Format is
byte-compatible with the Flutter controller's forQueue() and was verified live
(QNAP -> M210B PLAYING).
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from html import escape
from urllib.parse import unquote, urljoin, urlparse

from homeassistant.core import HomeAssistant
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .soap import SoapClient, parse_xml_lenient

_LOGGER = logging.getLogger(__name__)

# Root-level container names that are clearly not music (localized variants
# from minidlna / QNAP / Synology / WMP), plus class hints used at any depth.
_NON_AUDIO_TITLES = {
    "video", "videos", "movie", "movies", "影片", "视频", "电影", "录像",
    "picture", "pictures", "photo", "photos", "image", "images",
    "照片", "图片", "图像",
}
_NON_AUDIO_CLASS_HINTS = ("photo", "video", "image")

DMS_DEVICE_ST = "urn:schemas-upnp-org:device:MediaServer:1"
_CD_PREFIX = "urn:schemas-upnp-org:service:ContentDirectory"

_DIDL_NS = "{urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/}"
_DC = "{http://purl.org/dc/elements/1.1/}"
_UPNP = "{urn:schemas-upnp-org:metadata-1-0/upnp/}"


@dataclass
class MediaServer:
    udn: str
    name: str
    base_url: str       # http://host:port
    control_path: str   # /ctl/ContentDir
    service_type: str   # exact urn:...:ContentDirectory:N of this server


@dataclass
class DmsEntry:
    """One ContentDirectory child: a container (folder) or an audio item."""

    is_container: bool
    object_id: str
    title: str
    artist: str = ""
    album: str = ""
    art: str = ""
    url: str = ""
    protocol_info: str = ""
    duration: str = "0:00:00"
    upnp_class: str = ""

    @property
    def looks_non_audio(self) -> bool:
        """Photo/video container by class hint or well-known root title."""
        cls = self.upnp_class.lower()
        if any(hint in cls for hint in _NON_AUDIO_CLASS_HINTS):
            return True
        return self.title.strip().lower() in _NON_AUDIO_TITLES


async def async_get_media_servers(hass: HomeAssistant) -> list[MediaServer]:
    """MediaServers currently known to HA's SSDP cache (with ContentDirectory)."""
    from homeassistant.components import ssdp

    servers: list[MediaServer] = []
    seen: set[str] = set()
    try:
        infos = await ssdp.async_get_discovery_info_by_st(hass, DMS_DEVICE_ST)
    except Exception:  # noqa: BLE001 - cache lookup must never break browse
        return []
    for info in infos:
        udn = getattr(info, "ssdp_udn", "") or ""
        location = getattr(info, "ssdp_location", "") or ""
        if not udn or not location or udn in seen:
            continue
        upnp = getattr(info, "upnp", None) or {}
        svc = _find_content_directory(upnp.get("serviceList") or {})
        if svc is None:
            continue
        control, service_type = svc
        parsed = urlparse(location)
        base = f"{parsed.scheme}://{parsed.netloc}"
        if control.startswith("http"):
            cp = urlparse(control)
            base, control = f"{cp.scheme}://{cp.netloc}", cp.path
        elif not control.startswith("/"):
            control = urlparse(urljoin(location, control)).path
        seen.add(udn)
        servers.append(MediaServer(
            udn=udn,
            name=str(upnp.get("friendlyName") or parsed.hostname or "NAS"),
            base_url=base,
            control_path=control,
            service_type=service_type,
        ))
    servers.sort(key=lambda s: s.name)
    return servers


def _find_content_directory(service_list) -> tuple[str, str] | None:
    services = service_list.get("service") if isinstance(service_list, dict) else None
    if isinstance(services, dict):
        services = [services]
    for svc in services or []:
        stype = str(svc.get("serviceType", ""))
        if stype.startswith(_CD_PREFIX) and svc.get("controlURL"):
            return str(svc["controlURL"]), stype
    return None


async def async_find_server(hass: HomeAssistant, udn: str) -> MediaServer | None:
    for server in await async_get_media_servers(hass):
        if server.udn == udn:
            return server
    return None


async def async_browse_object(
    hass: HomeAssistant,
    server: MediaServer,
    object_id: str,
    count: int = 500,
) -> list[DmsEntry]:
    """One ContentDirectory Browse(BrowseDirectChildren) page, parsed."""
    client = SoapClient(async_get_clientsession(hass), server.base_url)
    result = await client.call(
        server.control_path, server.service_type, "Browse",
        {"ObjectID": object_id, "BrowseFlag": "BrowseDirectChildren",
         "Filter": "*", "StartingIndex": "0", "RequestedCount": str(count),
         "SortCriteria": ""},
    )
    return parse_dms_didl(result.get("Result", ""))


def parse_dms_didl(didl: str) -> list[DmsEntry]:
    from xml.etree import ElementTree as ET

    if not didl or not didl.strip():
        return []
    try:
        root = parse_xml_lenient(didl)
    except ET.ParseError:
        return []
    entries: list[DmsEntry] = []
    for el in root:
        tag = el.tag.rsplit("}", 1)[-1]

        def text(path: str) -> str:
            node = el.find(path)
            return (node.text or "").strip() if node is not None else ""

        if tag == "container":
            entries.append(DmsEntry(
                is_container=True, object_id=el.get("id", ""),
                title=text(f"{_DC}title") or "?",
                art=text(f"{_UPNP}albumArtURI"),
                upnp_class=text(f"{_UPNP}class"),
            ))
        elif tag == "item":
            if "audioItem" not in text(f"{_UPNP}class"):
                continue
            res = el.find(f"{_DIDL_NS}res")
            url = (res.text or "").strip() if res is not None else ""
            if not url:
                continue
            title = text(f"{_DC}title") or _basename(url)
            entries.append(DmsEntry(
                is_container=False, object_id=el.get("id", ""), title=title,
                artist=text(f"{_UPNP}artist") or text(f"{_DC}creator"),
                album=text(f"{_UPNP}album"), art=text(f"{_UPNP}albumArtURI"),
                url=url,
                protocol_info=res.get("protocolInfo", "") if res is not None else "",
                duration=res.get("duration", "0:00:00") if res is not None else "0:00:00",
            ))
    return entries


def build_miyue_didl(tracks: list[DmsEntry], source_name: str) -> str:
    """MiYue queue DIDL for direct-link NAS tracks (songSrc=21 + sourceName)."""
    parts = ['<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" '
             'xmlns:dc="http://purl.org/dc/elements/1.1/" '
             'xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/" '
             'xmlns:miyue="urn:miyue-hk:metadata">']
    for t in tracks:
        art = (f"<upnp:albumArtURI>{escape(t.art)}</upnp:albumArtURI>"
               if t.art else "")
        parts.append(
            f'<item id="{escape(t.object_id)}" parentID="-1" restricted="1">'
            f"<dc:title>{escape(t.title)}</dc:title>"
            f"<dc:creator>{escape(t.artist)}</dc:creator>"
            f"<upnp:artist>{escape(t.artist)}</upnp:artist>"
            f"<upnp:album>{escape(t.album)}</upnp:album>{art}"
            f"<upnp:class>object.item.audioItem.musicTrack</upnp:class>"
            f"<miyue:songSrc>21</miyue:songSrc>"
            f"<miyue:songId>{escape(t.object_id)}</miyue:songId>"
            f"<miyue:sourceName>{escape(source_name)}</miyue:sourceName>"
            f'<res protocolInfo="http-get:*:{_mime(t.protocol_info)}:*" '
            f'duration="{escape(t.duration or "0:00:00")}">{escape(t.url)}</res>'
            f"</item>"
        )
    parts.append("</DIDL-Lite>")
    return "".join(parts)


def _mime(protocol_info: str) -> str:
    fields = protocol_info.split(":")
    if len(fields) >= 3 and fields[2] and fields[2] != "*":
        return fields[2]
    return "audio/mpeg"


def _basename(url: str) -> str:
    path = urlparse(url).path
    name = unquote(path.rsplit("/", 1)[-1]) or "Track"
    return name
