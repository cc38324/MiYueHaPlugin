"""Pure-logic tests for songSrc skip gating — no Home Assistant needed.

The expectations mirror the Flutter controller's song_src.dart (canSkip) and
the firmware's GetQueue CurrentIndex sentinel table.
"""

from custom_components.miyue.song_src import (
    SENTINEL_SONGSRC,
    SKIP_SONGSRCS,
    UNKNOWN,
    can_skip,
    effective_song_src,
)


def test_queue_sources_can_skip():
    # local, ximalaya, ergeduoduo, kugou, kugou-browse, netease, NAS/DMS
    for src in (0, 4, 10, 11, 12, 15, 21):
        assert can_skip(src), src


def test_reverse_control_sources_can_skip():
    # Bluetooth (AVRCP) and AirPlay (AP2) skip by reverse-controlling the phone.
    for src in (9, 20):
        assert can_skip(src), src


def test_single_streams_cannot_skip():
    # AUX, SPDIF, DLNA cast-in, douban, radio, lava, netease-FM
    for src in (1, 2, 3, 5, 8, 14, 16):
        assert not can_skip(src), src


def test_sync_slave_can_skip():
    # A Linux slave forwards Next/Previous to its master (whole-group skip);
    # the unsafe Android-slave case is gated by Role in media_player, not here.
    assert can_skip(13)


def test_unknown_source_is_permissive():
    assert can_skip(UNKNOWN)
    assert can_skip(-7)


def test_sentinel_maps_to_owner_songsrc():
    assert effective_song_src(None, -100, None) == 8  # radio
    assert effective_song_src(None, -102, None) == 20  # airplay -> skippable
    assert effective_song_src(None, -106, None) == 9  # bluetooth -> skippable
    assert effective_song_src(None, -105, None) == 13  # sync slave -> gray


def test_transient_sentinels_fall_through_to_track():
    # TTS/voice/intercom overlays keep TrackMetaData's songSrc pinned to the
    # underlying source -- the verdict must not flap during an announcement.
    for idx in (-107, -108, -109):
        assert effective_song_src(None, idx, 8) == 8, idx  # radio stays gray
        assert effective_song_src(None, idx, 15) == 15, idx  # queue stays lit
        assert effective_song_src(None, idx, None) == UNKNOWN, idx


def test_active_external_input_wins():
    assert effective_song_src("AUX", 3, 15) == 1
    assert effective_song_src("SPDIF", 3, 15) == 2
    # Bluetooth input open -> skippable via device-side AVRCP.
    assert can_skip(effective_song_src("Bluetooth", -100, None))


def test_track_src_used_when_queue_in_use():
    assert effective_song_src(None, 3, 15) == 15
    assert effective_song_src(None, 0, 8) == 8  # radio row selected in queue


def test_no_signal_is_unknown():
    assert effective_song_src(None, -1, None) == UNKNOWN


def test_sentinel_table_only_contains_gray_or_reverse_control():
    # Every sentinel owner must map to a src with a deliberate verdict;
    # AirPlay/Bluetooth (phone reverse control) and the Linux sync slave
    # (master forwarding) remain skippable.
    skippable = {s for s in SENTINEL_SONGSRC.values() if s in SKIP_SONGSRCS}
    assert skippable == {20, 9, 13}
