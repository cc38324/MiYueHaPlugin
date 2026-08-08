# MiYue UPnP control contract

Ground truth for this integration. Captured from **live firmware** (M330B
`eng.miyue.20260712`, Android M100 sw 1.0) by dumping `description.xml` + every
SCPD and firing read-only SOAP, cross-checked against the `MiYue`, `Miyuemusic`
and `newMiYueApp/flutter_app` source trees.

## Device model

- Root device: `urn:schemas-upnp-org:device:MultiRoomMusicPlayer:1`, UDN
  `uuid:<factory-uuid>`. Carries the private `urn:miyue-hk:*` services.
- Embedded device: `urn:schemas-upnp-org:device:MediaRenderer:1`, UDN
  `<root-udn>-mr`. Carries the three standard DLNA services.
- `description.xml` is served at **`/description.xml`**; the SSDP `LOCATION`
  header always points at it. **The HTTP port drifts** across reboots (pupnp
  starts at 49495 and climbs to 4950x on FD leaks). Never hardcode it — read
  `LOCATION`, re-resolve on SSDP re-sighting.
- Both firmwares expose the same services, except `MiyueUpdate:1`, which is
  implemented by the **Linux** firmware only (`MYDevice/MYUpnp/MiyueUpdate.cpp`;
  the Android build has no such service) — its presence in `description.xml`
  identifies a Linux/RK3308 device. An earlier revision of this document had
  that discriminator inverted; treat other Linux-vs-Android labels below with
  matching suspicion until re-verified. **Read role/group state from
  `MiyueGroup.GetGroupInfo`, never from description tags.**

## Services & control URLs

| Service | Type | Control URL |
|---|---|---|
| AVTransport | `urn:schemas-upnp-org:service:AVTransport:1` | `/MediaRenderer/AVTransport/Control` |
| RenderingControl | `urn:schemas-upnp-org:service:RenderingControl:1` | `/MediaRenderer/RenderingControl/Control` |
| ConnectionManager | `urn:schemas-upnp-org:service:ConnectionManager:1` | `/MediaRenderer/ConnectionManager/Control` |
| MiyueGroup | `urn:miyue-hk:service:MiyueGroup:1` | `/_control/MiyueGroup` |
| MiyueSystem | `urn:miyue-hk:service:MiyueSystem:1` | `/_control/MiyueSystem` |
| MiyueAudioSource | `urn:miyue-hk:service:MiyueAudioSource:1` | `/_control/MiyueAudioSource` |
| MiyueLibrary | `urn:miyue-hk:service:MiyueLibrary:1` | `/_control/MiyueLibrary` |
| MiyueQueue | `urn:miyue-hk:service:MiyueQueue:1` | `/_control/MiyueQueue` |
| MiyueAlarmClock | `urn:miyue-hk:service:MiyueAlarmClock:1` | `/_control/MiyueAlarmClock` |
| MiyueSensor | `urn:miyue-hk:service:MiyueSensor:1` | `/_control/MiyueSensor` |
| MiyueAccount | `urn:miyue-hk:service:MiyueAccount:1` | `/_control/MiyueAccount` |
| MiyueIntercom | `urn:miyue-hk:service:MiyueIntercom:1` | `/_control/MiyueIntercom` |
| MiyueUpdate (Linux only) | `urn:miyue-hk:service:MiyueUpdate:1` | `/_control/MiyueUpdate` |

Event (GENA) URLs mirror control URLs with `/_event/<Name>` (private) and
`/MediaRenderer/<Svc>/Event` (standard). SOAP header:
`SOAPACTION: "<serviceType>#<Action>"`, `Content-Type: text/xml; charset="utf-8"`.

## Actions used by this integration

**AVTransport** — `Play(InstanceID,Speed)`, `Pause(InstanceID)`,
`Stop(InstanceID)`, `Seek(InstanceID,Unit=REL_TIME,Target=H:MM:SS)`,
`GetTransportInfo → CurrentTransportState {STOPPED,PLAYING,PAUSED_PLAYBACK,TRANSITIONING,NO_MEDIA_PRESENT}`,
`GetPositionInfo → RelTime,TrackDuration`, `SetAVTransportURI(CurrentURI,CurrentURIMetaData)`,
`Next(InstanceID)` / `Previous(InstanceID)`. **Next/Previous are handled by
BOTH firmwares' runtime dispatchers** (the Linux SCPD declares them; the
Android SCPD hides them but answers) and route through the device's single
button arbiter — queue advance with wrap+shuffle, Bluetooth AVRCP / AirPlay
AP2 reverse control, radio/AUX gating — the same path as the physical keys.
**Always prefer them over client-side SeekToTrack index math**; firmware that
predates them answers a SOAP fault → fall back to `MiyueQueue.SeekToTrack`,
but ONLY on a genuine fault (a timeout may mean Next already ran → blind
SeekToTrack double-skips) and only when a queue source is sounding.
(An earlier revision claimed no Next/Previous existed — wrong.)
**Sync slaves differ by firmware**: a Linux slave keeps its renderer up and
forwards Next/Previous to the master (whole-group skip, same as its physical
key) and reports the `-105` sentinel; an Android slave unregisters the whole
MediaRenderer (control URLs 404) and a `SeekToTrack` pushed at it rips it out
of the group and starts its stale local queue — gate every queue write on
`GetGroupInfo.Role`.

**RenderingControl** — `GetVolume/SetVolume(InstanceID,Channel=Master,Desired 0..100)`,
`GetMute/SetMute`. A **slave** hides its renderer and 404s here; read a slave's
volume from the SSDP `X-MIYUE-VOL` header instead.

**MiyueQueue** — `GetQueue(StartIndex,RequestedCount) → Result(DIDL-Lite),
Count, Total, CurrentIndex` is the **universal queue read** (both firmwares).
`CurrentIndex` is the QUEUE index — the same space `SeekToTrack(Index)` takes
(0-based, verified live by title match). `CurrentIndex < 0` means the queue is
not what is sounding **on the Linux firmware**: `-1` idle/none; `<= -100`
owner sentinels (-100 radio, -101 DLNA cast, -102 AirPlay, -103 AUX,
-104 SPDIF, -105 sync-slave, -106 Bluetooth, -107 TTS, -108 voice,
-109 intercom). **Android does NOT project owner sentinels into GetQueue**:
its CurrentIndex is the raw queue index (`b.getIndex()`; only the radio path
writes -100), so a stale POSITIVE index can persist while Bluetooth/AirPlay/
DLNA/a sync group owns playback — never treat a positive CurrentIndex alone
as proof the queue is sounding. Android's owner projection (-100..-106) is
exposed only as `GetTimeline`'s `MusicIndex` out-arg.
`GetTimeline` exists **only on the Android firmware**
(the Linux one answers HTTP 500 / errorCode 501 "Action Failed") and its
CurrentIndex is a TIMELINE position that diverges from the queue index under
SHUFFLE — never use it for control. Other actions: `SeekToTrack(Index)`,
`GetPlayMode/SetPlayMode(PlayMode {NORMAL,REPEAT_ONE,REPEAT_ALL,SHUFFLE})`,
`ReplaceQueue/AppendQueue(Items=DIDL-Lite,...)`, `RemoveTrack`, `RemoveAllTracks`.
`SeekToTrack` silently no-ops (still returning success) on an out-of-range or
negative Index. The DIDL items carry `<miyue:songSrc>`, `<miyue:songId>`,
`<miyue:musicId>` alongside `dc:title` / `upnp:album` / `upnp:albumArtURI`.
**now-playing metadata comes from here**, not AVTransport. Skip capability by
songSrc mirrors the Flutter controller's `song_src.dart` (see `song_src.py`).

**MiyueAudioSource** — `GetExternalInputs → WithAux,OpenAux,WithSpdif,OpenSpdif,WithBluetooth,OpenBluetooth,BluetoothStatus`,
`SelectSource(Source)`, `SetInputOpen(Source,On)`.

**MiyueSystem** — `GetDeviceInfo → Name,Model,SoftwareVersion,IPAddress,Role,GroupID,DeviceUUID,...`,
`SetDeviceName`, `Reboot`, `PlayTTS(Text,Volume)`.

**MiyueGroup** — see below.

## Grouping (MiyueGroup) — mirror model

`GetGroupInfo → Role {Master,Slave,None}, GroupID (28 hex), MasterIp,
MulticastAddr (239.10.h1.h2), MulticastPort/SyncPort/ControlPort, DeviceUUID,
DeviceName`. `GetMemberList` returns only the device itself. There is **no
whole-group query** — poll every device and cluster by `GroupID`.

Topology: `isStandalone := Role=="None" || GroupID=="null"` (**Role==None vetoes
any gid**). Cluster non-standalone devices by `GroupID`; a cluster ≥2 is a
group (leader = the `Master`); a cluster of 1 is standalone.

**Join (A=master, B=slave):**
1. `A.GetGroupInfo` → `gid`, `multicast`, `masterIp` (use A's `MasterIp` if a
   valid IPv4, else fall back to A's host). Abort if gid empty/`null`, multicast
   empty, or masterIp not a valid IPv4.
2. `A.SetGroupConfig(TargetUUID=A, GroupID=gid, MasterIp, MulticastAddr, Role=Master)`
   **first**. The master ignores supplied gid/multicast/ip and self-derives.
3. `B.SetGroupConfig(TargetUUID=B, GroupID=gid, MasterIp, MulticastAddr, Role=Slave)`.
   The slave adopts them; it rejects a non-IPv4 or self-IP masterIp.
4. Wait ~900 ms, **re-poll `GetGroupInfo`** to confirm — SOAP returns success
   even on rejection and applies asynchronously.

**Unjoin:** `LeaveGroup()` (no args) on the leaving device. If that would drop
the group below 2 members, dissolve it — send `LeaveGroup` to **every** member
(master included). Never rely on the device to cascade.

**Invariants:**
- `masterIp` must equal the master's current IPv4; devices self-derive it —
  never push a foreign value. Validate every IPv4 (reject empty / IPv6 /
  hostname / `0.0.0.0` / `255.255.255.255`).
- Never fabricate `GroupID` or `MulticastAddr` — read both from the master. The
  historical `239.10.10.230` default-group collision is why hardcoded multicast
  is forbidden.
- `MiyueGroup.SetVolume` is per-device, not a group fan-out.

**Change signal:** `/_event/MiyueGroup` GENA emits a content-free
`<Event xmlns="urn:miyue-hk:event:MiyueGroup"><InstanceID val="0"><Op val="changed"/></InstanceID></Event>`
ping (+ one on subscribe) — a "re-read me" trigger with no state. SSDP NOTIFY
carries `X-MIYUE-ROLE`/`X-MIYUE-GID`/`X-MIYUE-VOL` as coarse hints only; stale
SSDP must never override a fresh `GetGroupInfo`.

## Browse device-local content (MiyueLibrary) — login-free

All reads hit the device's own SQLite (favorites, local USB/TF library); no
network, no cookies. List calls return **JSON strings**, track calls return
**DIDL-Lite**. Drill-down keys in parentheses:

- `GetCollectedSonglists → Songlists` JSON `[{id,name,count(,icon)}]` → `id`
- `GetSonglistTracks(SonglistId) → Result` DIDL  *(also used for boards)*
- `GetCollectedMusic → Result` DIDL  (Liked / 我喜欢的)
- `GetCollectedRadios → Result` DIDL
- `GetCollectedBoards → Boards` JSON `[{id,name,count}]` → `id`
- `GetLocalCategories(GroupBy=artist|album|folder) → Categories` JSON
  `[{key,name,count,icon}]` → `key` (folder key is the full path)
- `GetLocalFolders → Folders` JSON `[{name,path,count}]` — `path` is the
  `directoryPath` used as an alarm/scene music source, NOT a browse key
  (folder browsing goes through `GetLocalCategories(folder)`); `path` is the
  leaf dir name on Linux but the full parent path on Android
- `GetLocalCategoryTracks(GroupBy,Key,StartIndex,Count) → Result,Total` DIDL
- `GetLocalSongs(StartIndex,Count) → Result,Total` DIDL
- `GetQueue(StartIndex,RequestedCount) → Result,Count,Total,CurrentIndex` DIDL

Local tracks have `<miyue:songSrc>0</miyue:songSrc>`, empty `<res>` (device
resolves the path by `songId`), and cover art at `http://<device-ip>:18181/…`.
Some local ID3 tags are GBK bytes served as latin1 — re-decode conservatively.

## Play a browsed item (MiyueQueue)

`ReplaceQueue(Items=<DIDL>, StartingIndex=N)` **replaces the queue and
auto-plays index N in one call** — no `AVTransport.Play` needed. The `Items`
DIDL can be fed back verbatim from any `Get*Tracks`/`GetCollected*` `Result`.
`<miyue:songSrc>` decides stream resolution: online sources (netease 15, kugou
11, ximalaya 4) re-resolve from `songId` (empty `<res>`); direct-link sources
(ergeduoduo 10, radio 8, DMS 21) need a non-empty `<res>` URL. To play a track
already in the queue, use `SeekToTrack(Index)`.

## Alarms (MiyueAlarmClock)

`ListAlarms → Alarms` (JSON array), `CreateAlarm(Json) → NewAlarmId`,
`UpdateAlarm(Json)` (must include `id`), `DeleteAlarm(AlarmId)`,
`EnableAlarm(AlarmId, Enabled=0|1)`. Alarm object:

```json
{"time":"07:30","recurrence":"1,2,3,4,5","enabled":1,"volume":30,"duration":30,
 "random":0,"isTimeOpen":0,"isWeatherOpen":0,"isTextOpen":0,"isMusicOpen":1,
 "ttsText":"","playOrder":"时间,天气,文字,音乐","musicInfoId":0,
 "songlistInfoId":5,"directoryPath":"","ringName":"…"}
```
- `recurrence`: CSV of weekday digits **0=Sun … 6=Sat**. **Always send an
  explicit set** — an empty string means *one-shot* on Linux but *every day* on
  Android.
- `volume`: 0–100, or `-1` = keep current volume.
- Source is one of `songlistInfoId` / `musicInfoId` / `directoryPath` (a device
  `songlist._id` / collected-track id / local folder), with `isMusicOpen:1`.
- `ringName` is echoed on Linux only.

## Scenes / 情景 (MiyueSensor)

Despite the name this is the **scene store**, not a sensor read. `ListSensors →
Sensors` (JSON array, **no GENA — poll it**), `CreateSensor(Json) → NewSensorId`,
`UpdateSensor(Json)` (with `id`), `ExecuteSensor(SensorId)` (run now),
`DeleteSensor(SensorId)`. Scene object = the alarm fields **plus** `cmdName`,
`cmd` (trigger code — KNX/RS-485/custom), `startHour/Min`–`endHour/Min` window,
`isOpen`, and `textEndType` (**4 = replace queue & play the songlist**; 1 keep /
2 pause / 3 continue / 5 SPDIF). Rules:
- `cmd` **must be unique and non-empty** — the scene map is keyed by it.
- `ExecuteSensor` skips the time window but Linux still honors `isOpen` — only
  expose `isOpen==1` scenes as HA scene entities.

## TTS (MiyueSystem)

`PlayTTS(Text, Volume)` — plain UTF-8, fire-and-forget, **per device** (no
group-announce action; fan out to each member). **Always send `Volume` 1–100**:
on the Linux firmware an empty/0 volume mutes the speaker and is never restored,
and even a valid volume persists after the announcement (music resumes at it).
Android auto-restores; Linux does not.
