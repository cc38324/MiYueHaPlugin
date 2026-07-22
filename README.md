# MiYue Multi-Room Speakers — Home Assistant integration

Control MiYue smart speakers (M100 / M210B / M310B / M320B / M330B / M500B …)
from Home Assistant over their native UPnP interface. Works with **both** MiYue
firmware generations (the Android `Miyuemusic` app and the headless Linux
`MiYue` firmware) — they expose a byte-identical private UPnP surface.

No cloud, no extra hardware, **no native code** — pure-Python, LAN-local.

---

## 使用说明（中文）

### 安装

**方式一：HACS（推荐）**
1. HACS → 集成 → 右上角 ⋮ → **自定义存储库** → 填本仓库地址，类别选 *Integration*；
2. 搜索安装 **MiYue Multi-Room Speakers**，重启 Home Assistant。

**方式二：手动**
把 `custom_components/miyue/` 整个目录拷到 HA 配置目录的
`config/custom_components/` 下，重启 HA。

要求：Home Assistant **2026.7+**。无需安装任何额外 Python 包。
HA 所在机器必须和音箱在**同一局域网**（依赖 SSDP 组播发现；Docker 部署须
`network_mode: host`）。

### 添加音箱

- **自动发现**：重启后到「设置 → 设备与服务」，局域网里的米悦音箱会以发现卡片
  出现，逐个点「添加」确认即可。
- **手动添加**：同页面 →「添加集成」→ 搜 MiYue → 填音箱 IP（端口留空会自动在
  49495–49520 范围探测）。
- 音箱重启后 UPnP 端口会变，**不用管**：集成以 UDN 识别设备，SSDP 通告 +
  主动端口扫描双路自恢复，约 10 秒内自动重连（日志可见 `self-recovered`）。

### 播放控制（media_player 实体）

每台音箱一个 `media_player.<房间名>` 实体：
- 播放 / 暂停 / 停止 / 上一曲 / 下一曲 / 进度拖动；
- 音量、静音；随机、单曲/列表循环；
- **音源切换**：Music（网络播放）/ AUX / SPDIF / 蓝牙（按硬件实际有的显示）；
- now-playing 卡片显示曲名、歌手、专辑、封面、进度（电台/蓝牙等直播源同样支持）。

### 浏览并播放音箱里的内容（免登录）

点播放器卡片的「**浏览媒体**」：

```
收藏 Favorites            本机/U盘 Local           当前队列 Queue
├─ 我喜欢的                ├─ 歌手                   （点任意一首直接跳播）
├─ 电台                    ├─ 专辑
├─ 歌单 → 曲目             ├─ 文件夹
└─ 榜单 → 曲目             └─ 全部歌曲
```

- 点任意**歌单/分类**上的播放键 = 整列表替换队列并开播；点**单曲** = 从那首开始播；
- 全程不需要在 HA 里登录任何音乐账号——浏览的是音箱本地数据，取流由音箱自己完成；
- 收藏电台若是老版本收藏（缺直链），集成会自动补蜻蜓 FM 直链，安卓音箱也能播。

### 多房间分组

播放器卡片右上角的**分组图标**（Sonos 同款交互）：勾选要加入的音箱 → 组建完成后
`group_members` 属性可见成员列表（主机排第一）；取消勾选即退组。对整组发
TTS/音量操作时按成员逐台生效。

### 情景（button 实体）

音箱上每个**启用中**的情景自动成为一个按钮实体（设备页可见），按一下即触发
（等同手机 App 的「测试」）。设备上删掉的情景会自动从 HA 消失。
按钮属性里有 `scene_id`（删除服务要用）和触发码 `cmd`。

### 闹钟（switch 实体）

音箱上每个闹钟自动成为一个开关实体，名字形如「闹钟 07:30 #3」（#后为 id）；
开/关即启用/停用。属性里有 `alarm_id`、时间、星期、音量、歌单等完整信息。

### 服务（开发者工具 → 动作，或写进自动化）

| 服务 | 说明 |
|---|---|
| `miyue.play_tts` | 语音播报。`message` 必填；`volume` 留空=各音箱用自己当前音量（推荐，播完音量不变）；`announce_group: true`（默认）分组时全组齐播 |
| `miyue.create_alarm` | 建闹钟：`time`("07:30")、`days`(留空=每天)、`volume`、`songlist_id`(歌单id，留空=纯播报)、`tts_text` |
| `miyue.delete_alarm` | 按 `alarm_id` 删（id 见闹钟开关属性/名字） |
| `miyue.create_scene` | 建情景：`name`、`songlist_id`、`volume`、`tts_text`、`cmd`(触发码，留空自动生成) |
| `miyue.delete_scene` | 按 `scene_id` 删（id 见情景按钮属性） |

自动化示例——门铃响了全屋播报：

```yaml
automation:
  - alias: 门铃播报
    trigger:
      - platform: state
        entity_id: binary_sensor.doorbell
        to: "on"
    action:
      - action: miyue.play_tts
        target:
          entity_id: media_player.ke_ting
        data:
          message: 门口有人按门铃
```

再如——工作日早上用「晨跑」歌单叫醒：

```yaml
      - action: miyue.create_alarm
        target:
          entity_id: media_player.zhu_wo
        data:
          time: "07:00"
          days: [mon, tue, wed, thu, fri]
          volume: 35
          songlist_id: "9"        # 歌单 id 可在浏览媒体里查看
```

### 注意事项

- 每台音箱浏览到的是**它自己**的曲库/收藏（U盘插哪台就在哪台下面），不是全屋聚合；
- 安卓固件的本机歌曲暂无封面（固件图床未实现，手机 App 同样没有）；
- Linux 固件播完 TTS 会自动续播刚才的音乐，安卓固件不续播（固件差异）；
- TTS 若显式指定音量，Linux 音箱播完后音量会停留在该值（所以推荐留空跟随当前）；
- 本机歌超过 2000 首的库会截断加载（日志有提示）。

---

## Features

- **Discovery** via SSDP (`MultiRoomMusicPlayer:1` / `MiyueGroup` service), plus
  manual add by IP. Survives the speakers' habit of changing UPnP port on every
  reboot — identity is the UDN, and the stored location is rewritten whenever
  SSDP re-sights the device at a new port.
- **Playback**: play / pause / stop / next / previous / seek, shuffle & repeat.
- **Now playing**: title, artist, album, cover art, progress. (Metadata is read
  from the private `MiyueQueue` service — the standard AVTransport reports only
  `local://current`.)
- **Volume & mute** (RenderingControl).
- **Source select**: network audio ("Music") plus whatever external inputs the
  hardware has — AUX / SPDIF / Bluetooth.
- **Multi-room grouping** mapped to Home Assistant's native
  join/unjoin (`group_members`) — group speakers together like Sonos.
- **Browse & play device-local content** (`media_browser`) — **no login required**:
  the integration browses what the speaker already holds and tells it to play;
  the device handles all account/streaming itself.
  - **Favorites**: liked songs, radios, collected songlists, chart boards.
  - **Local / USB**: by artist, album, folder, or all songs (paged up to 2000
    tracks; a warning is logged if a library exceeds the cap).
  - **Current queue** — jump to any track.
  - Garbled GBK ID3 tags from **local files** are auto-repaired
    (`ÒôÀÖÈÈËÑ` → `音乐热搜`) without touching real accented names (Björk stays
    Björk); device-hosted cover art is proxied through HA so it loads off-LAN.
- **Scenes (情景)** — each enabled device scene becomes a **button**; press to
  run it (`ExecuteSensor`). Create/delete from HA services; scenes deleted on
  the device are pruned from HA automatically. The `scene_id` for the delete
  service is shown as a button attribute.
- **Alarms (闹钟)** — each alarm becomes a **switch** (enable/disable); create
  and delete from HA services; deleted alarms are pruned automatically. The
  `alarm_id` is shown in the switch name and attributes.
- **TTS announcements** — `miyue.play_tts` makes a speaker speak, for automation
  linkage; announces to every member of a group.
- **Cast a URL** via `media_player.play_media`.

### Services

| Service | What it does |
|---|---|
| `miyue.play_tts` | Speak a message on a speaker (and its group), at a set volume |
| `miyue.create_alarm` / `miyue.delete_alarm` | Manage device alarms |
| `miyue.create_scene` / `miyue.delete_scene` | Manage device scenes (情景) |

> **Login-free by design.** This integration never signs in to NetEase / Kugou /
> Ximalaya. It only browses device-local content (favorites, local files, the
> queue) and asks the speaker to play items it already knows; the speaker
> resolves the stream with its own account. Online cloud browsing that would
> need a login is intentionally out of scope.

## Install

### HACS (recommended)
1. HACS → Integrations → ⋮ → *Custom repositories* → add this repo as an
   *Integration*.
2. Install **MiYue Multi-Room Speakers**, then restart Home Assistant.
3. Speakers are auto-discovered (Settings → Devices & Services). Or add one
   manually by IP.

### Manual
Copy `custom_components/miyue/` into your HA `config/custom_components/` and
restart.

Requires Home Assistant **2026.7+** (Python 3.14). No extra Python packages.

## How it works (architecture)

```
                       Home Assistant
   media_player entity ──┬── coordinator (poll ~3s): transport/volume/queue/group/source
   scene buttons ────────┤
   alarm switches ───────┴── aux coordinator (poll ~30s): scenes + alarms
                         │
                         ├── standard MediaRenderer  ── AVTransport / RenderingControl
                         │        (play/pause/seek/volume, transport state)
                         │
                         └── private urn:miyue-hk services (raw SOAP, soap.py)
                                  MiyueGroup       → grouping (mirror model)
                                  MiyueQueue       → now-playing, play mode, next/prev, enqueue
                                  MiyueLibrary     → browse favorites + local library
                                  MiyueAudioSource → source select
                                  MiyueAlarmClock  → alarms (switches + services)
                                  MiyueSensor      → scenes 情景 (buttons + services)
                                  MiyueSystem      → device info, TTS
```

**Grouping is a mirror model.** No device knows the whole group; each reports
its own `Role` / `GroupID` / `MasterIp` via `MiyueGroup.GetGroupInfo`. The
integration polls every speaker and clusters by `GroupID` (a device reporting
`Role=None` is standalone even if it still carries a group id). Joining fans
`SetGroupConfig` out **master-first**, reading the master's self-derived
group id / multicast / IPv4 and never fabricating them; because those SOAP
actions apply asynchronously and return success even on rejection, every
mutation is re-verified by re-polling after a short settle. Unjoin sends an
explicit `LeaveGroup` to every affected member (the firmware never cascades a
teardown reliably).

The full reverse-engineered wire contract is in
[`docs/CONTRACT.md`](docs/CONTRACT.md).

## Status & roadmap

**v0.2 (this release)** — pure verified SOAP over polling coordinators
(`iot_class: local_polling`). Device interactions were validated against live
hardware (two firmware generations). Working: discovery, transport, volume/mute,
now-playing, source select, grouping, login-free browse & play, scene buttons,
alarm switches, TTS service.

**Planned**
- **GENA push** (`local_push`): subscribe to AVTransport/RenderingControl
  `LastChange` for instant transport/volume state, keep polling as fallback.
- Scene/alarm **editor UI** (currently create/delete via services).
- Paging beyond 500 tracks for very large local libraries.

## Firmware differences handled

Both firmware generations expose the same private services; the integration
normalizes the known divergences (read role/group from `GetGroupInfo` not the
description; alarm `recurrence` is sent explicitly because empty means one-shot
on Linux but daily on Android; TTS `volume` is always sent ≥1 because the Linux
firmware mutes and never restores on an empty volume; local cover art and the
optional `icon` field differ). See [`docs/CONTRACT.md`](docs/CONTRACT.md).

## License

See repository.
