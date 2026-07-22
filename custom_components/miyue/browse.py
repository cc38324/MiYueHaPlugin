"""async_browse_media tree + play resolution for MiYue device-local content.

All browse reads are login-free (device SQLite: collected songlists/songs, local
USB/TF library, current queue). Playing any item uses MiyueQueue.ReplaceQueue
(auto-plays StartingIndex) — except the current queue, which uses SeekToTrack.

media_content_id encoding (compact, delimiter-safe):
  containers : "root" | "fav" | "fav_songlists" | "fav_boards" | "local"
               | "local_artists" | "local_albums" | "local_folders"
  playable   : "liked" | "radios" | "local_all" | "queue"
               | "songlist:<id>" | "board:<id>"
               | "artist:<qkey>" | "album:<qkey>" | "folder:<qkey>"
  a track     : "<playable>|<index>"   (index into that list; queue|<i> -> SeekToTrack)
Variable keys are urllib-quoted (safe='') so ':' '|' '/' never appear raw.

Paged sources (local library, queue) are fetched page-by-page up to MAX_TRACKS
and merged, so libraries beyond one 500-item page neither truncate the browse
list nor the queue that ReplaceQueue receives.
"""

from __future__ import annotations

import logging
from urllib.parse import quote, unquote

from homeassistant.components.media_player import BrowseMedia, MediaClass, MediaType
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError

from . import dms
from .device import MiyueDevice
from .didl import (
    DIRECT_LINK_SRCS,
    MiyueTrack,
    fill_radio_res,
    parse_didl,
    repair_mojibake,
)

_LOGGER = logging.getLogger(__name__)

PAGE_SIZE = 500
# Hard cap on how many tracks we fetch/enqueue from a paged source. Above this
# we log the truncation instead of silently dropping the tail.
MAX_TRACKS = 2000

# Human titles for every fixed node (id -> title).
LABELS = {
    "root": "MiYue",
    "fav": "收藏 Favorites",
    "local": "本机/U盘 Local",
    "nas": "NAS 媒体库 DMS",
    "fav_songlists": "歌单 Songlists",
    "fav_boards": "榜单 Boards",
    "local_artists": "歌手 Artists",
    "local_albums": "专辑 Albums",
    "local_folders": "文件夹 Folders",
    "liked": "我喜欢的 Liked",
    "radios": "电台 Radios",
    "local_all": "全部歌曲 All songs",
    "queue": "当前队列 Queue",
}


async def async_browse(
    hass: HomeAssistant, device: MiyueDevice, content_id: str | None
) -> BrowseMedia:
    cid = content_id or "root"
    if cid == "root":
        return _dir("root", [
            _dir("fav"),
            _dir("local"),
            _dir("nas"),
            _playable_dir("queue", MediaClass.PLAYLIST),
        ])
    if cid == "nas":
        servers = await dms.async_get_media_servers(hass)
        return _dir("nas", [
            _dir(f"nasrv:{quote(s.udn, safe='')}", title=s.name)
            for s in servers
        ])
    if cid.startswith(("nasrv:", "nascont:")):
        return await _browse_dms(hass, cid)
    if cid == "fav":
        return _dir("fav", [
            _playable_dir("liked", MediaClass.PLAYLIST),
            _playable_dir("radios", MediaClass.PLAYLIST),
            _dir("fav_songlists"),
            _dir("fav_boards"),
        ])
    if cid == "local":
        return _dir("local", [
            _dir("local_artists"),
            _dir("local_albums"),
            _dir("local_folders"),
            _playable_dir("local_all", MediaClass.PLAYLIST),
        ])
    if cid == "fav_songlists":
        lists = await device.get_collected_songlists()
        return _dir("fav_songlists", [
            _playable_dir(f"songlist:{sl.get('id')}", MediaClass.PLAYLIST,
                          title=_name(sl), thumb=sl.get("icon"))
            for sl in lists
        ])
    if cid == "fav_boards":
        boards = await device.get_collected_boards()
        return _dir("fav_boards", [
            _playable_dir(f"board:{b.get('id')}", MediaClass.PLAYLIST,
                          title=_name(b))
            for b in boards
        ])
    if cid in ("local_artists", "local_albums", "local_folders"):
        group_by = {"local_artists": "artist", "local_albums": "album",
                    "local_folders": "folder"}[cid]
        cats = await device.get_local_categories(group_by)
        return _dir(cid, [
            _playable_dir(
                f"{group_by}:{quote(str(c.get('key', '')), safe='')}",
                MediaClass.DIRECTORY,
                title=repair_mojibake(_name(c)),
                thumb=c.get("icon"),
            )
            for c in cats
        ])

    # Playable leaves -> list their tracks.
    title = await _leaf_title(device, cid)
    tracks = await _leaf_tracks(device, cid)
    return _playable_dir(
        cid, MediaClass.PLAYLIST, title=title,
        children=[_track_node(cid, i, t) for i, t in enumerate(tracks)],
    )


async def _browse_dms(hass: HomeAssistant, cid: str) -> BrowseMedia:
    """List one NAS server root or container: sub-folders + audio tracks."""
    server, object_id = await _resolve_dms(hass, cid)
    entries = await dms.async_browse_object(hass, server, object_id)
    qudn = quote(server.udn, safe="")
    children: list[BrowseMedia] = []
    for entry in entries:
        if entry.is_container:
            # 只留音乐:照片/视频容器(按 upnp:class 提示或常见目录名)不进树。
            if entry.looks_non_audio:
                continue
            children.append(_playable_dir(
                f"nascont:{qudn}:{quote(entry.object_id, safe='')}",
                MediaClass.DIRECTORY, title=entry.title,
                thumb=entry.art or None,
            ))
    tracks = [e for e in entries if not e.is_container]
    for i, t in enumerate(tracks):
        title = t.title if not t.artist else f"{t.title} — {t.artist}"
        children.append(BrowseMedia(
            media_class=MediaClass.TRACK,
            media_content_id=f"{cid}|{i}",
            media_content_type=MediaType.TRACK,
            title=title,
            can_play=True,
            can_expand=False,
            thumbnail=t.art or None,
        ))
    return _playable_dir(cid, MediaClass.DIRECTORY, title=server.name,
                         children=children)


async def _resolve_dms(hass: HomeAssistant, leaf: str):
    """'nasrv:<qudn>' -> (server, '0'); 'nascont:<qudn>:<qobj>' -> (server, obj)."""
    parts = leaf.split(":", 2)
    udn = unquote(parts[1])
    object_id = unquote(parts[2]) if len(parts) > 2 else "0"
    server = await dms.async_find_server(hass, udn)
    if server is None:
        raise HomeAssistantError("NAS 已不在线（SSDP 缓存里找不到了），稍后再试")
    return server, object_id


async def async_play(
    hass: HomeAssistant, device: MiyueDevice, content_id: str
) -> None:
    """Resolve a media_content_id to a device play action."""
    leaf, index = _split_index(content_id)

    if leaf == "queue":
        # The queue is already loaded; just jump (or restart at 0).
        await device.seek_to_track(index if index is not None else 0)
        return

    if leaf.startswith(("nasrv:", "nascont:")):
        server, object_id = await _resolve_dms(hass, leaf)
        entries = await dms.async_browse_object(hass, server, object_id)
        tracks = [e for e in entries if not e.is_container]
        if not tracks:
            raise HomeAssistantError("该 NAS 目录下没有可播的音频")
        didl = dms.build_miyue_didl(tracks, server.name)
        await device.replace_queue(didl, index or 0)
        return

    didl = await _leaf_didl(device, leaf)
    if not didl:
        return
    # Old collect paths stored radios without a stream URL, and the ANDROID
    # firmware's controller-queue path never self-resolves direct-link sources
    # (device-local play and the Linux firmware do) -- synthesize the qingting
    # URL so pushed radios play on both generations.
    didl = fill_radio_res(didl)

    start = index or 0
    tracks = parse_didl(didl)
    if 0 <= start < len(tracks):
        t = tracks[start]
        if t.song_src in DIRECT_LINK_SRCS and not t.res_uri:
            raise HomeAssistantError(
                f"「{t.title}」这条收藏缺少直链(songSrc={t.song_src})，设备无法"
                "自解析——请在手机 App 里重新收藏一次"
            )
    await device.replace_queue(didl, start)


# -- leaf resolution --------------------------------------------------------
async def _leaf_didl(device: MiyueDevice, leaf: str) -> str:
    if leaf == "liked":
        return await device.get_collected_music_didl()
    if leaf == "radios":
        return await device.get_collected_radios_didl()
    if leaf == "local_all":
        return await _paged(leaf, device.get_local_songs_didl)
    if leaf == "queue":
        async def _queue_page(start: int, count: int) -> tuple[str, int]:
            didl, total, _cur = await device.get_queue_didl(start, count)
            return didl, total
        return await _paged(leaf, _queue_page)
    if leaf.startswith("songlist:") or leaf.startswith("board:"):
        return await device.get_songlist_tracks_didl(leaf.split(":", 1)[1])
    for prefix in ("artist", "album", "folder"):
        if leaf.startswith(f"{prefix}:"):
            key = unquote(leaf.split(":", 1)[1])

            async def _cat_page(start: int, count: int) -> tuple[str, int]:
                return await device.get_local_category_tracks_didl(
                    prefix, key, start, count)
            return await _paged(leaf, _cat_page)
    return ""


async def _paged(leaf: str, fetch) -> str:
    """Fetch (didl, total) pages until Total or MAX_TRACKS; merge the DIDL."""
    pages: list[str] = []
    got = 0
    total = None
    while got < (total if total is not None else 1):
        didl, page_total = await fetch(got, PAGE_SIZE)
        if total is None:
            total = max(page_total, 0)
        count = didl.count("<item")
        if count == 0:
            break
        pages.append(didl)
        got += count
        if got >= MAX_TRACKS:
            _LOGGER.warning(
                "miyue browse %s: capped at %d of %d tracks", leaf, got, total)
            break
    return _merge_didl(pages)


def _merge_didl(pages: list[str]) -> str:
    """Merge multiple DIDL-Lite documents by splicing item runs together.

    String-level on purpose: re-serializing via ElementTree would rewrite the
    namespace prefixes (miyue: -> ns2:), which the firmware's string-matching
    parser may not accept.
    """
    pages = [p for p in pages if p and "<item" in p]
    if not pages:
        return ""
    if len(pages) == 1:
        return pages[0]
    first = pages[0]
    close = first.rfind("</DIDL-Lite>")
    if close < 0:
        return first
    inner_parts = [first[:close]]
    for page in pages[1:]:
        start = page.find("<item")
        end = page.rfind("</DIDL-Lite>")
        if start < 0 or end < 0:
            continue
        inner_parts.append(page[start:end])
    inner_parts.append("</DIDL-Lite>")
    return "".join(inner_parts)


async def _leaf_tracks(device: MiyueDevice, leaf: str) -> list[MiyueTrack]:
    return parse_didl(await _leaf_didl(device, leaf))


async def _leaf_title(device: MiyueDevice, cid: str) -> str:
    """Human title for a playable-leaf page (never the raw content id)."""
    if cid in LABELS:
        return LABELS[cid]
    kind, _, rest = cid.partition(":")
    if kind in ("songlist", "board"):
        try:
            lists = (await device.get_collected_songlists() if kind == "songlist"
                     else await device.get_collected_boards())
        except Exception:  # noqa: BLE001 - title lookup must never break browse
            lists = []
        for obj in lists:
            if str(obj.get("id")) == rest:
                return _name(obj)
        return "歌单 Songlist" if kind == "songlist" else "榜单 Board"
    if kind in ("artist", "album", "folder"):
        key = unquote(rest)
        if kind == "folder":
            key = key.rstrip("/").rsplit("/", 1)[-1] or key
        return repair_mojibake(key)
    return cid


# -- BrowseMedia builders ---------------------------------------------------
def _dir(cid: str, children: list[BrowseMedia] | None = None,
         title: str | None = None) -> BrowseMedia:
    return BrowseMedia(
        media_class=MediaClass.DIRECTORY,
        media_content_id=cid,
        media_content_type=MediaType.PLAYLIST,
        title=title or LABELS.get(cid, cid),
        can_play=False,
        can_expand=True,
        children=children or [],
        children_media_class=MediaClass.DIRECTORY,
    )


def _playable_dir(
    cid: str,
    media_class: MediaClass,
    title: str | None = None,
    children: list[BrowseMedia] | None = None,
    thumb: str | None = None,
) -> BrowseMedia:
    return BrowseMedia(
        media_class=media_class,
        media_content_id=cid,
        media_content_type=MediaType.PLAYLIST,
        title=title or LABELS.get(cid, cid),
        can_play=True,
        can_expand=True,
        children=children or [],
        children_media_class=MediaClass.TRACK,
        thumbnail=thumb or None,
    )


def _track_node(container_id: str, index: int, track: MiyueTrack) -> BrowseMedia:
    title = track.title or f"Track {index + 1}"
    if track.artist:
        title = f"{title} — {track.artist}"
    return BrowseMedia(
        media_class=MediaClass.TRACK,
        media_content_id=f"{container_id}|{index}",
        media_content_type=MediaType.TRACK,
        title=title,
        can_play=True,
        can_expand=False,
        thumbnail=track.album_art or None,
    )


# -- helpers ----------------------------------------------------------------
def _name(obj: dict) -> str:
    return str(obj.get("name") or obj.get("cmdName") or obj.get("key") or "?")


def _split_index(content_id: str) -> tuple[str, int | None]:
    if "|" in content_id:
        leaf, idx = content_id.rsplit("|", 1)
        try:
            return leaf, int(idx)
        except ValueError:
            return leaf, None
    return content_id, None
