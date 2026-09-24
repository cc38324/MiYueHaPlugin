# 米悦 MiYue 快思聪 Crestron 驱动

[中文](#中文) · [English](#english)

---

## 中文

用于米悦(MiYue)背景音乐播放器的快思聪 Crestron 4 系列驱动。通过播放器的 **UPnP/SOAP**
接口控制(MiyueLibrary / MiyueQueue / MiyueAudioSource / MiyueSystem / MiyueSensor / MiyueGroup
+ 标准 AVTransport / RenderingControl)。协议行为与米悦 Control4 驱动、Home Assistant 集成完全一致。

| 组成 | 文件 | 状态 |
|---|---|---|
| SIMPL# 库(全部网络、解析、轮询) | `src/MiYue.Crestron` → `MiYue.Crestron.clz` | **已用 Crestron SDK 2.21.274 编译**,未上真机 |
| 纯逻辑核心(无 Crestron 依赖) | `src/MiYue.Core` | 124 个单元测试通过 |
| 播放器 SIMPL+ 模块 | `simplplus/MiYue Player v1.0.usp` | **未经 SIMPL+ 编译器验证** |
| 列表浏览 SIMPL+ 模块 | `simplplus/MiYue Browser v1.0.usp` | **未经 SIMPL+ 编译器验证** |
| 用户宏 | `macro/MiYue Player.umc`(`gen_umc.py` 生成) | **未验证,且缺少宏参数定义** |

### 未验证的文件(使用前必读)

本仓库是在没有 SIMPL Windows / SIMPL+ 的环境里完成的:

1. **`simplplus/*.usp`** —— 手写,未经 SIMPL+ 编译。必须在 Windows 上用 SIMPL+ 打开、
   `Build > Save and Compile`,修正编译器报出的问题。单元测试只检查了:`#DEFINE_CONSTANT`
   编号与 C# 一致、调用的方法/委托在 C# 外观类中存在、文件为纯 ASCII。
2. **`macro/MiYue Player.umc`** —— 文本格式参照一份真实的 SIMPL Windows 4.02 程序(旧版米悦
   模块)生成,但 **宏参数定义(DefineArguments)的对象格式未知,留空**。用 SIMPL Windows
   打开并另存;若无法打开,按下文「重建用户宏」几分钟即可手工做出。
3. **`MiYue.Crestron.clz`** —— 已用真实 SDK 编译,但 **没有在 4 系列主机和真实播放器上运行过**
   (HTTP 传输、CTimer、SIMPL+ 委托、UTF-16 字符串均未实机验证)。

### 安装

1. 编译 SIMPL# 库(Windows + .NET SDK 8,或 Visual Studio 2022):
   ```
   dotnet build src/MiYue.Crestron/MiYue.Crestron.csproj -c Release
   ```
   输出 `src/MiYue.Crestron/bin/Release/net472/MiYue.Crestron.clz`。
2. 把 `MiYue.Crestron.clz` 与两个 `.usp` 复制到 SIMPL Windows 程序目录(或 User Modules 目录)。
3. 在 SIMPL+ 中打开并编译两个 `.usp`(目标 4 系列)。
4. 在 SIMPL Windows 中加入 `MiYue Player v1.0`(每台播放器一个),需要触摸屏列表时再加
   `MiYue Browser v1.0`(`Player_IP_Address` 填同一台播放器的 IP)。
5. 程序字符串编码:建议程序/模块使用 UTF-16(`Unicode_Text=1`)以显示中文歌名;
   ASCII 编码的程序用 `Unicode_Text=0`(输出 UTF-16LE 字节串,旧版米悦模块的做法)。
6. 调试:`Debug_Mode=1` 时在主机控制台打印 `[MiYue] ...`;错误同时写入 ErrorLog。

运行测试(任何系统,无需设备):
```
cd <驱动根目录 / driver root>   # 独立仓库根目录,或 HA 仓库中的 crestron/
dotnet test tests/MiYue.Core.Tests
```

### 参数

| 模块 | 参数 | 说明 | 默认 |
|---|---|---|---|
| Player | `IP_Address` | 播放器 IP;HTTP 端口 49495..49520 自动探测,记住上次端口 | — |
| Player | `Poll_Interval` | 状态轮询周期(秒,1..60) | 3 |
| Player | `Volume_Step` | Vol_Up/Vol_Down 步进(1..20) | 5 |
| 两者 | `Debug_Mode` | 1 = 控制台诊断输出 | 0 |
| 两者 | `Unicode_Text` | 1 = UTF-16 字符串;0 = UTF-16LE 字节串(ASCII 程序) | 1 |
| Browser | `Player_IP_Address` | 要浏览的 Player 模块的 IP | — |
| Browser | `Page_Size` | 每页行数(1..20),与面板列表行数一致 | 10 |

### 播放器模块信号(MiYue Player v1.0)

**数字输入**(脉冲取上升沿;Vol_Up/Vol_Down/TTS_To_Group 为电平)

| 信号 | 作用 |
|---|---|
| `Play` `Pause` `Play_Pause` `Stop` | AVTransport Play/Pause/Stop(Play_Pause 按当前状态切换) |
| `Next_Track` `Previous_Track` | AVTransport Next/Previous;固件回 SOAP 故障时退回 SeekToTrack(±1,不回绕);收音机/AUX/SPDIF/DLNA 投入时忽略(看 `Can_Skip_Fb`) |
| `Vol_Up` `Vol_Down` | 按下步进一次,按住每 400 ms 继续;音量未知时忽略 |
| `Mute_On` `Mute_Off` `Mute_Toggle` | 静音 |
| `Mode_Normal` `Mode_Repeat_All` `Mode_Repeat_One` `Mode_Shuffle` | 播放模式 |
| `Mode_Cycle` | NORMAL → REPEAT_ALL → REPEAT_ONE → SHUFFLE → NORMAL |
| `Source_Music` `Source_SPDIF` `Source_Bluetooth` `Source_AUX` | `MiyueAudioSource.SelectSource`(Music = 关闭外部输入,不自动续播) |
| `Input_SPDIF_On/Off` `Input_Bluetooth_On/Off` `Input_AUX_On/Off` | `SetInputOpen`(安卓固件异步,可能延迟十几秒,只发一次) |
| `Group_Become_Master` | 本机成为同步组主机 |
| `Group_Leave` | 退出同步组;组内只剩 < 2 台时解散整组 |
| `Group_Dissolve` | 组内每台都 LeaveGroup |
| `Refresh` | 重新发送全部输出并重读设备 |
| `Reconnect` | 重新探测端口 |
| `TTS_To_Group` | 电平:为 1 时 TTS 同时在同步组所有成员上播报 |
| `Scene[1..16]` | 执行设备上第 n 个已启用的情景(名称见 `Scene_Name[n]`) |

**模拟输入**:`Volume_Set`(0..100)、`Volume_Set_Raw`(0..65535)、`Seek_Seconds`(当前曲目内定位,秒)、
`TTS_Volume`(1..100;0 = 用当前音量,当前音量为 0 时用 30)

**串行输入**

| 信号 | 作用 |
|---|---|
| `TTS_Text` | 播报文字(`PlayTTS` 永远带 1..100 的音量) |
| `Play_URL` | 投放流媒体 URL;本机是从机时改投组长 |
| `Group_Join_Master` | 本程序中另一台播放器的 IP:本机作为从机加入它的组 |
| `Group_Add_Slave` | 本程序中另一台播放器的 IP:把它加入本机的组 |
| `Scene_Execute` | 情景 id、名称或触发码 |

**数字输出**

| 信号 | 含义 |
|---|---|
| `Online_Fb` | 已连接 |
| `Playing_Fb` `Paused_Fb` `Stopped_Fb` | 传输状态(TRANSITIONING 保持上一状态) |
| `Muted_Fb` | 静音 |
| `Mode_Normal_Fb` `Mode_Repeat_All_Fb` `Mode_Repeat_One_Fb` `Mode_Shuffle_Fb` | 播放模式 |
| `Can_Skip_Fb` | 上/下一首当前是否有效(按 songSrc 判断,同 HA/Flutter) |
| `Source_Music_Fb` `Source_SPDIF_Fb` `Source_Bluetooth_Fb` `Source_AUX_Fb` | 当前音源 |
| `Has_SPDIF_Fb` `Has_Bluetooth_Fb` `Has_AUX_Fb` | 硬件是否具备该输入 |
| `Group_Master_Fb` `Group_Slave_Fb` | GetGroupInfo 角色 |
| `Grouped_Fb` | 在一个 ≥2 台的组中(或为从机) |
| `Group_Busy_Fb` | 组操作进行中 |

**模拟输出**:`Volume_Fb`(0..100)、`Volume_Raw_Fb`(0..65535)、`Position_Fb`(秒,播放时每秒插值)、
`Duration_Fb`(秒)、`Progress_Raw_Fb`(0..65535)、`Queue_Index_Fb`(1 起;0 = 无/外部音源)、
`Queue_Total_Fb`、`Port_Fb`、`Scene_Count_Fb`、`Group_Member_Count_Fb`、`Bluetooth_Status_Fb`

**串行输出**:`Title_Fb` `Artist_Fb` `Album_Fb` `Cover_URL_Fb`(封面 URL,可直接给面板动态图片)、
`Transport_State_Fb`(PLAYING / PAUSED_PLAYBACK / STOPPED / … / OFFLINE)、`Play_Mode_Fb`、`Source_Fb`、
`Position_Text_Fb` `Duration_Text_Fb`(m:ss)、`Device_Name_Fb` `Model_Fb` `Firmware_Fb` `UDN_Fb`、
`Status_Fb`(连接状态文字)、`Last_Error_Fb`、`Group_Role_Fb` `Group_Master_IP_Fb` `Group_ID_Fb`、
`Group_Members_Fb`(组员名称,组长在前,逗号分隔)、`Scene_Name[1..16]`

### 列表浏览模块信号(MiYue Browser v1.0)

| 类型 | 信号 | 作用 |
|---|---|---|
| 数字输入 | `List_Liked` `List_Songlists` `List_Boards` `List_Radios` `List_Queue` `List_Scenes` | 打开列表(第 1 页) |
| 数字输入 | `Page_Next` `Page_Prev` `Page_First` | 翻页 |
| 数字输入 | `Back` | 从歌单/榜单曲目返回上一级 |
| 数字输入 | `Play_All` | 从第 1 首播放整个列表(队列:回到第 1 首) |
| 数字输入 | `Refresh` | 重新读取当前页 |
| 数字输入 | `Item_Select[1..20]` | 按下本页第 n 行:曲目列表 → ReplaceQueue(整个列表, 该首) 并播放;歌单/榜单 → 打开曲目;队列 → SeekToTrack;情景 → ExecuteSensor |
| 模拟输入 | `Item_Clicked` | 同 `Item_Select[n]`(Smart Graphics 的 Item Clicked) |
| 模拟输入 | `Goto_Page` | 跳到第 n 页(1 起) |
| 数字输出 | `Busy_Fb` `Can_Back_Fb` `Has_Prev_Page_Fb` `Has_Next_Page_Fb` `Player_Found_Fb` | 状态 |
| 数字输出 | `Item_Is_Current[1..20]` | 队列列表中正在播放的行 |
| 模拟输出 | `Page_Fb`(1 起) `Page_Count_Fb` `Items_On_Page_Fb` `Total_Items_Fb` | 分页 |
| 串行输出 | `List_Title_Fb` `Status_Fb` | 标题 / 状态文字 |
| 串行输出 | `Item_Text[1..20]` `Item_Sub[1..20]` `Item_Icon[1..20]` | 行文字 / 副标题(歌手或曲目数)/ 封面 URL |

### 用户宏(MiYue Player.umc)

宏只包装播放器模块;引脚与上表的播放器信号**同名、同顺序**,参数同「参数」表
(`IP_Address`、`Poll_Interval`、`Volume_Step`、`Debug_Mode`、`Unicode_Text`)。

**重建用户宏**(如果 .umc 打不开,或缺少参数定义):SIMPL Windows → File → New User Macro;
在 Logic 中放入 `MiYue Player v1.0`;在 Argument Definition 中按上表顺序添加全部输入、输出和
5 个参数,并与模块同名引脚相连;模块参数填入对应的宏参数(`#IP_Address` 等);保存为
`MiYue Player.umc`。`gen_umc.py` 可在 `.usp` 改动后重新生成文件。

### 触摸屏列表接线示例

面板上放一个 Smart Graphics「Subpage Reference List」,行数 = `Page_Size`(如 10),每行一个按钮、
两行文字、一个动态图片:

```
按钮 List_Liked / List_Songlists / List_Queue ...  → Browser 同名数字输入
列表 "Set Number of Items"   (模拟) ← Items_On_Page_Fb
列表第 n 行 按钮按下          (数字) → Item_Select[n]      或 列表 "Item Clicked" → Item_Clicked
列表第 n 行 文字1            (串行) ← Item_Text[n]
列表第 n 行 文字2            (串行) ← Item_Sub[n]
列表第 n 行 图片 URL          (串行) ← Item_Icon[n]
列表第 n 行 高亮             (数字) ← Item_Is_Current[n]
上一页/下一页按钮 → Page_Prev / Page_Next,可见性 ← Has_Prev_Page_Fb / Has_Next_Page_Fb
返回按钮 → Back,可见性 ← Can_Back_Fb;标题 ← List_Title_Fb;页码 ← Page_Fb / Page_Count_Fb
```

正在播放页:`Title_Fb`/`Artist_Fb` → 文字,`Cover_URL_Fb` → 动态图片 URL,`Progress_Raw_Fb` → 进度条,
`Volume_Raw_Fb` ↔ 音量滑条(滑条输出接 `Volume_Set_Raw`),`Can_Skip_Fb` → 上/下一首按钮可用。

### 与 Control4 / Home Assistant 的功能对照

| 功能 | Control4 | HA | Crestron |
|---|---|---|---|
| 按 IP 发现 + 端口探测、在线状态 | ✔ | ✔(SSDP + 端口扫描) | ✔(49495..49520,上次端口优先,5/15/30/60 s 退避) |
| 播放/暂停/停止 | ✔(无 Stop) | ✔ | ✔ |
| 上/下一首 | SeekToTrack 优先 | AVTransport Next/Previous + 故障回退 + songSrc 置灰 | 同 HA |
| 进度定位 Seek | — | ✔ | ✔ |
| 音量/静音 | ✔ | ✔ | ✔(含按住连续调节) |
| 正在播放(标题/歌手/专辑/封面/进度) | ✔ | ✔ | ✔ |
| 播放模式 | ✔ | ✔ | ✔ |
| 音源 / 外部输入 | SetInputOpen(SPDIF) | SelectSource | 两者都有 |
| 情景(只执行) | ✔ | ✔(scene 实体) | ✔ |
| TTS | ✔ | ✔(可整组) | ✔(可整组) |
| 收藏/歌单/榜单/电台浏览与播放 | ✔ | ✔ | ✔(Browser 模块) |
| 当前队列浏览/跳转 | ✔ | ✔ | ✔ |
| 本地曲库(歌手/专辑/文件夹)、NAS/DMS | ✔ / — | ✔ / ✔ | —(未做) |
| 收藏/取消收藏 | ✔ | — | —(HA 不支持,按要求不做) |
| 同步组 加入/退出 | ✔(hub 驱动) | ✔(group.py) | ✔(HA 语义) |
| 从机投放改投组长 | — | ✔ | ✔ |
| 语音文件播放(旧 TCP 协议) | ✔ | — | —(非 UPnP) |
| 闹钟 / 情景编辑 | — | —(已删除) | — |

### 已知限制

- 同步组只能在**同一程序内**的播放器之间编排(`Group_Join_Master` / `Group_Add_Slave` 填的 IP 必须
  有对应的 Player 模块);组员列表也只统计本程序内的播放器。米悦 App 里组的队能被看到(每台轮询
  `GetGroupInfo`),但不在本程序中的组员不会列出。
- 从机上的播放/暂停等命令发给从机本身(与 HA 一致);只有 `Play_URL` 改投组长。安卓从机的
  `SeekToTrack` 会把它拉出组 —— 因此从机的上/下一首只在 Linux 固件报告 `-105` 时才允许。
- 老固件从机隐藏 MediaRenderer(404):音量/静音读取会暂停 10 个周期后重试,不会判为离线。
- 上次成功的端口只保存在内存中(程序重启后从 49495 起重新探测,最多 26 次、每次 2 s)。
- SOAP 请求不会自动重发(HA 只在连接被复位时重发一次),三次连续传输失败视为离线并重新探测。
- 收藏/取消收藏、本地曲库、NAS、闹钟、情景编辑、语音文件播放未实现。
- 同一时刻每台播放器最多 2 个在途请求;轮询绝不重叠,周期看门狗 60 s、单请求看门狗 16 s。

### 固件差异说明(两份参考实现的分歧)

- **Next/Previous**:C4 契约记录安卓 SCPD 列出、Linux 未列出但可用;HA CONTRACT 则相反。
  本驱动不依赖 SCPD:直接调用,只在真正的 SOAP 故障时退回 SeekToTrack(按 HA)。
- **GetTimeline**:C4 说仅 Linux,HA 说仅安卓 —— 本驱动不使用。
- **MiyueUpdate** 作为平台判别:两份文档结论相反 —— 本驱动不做平台判别。
- **从机渲染器**:C4 说当前固件始终暴露 MediaRenderer,HA 说安卓从机 404 —— 两种情况都兼容。
- **CurrentIndex 哨兵**:HA 说安卓不投射外部音源哨兵 —— 置灰逻辑同 HA,正数索引不单独作为依据。

---

## English

Crestron 4-series driver for MiYue (米悦) background-music players, talking to the player's
**UPnP/SOAP** interface (MiyueLibrary / MiyueQueue / MiyueAudioSource / MiyueSystem / MiyueSensor /
MiyueGroup + standard AVTransport / RenderingControl). The protocol behaviour matches the MiYue
Control4 driver and the Home Assistant integration.

| Part | Files | Status |
|---|---|---|
| SIMPL# library (all networking, parsing, polling) | `src/MiYue.Crestron` → `MiYue.Crestron.clz` | **compiled against Crestron SDK 2.21.274**, never run on hardware |
| Pure-logic core (no Crestron dependency) | `src/MiYue.Core` | 124 unit tests pass |
| Player SIMPL+ module | `simplplus/MiYue Player v1.0.usp` | **not compiled by SIMPL+** |
| Browse SIMPL+ module | `simplplus/MiYue Browser v1.0.usp` | **not compiled by SIMPL+** |
| User macro | `macro/MiYue Player.umc` (made by `gen_umc.py`) | **unverified, argument definition missing** |

### Unverified files (read before use)

This repository was produced without SIMPL Windows or SIMPL+:

1. **`simplplus/*.usp`** were written by hand and never compiled. Open each one in SIMPL+ on
   Windows, run `Build > Save and Compile`, and fix whatever the compiler reports. The unit tests only
   check three things: the `#DEFINE_CONSTANT` numbers match the C# ones, every method and delegate the
   module calls exists on the C# facade, and the files are pure ASCII.
2. **`macro/MiYue Player.umc`** is written in the text format of a real SIMPL Windows 4.02 program
   (the legacy MiYue module), but **the object layout of the macro's argument definition (DefineArguments)
   is unknown, so that part is left empty**. Open the file in SIMPL Windows and save it. If it won't open,
   rebuild it by hand in a few minutes (see "Rebuilding the macro").
3. **`MiYue.Crestron.clz`** compiles against the real SDK, but it **has never run on a 4-series
   processor against real players**. The HTTP transport, CTimer, SIMPL+ delegates and UTF-16 strings are
   all untested on hardware.

### Installation

1. Build the SIMPL# library (Windows with the .NET 8 SDK, or Visual Studio 2022):
   `dotnet build src/MiYue.Crestron/MiYue.Crestron.csproj -c Release`. The output is
   `src/MiYue.Crestron/bin/Release/net472/MiYue.Crestron.clz`.
2. Copy `MiYue.Crestron.clz` and both `.usp` files into the SIMPL Windows program folder (or your User
   Modules folder).
3. Open both `.usp` files in SIMPL+ and compile them for 4-series.
4. In SIMPL Windows, add one `MiYue Player v1.0` per player. If you need touch-panel lists, also add
   `MiYue Browser v1.0` and set its `Player_IP_Address` to that player's IP.
5. String encoding: a UTF-16 program or module with `Unicode_Text=1` shows Chinese titles. For ASCII
   programs, set `Unicode_Text=0`, which sends UTF-16LE byte strings (the legacy MiYue module's technique).
6. Diagnostics: with `Debug_Mode=1` the module prints `[MiYue] ...` lines to the processor console.
   Errors also go to the ErrorLog.

Tests run on any OS and need no device: `dotnet test tests/MiYue.Core.Tests`, run from the driver root (the repo root of MiYueCrestronDriver, or `crestron/` inside the HA repo).

### Parameters

| Module | Parameter | Meaning | Default |
|---|---|---|---|
| Player | `IP_Address` | player IP. The HTTP port is probed from 49495 to 49520, and the last good port is tried first | — |
| Player | `Poll_Interval` | status poll period in seconds (1..60) | 3 |
| Player | `Volume_Step` | step size for Vol_Up/Vol_Down (1..20) | 5 |
| both | `Debug_Mode` | 1 = print diagnostics to the console | 0 |
| both | `Unicode_Text` | 1 = UTF-16 strings; 0 = UTF-16LE byte strings (ASCII programs) | 1 |
| Browser | `Player_IP_Address` | IP of the Player module to browse | — |
| Browser | `Page_Size` | rows per page (1..20). Match your panel list | 10 |

### Player module signals (MiYue Player v1.0)

**Digital inputs.** Pulses act on the rising edge. Vol_Up, Vol_Down and TTS_To_Group are levels.

| Signal | Action |
|---|---|
| `Play` `Pause` `Play_Pause` `Stop` | AVTransport Play/Pause/Stop. Play_Pause toggles based on the current state |
| `Next_Track` `Previous_Track` | AVTransport Next/Previous. On a genuine SOAP fault it falls back to SeekToTrack ±1 without wrapping. Ignored while radio, AUX, SPDIF or a DLNA cast is playing (see `Can_Skip_Fb`) |
| `Vol_Up` `Vol_Down` | one step when pressed, then another every 400 ms while held. Ignored while the volume is unknown |
| `Mute_On` `Mute_Off` `Mute_Toggle` | mute |
| `Mode_Normal` `Mode_Repeat_All` `Mode_Repeat_One` `Mode_Shuffle` | play mode |
| `Mode_Cycle` | NORMAL → REPEAT_ALL → REPEAT_ONE → SHUFFLE → NORMAL |
| `Source_Music` `Source_SPDIF` `Source_Bluetooth` `Source_AUX` | `MiyueAudioSource.SelectSource`. Music closes the external inputs but does not resume playback |
| `Input_SPDIF_On/Off` `Input_Bluetooth_On/Off` `Input_AUX_On/Off` | `SetInputOpen`. Asynchronous on Android and can lag by tens of seconds, so it is sent once |
| `Group_Become_Master` | make this player a sync-group master |
| `Group_Leave` | leave the group. If fewer than 2 players would remain, the group is dissolved |
| `Group_Dissolve` | send LeaveGroup to every member |
| `Refresh` | re-send every output and re-read the device |
| `Reconnect` | probe the port again |
| `TTS_To_Group` | level. While high, TTS also speaks on every member of the sync group |
| `Scene[1..16]` | run the n-th enabled device scene (its name is on `Scene_Name[n]`) |

**Analog inputs:**
- `Volume_Set` (0..100)
- `Volume_Set_Raw` (0..65535)
- `Seek_Seconds` (seek within the current track)
- `TTS_Volume` (1..100). 0 uses the current volume, or 30 if the current volume is 0

**Serial inputs:**

| Signal | Action |
|---|---|
| `TTS_Text` | speak the text. `PlayTTS` always carries a volume of 1..100 |
| `Play_URL` | cast a stream URL. On a slave it is cast to the group leader |
| `Group_Join_Master` | IP of another player in this program. This player joins that player's group as a slave |
| `Group_Add_Slave` | IP of another player in this program. That player joins this player's group |
| `Scene_Execute` | a scene id, name or trigger code |

**Digital outputs:**

| Signal | Meaning |
|---|---|
| `Online_Fb` | connected to the player |
| `Playing_Fb` `Paused_Fb` `Stopped_Fb` | transport state. TRANSITIONING keeps the previous state |
| `Muted_Fb` | muted |
| `Mode_*_Fb` | current play mode |
| `Can_Skip_Fb` | whether next/previous makes sense right now (songSrc gating, as in HA and Flutter) |
| `Source_Music_Fb` `Source_SPDIF_Fb` `Source_Bluetooth_Fb` `Source_AUX_Fb` | active source |
| `Has_SPDIF_Fb` `Has_Bluetooth_Fb` `Has_AUX_Fb` | the hardware has that input |
| `Group_Master_Fb` `Group_Slave_Fb` | role from GetGroupInfo |
| `Grouped_Fb` | in a group of two or more players (or a slave) |
| `Group_Busy_Fb` | a group operation is running |

**Analog outputs:**
- `Volume_Fb` (0..100) and `Volume_Raw_Fb` (0..65535)
- `Position_Fb` (seconds, interpolated every second while playing) and `Duration_Fb` (seconds)
- `Progress_Raw_Fb` (0..65535)
- `Queue_Index_Fb` (1-based; 0 = no queue index or an external source) and `Queue_Total_Fb`
- `Port_Fb`, `Scene_Count_Fb`, `Group_Member_Count_Fb`, `Bluetooth_Status_Fb`

**Serial outputs:**
- Now playing: `Title_Fb`, `Artist_Fb`, `Album_Fb`, `Cover_URL_Fb` (use it as a dynamic-graphic URL)
- `Transport_State_Fb` (PLAYING / PAUSED_PLAYBACK / STOPPED / … / OFFLINE), `Play_Mode_Fb`, `Source_Fb`
- `Position_Text_Fb` and `Duration_Text_Fb` (m:ss)
- Device: `Device_Name_Fb`, `Model_Fb`, `Firmware_Fb`, `UDN_Fb`
- `Status_Fb` (connection text) and `Last_Error_Fb`
- Group: `Group_Role_Fb`, `Group_Master_IP_Fb`, `Group_ID_Fb`, `Group_Members_Fb` (names, leader first, comma-separated)
- `Scene_Name[1..16]`

### Browser module signals (MiYue Browser v1.0)

| Type | Signal | Action |
|---|---|---|
| digital in | `List_Liked` `List_Songlists` `List_Boards` `List_Radios` `List_Queue` `List_Scenes` | open that list at page 1 |
| digital in | `Page_Next` `Page_Prev` `Page_First` | paging |
| digital in | `Back` | from a songlist's or board's tracks back to the list |
| digital in | `Play_All` | play the whole track list from track 1 (for the queue: restart at track 1) |
| digital in | `Refresh` | reload the current page |
| digital in | `Item_Select[1..20]` | press row n of the page. Track lists: ReplaceQueue with the whole list, starting at that track. Songlists/boards: open the tracks. Queue: SeekToTrack. Scenes: ExecuteSensor |
| analog in | `Item_Clicked` | same as `Item_Select[n]` (Smart Graphics "Item Clicked") |
| analog in | `Goto_Page` | go to page n (1-based) |
| digital out | `Busy_Fb` `Can_Back_Fb` `Has_Prev_Page_Fb` `Has_Next_Page_Fb` `Player_Found_Fb` | state |
| digital out | `Item_Is_Current[1..20]` | queue list: the row that is playing now |
| analog out | `Page_Fb` (1-based) `Page_Count_Fb` `Items_On_Page_Fb` `Total_Items_Fb` | paging |
| serial out | `List_Title_Fb` `Status_Fb` | list title and status text |
| serial out | `Item_Text[1..20]` `Item_Sub[1..20]` `Item_Icon[1..20]` | row text, sub-text (artist or track count), cover URL |

### User macro (MiYue Player.umc)

The macro wraps only the player module. Its pins have **the same names and the same order** as the
player signals above. Its parameters are `IP_Address`, `Poll_Interval`, `Volume_Step`, `Debug_Mode` and
`Unicode_Text`.

**Rebuilding the macro** (if the .umc won't open, or has no argument definition):
1. In SIMPL Windows, choose File → New User Macro.
2. Put `MiYue Player v1.0` into Logic.
3. In the Argument Definition, add every input, every output and the 5 parameters, in the order of the
   tables above, and wire each to the module pin with the same name.
4. Set the module parameters to the macro parameters (`#IP_Address` and so on).
5. Save the macro as `MiYue Player.umc`.

After changing the `.usp`, run `python macro/gen_umc.py` to regenerate the file.

### Touch-panel list wiring example

Use a Smart Graphics "Subpage Reference List" with `Page_Size` rows (10, say). Give each row one
button, two text fields and one dynamic graphic, then wire it like this:

```
buttons List_Liked / List_Songlists / List_Queue ...  → Browser digital inputs of the same name
list "Set Number of Items"   (analog)  ← Items_On_Page_Fb
row n button press           (digital) → Item_Select[n]     or list "Item Clicked" → Item_Clicked
row n text 1                 (serial)  ← Item_Text[n]
row n text 2                 (serial)  ← Item_Sub[n]
row n graphic URL            (serial)  ← Item_Icon[n]
row n highlight              (digital) ← Item_Is_Current[n]
prev/next page buttons → Page_Prev / Page_Next, visibility ← Has_Prev_Page_Fb / Has_Next_Page_Fb
back button → Back, visibility ← Can_Back_Fb; title ← List_Title_Fb; page ← Page_Fb / Page_Count_Fb
```

On the now-playing page:
- `Title_Fb` and `Artist_Fb` go to text fields.
- `Cover_URL_Fb` goes to a dynamic-graphic URL.
- `Progress_Raw_Fb` drives a gauge.
- `Volume_Raw_Fb` feeds the volume slider, and the slider output goes to `Volume_Set_Raw`.
- `Can_Skip_Fb` enables the skip buttons.

### Feature parity

| Feature | Control4 | HA | Crestron |
|---|---|---|---|
| discovery by IP + port probe, online status | ✔ | ✔ (SSDP + port sweep) | ✔ (49495..49520, last good port first, 5/15/30/60 s backoff) |
| play / pause / stop | ✔ (no Stop) | ✔ | ✔ |
| next / previous | SeekToTrack first | AVTransport Next/Previous + fault fallback + songSrc gating | as HA |
| seek | — | ✔ | ✔ |
| volume / mute | ✔ | ✔ | ✔ (incl. hold-to-ramp) |
| now playing (title/artist/album/cover/position) | ✔ | ✔ | ✔ |
| play mode | ✔ | ✔ | ✔ |
| sources / external inputs | SetInputOpen (SPDIF) | SelectSource | both |
| device scenes (activate only) | ✔ | ✔ (scene entities) | ✔ |
| TTS | ✔ | ✔ (group fan-out) | ✔ (group fan-out) |
| favorites / songlists / boards / radios browse + play | ✔ | ✔ | ✔ (Browser module) |
| queue browse / jump | ✔ | ✔ | ✔ |
| local library (artist/album/folder), NAS/DMS | ✔ / — | ✔ / ✔ | — (not implemented) |
| collect / uncollect | ✔ | — | — (HA lacks it, so it is excluded) |
| sync groups join / leave | ✔ (hub driver) | ✔ (group.py) | ✔ (HA semantics) |
| cast to the group leader on a slave | — | ✔ | ✔ |
| voice-file playback (legacy TCP) | ✔ | — | — (not UPnP) |
| alarms / scene editing | — | — (removed) | — |

### Known limitations

- **Sync groups only span players in this program.** The IP in `Group_Join_Master` / `Group_Add_Slave`
  needs a matching Player module, and member lists only count players in this program. Groups made in the
  MiYue app are still seen, because every player polls `GetGroupInfo`.
- **Commands on a slave go to the slave itself** (as in HA). Only `Play_URL` is redirected to the
  leader. A `SeekToTrack` rips an Android slave out of its group, so a slave may skip only when the Linux
  firmware reports `-105`.
- **Older firmware hides the MediaRenderer on a slave (404).** Volume/mute reads then pause for 10 cycles
  and retry, and the player is not marked offline.
- **The last good port is kept in memory only.** After a program restart, probing starts again at 49495:
  at most 26 probes of 2 s each.
- **SOAP requests are never re-sent.** HA retries only on a connection reset. Three consecutive transport
  failures mark the player offline and trigger a new probe.
- **Not implemented:** collect/uncollect, local library, NAS, alarms, scene editing, voice-file playback.
- **Request limits:** at most 2 requests in flight per player. Polls never overlap. There is a 60 s cycle
  watchdog and a 16 s per-request watchdog.

### Firmware differences between the two references

- **Next/Previous.** The C4 contract says the Android SCPD lists them and the Linux one doesn't; HA's
  CONTRACT says the opposite. This driver ignores the SCPD, calls them directly and falls back to
  SeekToTrack only on a genuine SOAP fault (as HA does).
- **GetTimeline.** C4 says Linux-only, HA says Android-only. Not used.
- **MiyueUpdate as a platform discriminator.** The references disagree. This driver does not
  discriminate between platforms.
- **Slave renderer.** C4 says current firmware always exposes the MediaRenderer; HA says an Android
  slave returns 404. Both cases are handled.
- **CurrentIndex sentinels.** HA says Android does not project owner sentinels. The skip gating follows
  HA, so a positive index alone is never trusted as proof that the queue is playing.
