"""miyue:songSrc semantics: which sources can skip, and index sentinels.

Canonical table mirrored from the Flutter controller's song_src.dart (the
reference implementation) and the firmware's globals. The device classifies
every playing item by an integer songSrc carried in queue DIDL metadata
(<miyue:songSrc>). Grouped with it here are the GetQueue CurrentIndex
sentinels the firmware emits when the queue is NOT what is sounding.
"""

from __future__ import annotations

from .const import SOURCE_AUX, SOURCE_BLUETOOTH, SOURCE_SPDIF

SONGSRC_LOCAL = 0
SONGSRC_AUX = 1
SONGSRC_SPDIF = 2
SONGSRC_DLNA_CAST = 3  # third-party cast-in (QPlay etc.); device is renderer
SONGSRC_XIMALAYA = 4
SONGSRC_DOUBAN = 5
SONGSRC_RADIO = 8  # FM/qingting live streams -- single stream, not a queue
SONGSRC_BLUETOOTH = 9
SONGSRC_ERGEDUODUO = 10
SONGSRC_KUGOU = 11
SONGSRC_KUGOU_BROWSE = 12  # device-browsed kugou on-demand
SONGSRC_SYNC_SLAVE = 13
SONGSRC_LAVA = 14
SONGSRC_NETEASE = 15
SONGSRC_NETEASE_FM = 16
SONGSRC_AIRPLAY = 20
SONGSRC_NAS_DMS = 21

UNKNOWN = -1

#: Sources the device drives as a true on-demand queue (skip + seek work).
QUEUE_SONGSRCS = {
    SONGSRC_LOCAL,
    SONGSRC_XIMALAYA,
    SONGSRC_ERGEDUODUO,
    SONGSRC_KUGOU,
    SONGSRC_KUGOU_BROWSE,
    SONGSRC_NETEASE,
    SONGSRC_NAS_DMS,
}

#: Sources where next/previous works: real queues; Bluetooth/AirPlay, where
#: the firmware reverse-controls the phone (AVRCP / AP2); and the sync-slave
#: role, where a LINUX slave forwards Next/Previous to its master (the
#: FS_SYNCSLAVE focus token -- whole-group skip, same path as the physical
#: key). Radio, DLNA cast-in and AUX/SPDIF cannot skip. NOTE: an ANDROID
#: slave unregisters its renderer entirely; media_player gates that case by
#: Role + the -105 sentinel, which only the Linux firmware emits.
SKIP_SONGSRCS = QUEUE_SONGSRCS | {
    SONGSRC_BLUETOOTH,
    SONGSRC_AIRPLAY,
    SONGSRC_SYNC_SLAVE,
}

#: GetQueue CurrentIndex <= -100: the queue is not what is sounding. The
#: LINUX firmware projects the playback owner into the value (global.cpp);
#: the ANDROID firmware does NOT project -- its GetQueue returns the raw
#: queue index and only the radio path writes -100, so a stale POSITIVE
#: index can persist there while an external source owns playback (Android's
#: owner projection is exposed only as GetTimeline's MusicIndex out-arg).
SENTINEL_SONGSRC = {
    -100: SONGSRC_RADIO,
    -101: SONGSRC_DLNA_CAST,
    -102: SONGSRC_AIRPLAY,
    -103: SONGSRC_AUX,
    -104: SONGSRC_SPDIF,
    -105: SONGSRC_SYNC_SLAVE,
    -106: SONGSRC_BLUETOOTH,
}

#: The Linux slave sentinel; media_player uses it to tell a Linux slave
#: (renderer up, Next forwards to master) from an Android one (renderer 404).
SENTINEL_SYNC_SLAVE = -105

_SENTINEL_MAX = -100


def can_skip(song_src: int) -> bool:
    """Whether next/previous makes sense for this source (unknown -> yes)."""
    return song_src < 0 or song_src in SKIP_SONGSRCS


def effective_song_src(
    active_input: str | None,
    queue_index: int,
    track_src: int | None,
) -> int:
    """Best-effort songSrc of what is sounding right now (UNKNOWN if unclear).

    Precedence: an OPEN external input (AUX/SPDIF/Bluetooth) overrides
    everything; then a queue-not-in-use sentinel index names the owner; then
    the current track's own tag. Unknown stays permissive -- graying a button
    that would work is worse than leaving one that no-ops (the Flutter
    controller's src<0 rule).
    """
    if active_input == SOURCE_AUX:
        return SONGSRC_AUX
    if active_input == SOURCE_SPDIF:
        return SONGSRC_SPDIF
    if active_input == SOURCE_BLUETOOTH:
        return SONGSRC_BLUETOOTH
    if queue_index <= _SENTINEL_MAX:
        src = SENTINEL_SONGSRC.get(queue_index)
        if src is not None:
            return src
        # Unmapped sentinel (-107..-109 TTS/voice/intercom overlays): the
        # firmware keeps TrackMetaData's songSrc pinned to the UNDERLYING
        # source while the overlay plays, so falling through to the track
        # tag preserves the pre-overlay verdict -- no button flapping in
        # either direction during a doorbell/TTS announcement.
    if track_src is not None:
        return track_src
    return UNKNOWN
