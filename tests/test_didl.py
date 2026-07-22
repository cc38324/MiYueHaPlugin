"""Pure-logic tests for DIDL parsing — no Home Assistant needed."""

from custom_components.miyue.didl import (
    duration_to_seconds,
    fill_radio_res,
    parse_didl,
    repair_mojibake,
)

# A real GetTimeline Result captured from a live M330B (netease queue).
LIVE_DIDL = (
    '<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"'
    ' xmlns:dc="http://purl.org/dc/elements/1.1/"'
    ' xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"'
    ' xmlns:miyue="urn:miyue-hk:metadata">'
    '<item id="1927693793" parentID="-1" restricted="1">'
    "<dc:title>再等冬天(Memories)</dc:title>"
    "<upnp:artist></upnp:artist><dc:creator></dc:creator>"
    "<upnp:album>故事商铺·上</upnp:album>"
    "<upnp:albumArtURI>http://p3.music.126.net/x==/109951169798343077.jpg</upnp:albumArtURI>"
    "<upnp:class>object.item.audioItem.musicTrack</upnp:class>"
    "<miyue:songSrc>15</miyue:songSrc><miyue:songId>1927693793</miyue:songId>"
    "<miyue:musicId>640085</miyue:musicId>"
    '<res protocolInfo="http-get:*:audio/mpeg:*" duration="0:03:42"></res>'
    "</item></DIDL-Lite>"
)


def test_parse_live_didl():
    tracks = parse_didl(LIVE_DIDL)
    assert len(tracks) == 1
    t = tracks[0]
    assert t.title == "再等冬天(Memories)"
    assert t.album == "故事商铺·上"
    assert t.album_art.endswith("109951169798343077.jpg")
    assert t.song_src == 15  # netease
    assert t.song_id == "1927693793"
    assert t.duration == "0:03:42"  # res element found despite DIDL namespace


def test_parse_empty():
    assert parse_didl("") == []
    assert parse_didl("   ") == []
    assert parse_didl("<not-xml") == []


def test_duration_to_seconds():
    assert duration_to_seconds("0:03:42") == 222
    assert duration_to_seconds("1:00:00") == 3600
    assert duration_to_seconds("11:57:00") == 43020  # long audiobook, H:MM:SS
    assert duration_to_seconds("0:00:00") is None
    assert duration_to_seconds("") is None
    assert duration_to_seconds("garbage") is None


def test_repair_mojibake_fixes_real_gbk():
    # Real garbled strings captured from a live device's local library.
    assert repair_mojibake("ÒôÀÖÈÈËÑ") == "音乐热搜"
    assert repair_mojibake("Õ¾³¤ËØ²Ä(sc.chinaz.com)") == "站长素材(sc.chinaz.com)"


def test_repair_mojibake_never_touches_real_latin():
    # Accented Western names must survive untouched — the old heuristic
    # corrupted these into CJK garbage (Björk -> Bj鰎k).
    for s in ("Björk", "Größe", "Sigur Rós", "Motörhead", "São Paulo",
              "naïve", "Café del Mar", "Joel Adams", "周杰伦"):
        assert repair_mojibake(s) == s


def _radio_item(song_id: str, res: str) -> str:
    return (
        f'<item id="{song_id}" parentID="-1" restricted="1">'
        f"<dc:title>radio</dc:title>"
        f"<upnp:class>object.item.audioItem.musicTrack</upnp:class>"
        f"<miyue:songSrc>8</miyue:songSrc><miyue:songId>{song_id}</miyue:songId>"
        f'<res protocolInfo="http-get:*:audio/mpeg:*" duration="0:00:00">{res}</res>'
        f"</item>"
    )


_DIDL_WRAP = (
    '<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"'
    ' xmlns:dc="http://purl.org/dc/elements/1.1/"'
    ' xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"'
    ' xmlns:miyue="urn:miyue-hk:metadata">{items}</DIDL-Lite>'
)


def test_fill_radio_res_synthesizes_qingting_url():
    # Empty-res radio (the broken collect) gets the derived qingting URL.
    didl = _DIDL_WRAP.format(items=_radio_item("20003", ""))
    filled = fill_radio_res(didl)
    assert "http://ls.qingting.fm/live/20003/24k.m3u8" in filled
    assert parse_didl(filled)[0].res_uri.endswith("/20003/24k.m3u8")


def test_fill_radio_res_leaves_good_entries_alone():
    good = _radio_item("270", "http://ls.qingting.fm/live/270/24k.m3u8")
    didl = _DIDL_WRAP.format(items=good)
    assert fill_radio_res(didl) == didl
    # Non-radio empty res (netease self-resolves) must NOT be touched.
    ne = didl.replace(">8<", ">15<")
    assert fill_radio_res(ne) == ne


def test_parse_didl_repairs_only_local_tracks():
    tmpl = (
        '<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"'
        ' xmlns:dc="http://purl.org/dc/elements/1.1/"'
        ' xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"'
        ' xmlns:miyue="urn:miyue-hk:metadata">'
        '<item id="1" parentID="-1" restricted="1">'
        "<dc:title>ÒôÀÖÈÈËÑ</dc:title><upnp:artist>x</upnp:artist>"
        "<upnp:album></upnp:album>"
        "<upnp:class>object.item.audioItem.musicTrack</upnp:class>"
        "<miyue:songSrc>{src}</miyue:songSrc><miyue:songId>1</miyue:songId>"
        '<res protocolInfo="http-get:*:audio/mpeg:*"></res></item></DIDL-Lite>'
    )
    # Local file (songSrc=0): garbled GBK tag gets repaired.
    assert parse_didl(tmpl.format(src=0))[0].title == "音乐热搜"
    # Online source (netease=15): text passes through verbatim.
    assert parse_didl(tmpl.format(src=15))[0].title == "ÒôÀÖÈÈËÑ"
