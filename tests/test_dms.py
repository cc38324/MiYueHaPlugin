"""Pure-logic tests for NAS/DMS browse parsing + MiYue queue building."""

from custom_components.miyue.didl import parse_didl
from custom_components.miyue.dms import (
    DmsEntry,
    _find_content_directory,
    _mime,
    build_miyue_didl,
    parse_dms_didl,
)

# A minidlna-style Browse Result (as returned by a real QNAP).
MINIDLNA_DIDL = (
    '<DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/"'
    ' xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"'
    ' xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/">'
    '<container id="1$4" parentID="1" restricted="1" childCount="9">'
    "<dc:title>All Music</dc:title>"
    "<upnp:class>object.container.storageFolder</upnp:class></container>"
    '<item id="1$4$1" parentID="1$4" restricted="1">'
    "<dc:title>(Everything I Do) I Do It For You</dc:title>"
    "<dc:creator>Bryan Adams</dc:creator><upnp:artist>Bryan Adams</upnp:artist>"
    "<upnp:album>Waking Up The Neighbours</upnp:album>"
    "<upnp:class>object.item.audioItem.musicTrack</upnp:class>"
    '<res protocolInfo="http-get:*:audio/x-flac:*" duration="0:06:34.000">'
    "http://192.168.1.31:8200/MediaItems/71106820.flac</res></item>"
    '<item id="1$4$2" parentID="1$4" restricted="1">'
    "<dc:title></dc:title>"  # minidlna sometimes emits empty titles
    "<upnp:class>object.item.audioItem.musicTrack</upnp:class>"
    '<res protocolInfo="http-get:*:audio/L16:*">'
    "http://192.168.1.31:8200/MediaItems/35062020.wav</res></item>"
    '<item id="1$4$3" parentID="1$4" restricted="1">'
    "<dc:title>a video</dc:title>"
    "<upnp:class>object.item.videoItem.movie</upnp:class>"
    '<res protocolInfo="http-get:*:video/mp4:*">http://x/v.mp4</res></item>'
    "</DIDL-Lite>"
)


def test_parse_dms_didl():
    entries = parse_dms_didl(MINIDLNA_DIDL)
    # container + 2 audio items; the video item is filtered out.
    assert len(entries) == 3
    assert entries[0].is_container and entries[0].title == "All Music"
    flac = entries[1]
    assert flac.title.startswith("(Everything I Do)")
    assert flac.artist == "Bryan Adams"
    assert flac.protocol_info == "http-get:*:audio/x-flac:*"
    # Empty-title track falls back to the URL basename.
    assert entries[2].title == "35062020.wav"


def test_build_miyue_didl_roundtrip():
    tracks = [e for e in parse_dms_didl(MINIDLNA_DIDL) if not e.is_container]
    didl = build_miyue_didl(tracks, "MiYue-402")
    assert "<miyue:songSrc>21</miyue:songSrc>" in didl
    assert "<miyue:sourceName>MiYue-402</miyue:sourceName>" in didl
    assert 'protocolInfo="http-get:*:audio/x-flac:*"' in didl
    # Must parse back with our own DIDL parser (device consumes same format).
    parsed = parse_didl(didl)
    assert len(parsed) == 2
    assert parsed[0].song_src == 21
    assert parsed[0].res_uri == "http://192.168.1.31:8200/MediaItems/71106820.flac"


def test_build_miyue_didl_escapes_xml():
    t = DmsEntry(is_container=False, object_id="a&b", title='R&B <Mix> "1"',
                 artist="A&B", url="http://n/a.mp3?x=1&y=2",
                 protocol_info="http-get:*:audio/mpeg:*")
    didl = build_miyue_didl([t], 'NAS "主" & <备>')
    parsed = parse_didl(didl)
    assert parsed[0].title == 'R&B <Mix> "1"'
    assert parsed[0].res_uri == "http://n/a.mp3?x=1&y=2"


def test_find_content_directory_variants():
    svc = {"serviceType": "urn:schemas-upnp-org:service:ContentDirectory:1",
           "controlURL": "/ctl/ContentDir"}
    # dict-of-list and dict-of-single-dict shapes both appear in SSDP caches.
    assert _find_content_directory({"service": [svc]}) == (
        "/ctl/ContentDir", svc["serviceType"])
    assert _find_content_directory({"service": svc})[0] == "/ctl/ContentDir"
    v4 = dict(svc, serviceType="urn:schemas-upnp-org:service:ContentDirectory:4")
    assert _find_content_directory({"service": [v4]})[1].endswith(":4")
    assert _find_content_directory({}) is None


def test_mime():
    assert _mime("http-get:*:audio/x-flac:*") == "audio/x-flac"
    assert _mime("") == "audio/mpeg"
    assert _mime("http-get:*:*:*") == "audio/mpeg"
