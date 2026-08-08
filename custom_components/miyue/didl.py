"""Parse and build MiYue DIDL-Lite metadata.

The now-playing card's title/artist/album/cover do NOT come from AVTransport
(its GetPositionInfo returns TrackURI="local://current" with empty metadata).
They come from MiyueQueue.GetQueue, whose Result is a DIDL-Lite document with
per-item <miyue:songSrc>/<miyue:songId>/<miyue:musicId> plus the usual
dc:title / upnp:album / upnp:albumArtURI. Format verified live on M330B.
"""

from __future__ import annotations

from dataclasses import dataclass
from xml.etree import ElementTree as ET

_DC = "{http://purl.org/dc/elements/1.1/}"
_UPNP = "{urn:schemas-upnp-org:metadata-1-0/upnp/}"
_MIYUE = "{urn:miyue-hk:metadata}"


@dataclass
class MiyueTrack:
    """One queue item as the controller needs it."""

    item_id: str
    title: str
    artist: str
    album: str
    album_art: str
    song_src: int | None
    song_id: str
    music_id: str
    duration: str  # "H:MM:SS" as given by the device, may be "0:00:00"
    res_uri: str


def repair_mojibake(value: str) -> str:
    """Repair GBK ID3 tags a device served as latin1 (e.g. 'ÒôÀÖÈÈËÑ'->'音乐热搜').

    Only apply to LOCAL-file tags (songSrc==0); online sources arrive as proper
    UTF-8. Even so this is deliberately strict, because a single accented Latin
    letter followed by a byte is often a *valid* GBK sequence — decoding
    'Björk'/'Größe'/'Sigur Rós' would corrupt them into CJK garbage. We only
    re-decode when the high bytes form an even, GBK-range run that decodes to a
    real run of >=2 CJK characters, which single/paired accents cannot fake.
    """
    if not value or any(ord(c) > 0xFF for c in value):
        return value  # already real Unicode (or empty)
    highs = [c for c in value if ord(c) > 0x7F]
    n = len(highs)
    if n < 4 or n % 2 != 0:
        return value  # need >=2 CJK chars' worth of paired double-byte codes
    if any(ord(c) < 0x81 for c in highs):
        return value  # 0x80..0xA0 aren't GBK bytes -> ordinary accented Latin
    try:
        repaired = value.encode("latin1").decode("gbk")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return value
    cjk = sum(1 for c in repaired if "一" <= c <= "鿿")
    if cjk >= 2 and cjk >= n // 2 - 1:
        return repaired
    return value


def _text(item: ET.Element, tag: str) -> str:
    el = item.find(tag)
    return (el.text or "").strip() if el is not None else ""


def _int_or_none(value: str) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def parse_didl(didl: str) -> list[MiyueTrack]:
    """Parse a DIDL-Lite string into MiyueTrack items (order preserved)."""
    if not didl or not didl.strip():
        return []
    try:
        root = ET.fromstring(didl)
    except ET.ParseError:
        return []

    tracks: list[MiyueTrack] = []
    # Items may be namespaced under the DIDL-Lite default ns; match by local name.
    for item in root:
        if not item.tag.endswith("item"):
            continue
        res = item.find("{urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/}res")
        if res is None:  # some firmwares emit <res> without the default ns prefix
            res = item.find("res")
        title = _text(item, f"{_DC}title")
        # dc:creator is the DIDL-Lite standard field and upnp:artist the UPnP
        # extension; firmwares differ. Linux emits upnp:artist, the Android
        # occupier/now-playing DIDL emits dc:creator -- a sync slave's card
        # showed no artist at all until this fallback. Read both.
        artist = _text(item, f"{_UPNP}artist") or _text(item, f"{_DC}creator")
        album = _text(item, f"{_UPNP}album")
        song_src = _int_or_none(_text(item, f"{_MIYUE}songSrc"))
        if song_src == 0:  # only local files carry GBK-as-latin1 ID3 tags
            title = repair_mojibake(title)
            artist = repair_mojibake(artist)
            album = repair_mojibake(album)
        tracks.append(
            MiyueTrack(
                item_id=item.get("id", ""),
                title=title,
                artist=artist,
                album=album,
                album_art=_text(item, f"{_UPNP}albumArtURI"),
                song_src=song_src,
                song_id=_text(item, f"{_MIYUE}songId"),
                music_id=_text(item, f"{_MIYUE}musicId"),
                duration=(res.get("duration", "") if res is not None else ""),
                res_uri=(res.text or "").strip() if res is not None else "",
            )
        )
    return tracks


import re as _re

# Direct-link sources the firmware will NOT self-resolve: the <res> URL must be
# non-empty or playback is silent (radio=8, ergeduoduo=10, NAS/DMS=21).
DIRECT_LINK_SRCS = {8, 10, 21}

_ITEM_RE = _re.compile(r"<item\b.*?</item>", _re.S)
_EMPTY_RES_RE = _re.compile(r"(<res\b[^>]*>)\s*(</res>)")
_SONGID_RE = _re.compile(r"<miyue:songId>(\d+)</miyue:songId>")


def fill_radio_res(didl: str) -> str:
    """Synthesize missing qingting stream URLs for collected radios.

    Old collect paths stored radios (songSrc=8) with an EMPTY <res> — and the
    firmware never self-resolves direct-link sources, so such entries play as
    silence. Qingting live URLs are derivable from the channel id (= songId):
    http://ls.qingting.fm/live/<id>/24k.m3u8 (same pattern the app stores for
    properly-collected radios; verified live). String-level surgery on purpose:
    the firmware's DIDL parser must see its own formatting untouched.
    """
    if "<miyue:songSrc>8</miyue:songSrc>" not in didl:
        return didl

    out: list[str] = []
    pos = 0
    for m in _ITEM_RE.finditer(didl):
        out.append(didl[pos:m.start()])
        block = m.group(0)
        if "<miyue:songSrc>8</miyue:songSrc>" in block:
            sid = _SONGID_RE.search(block)
            if sid and _EMPTY_RES_RE.search(block):
                url = f"http://ls.qingting.fm/live/{sid.group(1)}/24k.m3u8"
                block = _EMPTY_RES_RE.sub(rf"\g<1>{url}\g<2>", block, count=1)
        out.append(block)
        pos = m.end()
    out.append(didl[pos:])
    return "".join(out)


def duration_to_seconds(value: str) -> int | None:
    """Convert 'H:MM:SS' / 'HH:MM:SS' to seconds. Returns None for 0 or junk.

    Long audiobooks (>=1h) were mis-shown in the apps as raw minutes or with the
    hour dropped; parse defensively as colon-separated fields, big-endian.
    """
    if not value or value in ("0:00:00", "00:00:00"):
        return None
    parts = value.split(":")
    try:
        nums = [int(p) for p in parts]
    except ValueError:
        return None
    seconds = 0
    for n in nums:
        seconds = seconds * 60 + n
    return seconds or None
