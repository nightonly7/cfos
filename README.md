# cFos 皮肤网速悬浮窗

> 一个只有 300 KB 的绿色单文件小工具，在桌面上复刻 cFosSpeed 的网速悬浮窗。
> 它不自己画界面 —— 直接加载 cFosSpeed 原版的 `net.ini` 皮肤引擎，用原版的位图、原版的帧动画、原版的字体渲染。

| Liquid Crystal History — 九段历史柱 | Liquid Crystal — 单条长柱 |
| :---: | :---: |
| ![Liquid Crystal History](screenshots/lc_history.png) | ![Liquid Crystal](screenshots/liquid_crystal.png) |
| `lc_history` · 9 段逐段保留历史采样，带 `alpha_val`(55→230) 的由暗到亮渐变 | `liquid_crystal` · 单条长柱，`historysize=1`，只保留当前值，视觉上更"压得住" |

两张都是本程序 8 倍最近邻放大后的真实渲染输出（`--shot` 离线渲帧，936×488），不是 cFosSpeed 的截图。图里的 161K / 121K 速率、30ms 延迟、2 个连接数、亮起的 Ping 指示灯，都是实时采样会呈现的样子。

---

## 特性速览

| | |
| --- | --- |
| **真透明** | 分层窗口（`UpdateLayeredWindow` + premultiplied BGRA），透出真实桌面，不是黑底遮罩 |
| **Skin 引擎** | 完全按 `net.ini` 描述渲染：定位、帧序、位图字体、`mod` 循环、`method=history` 平滑，没有一处硬编码坐标 |
| **单文件** | 两个皮肤目录压进 exe 作资源，裸跑不用带任何文件夹；也支持往 exe 旁丢新皮肤目录而不重编译 |
| **常驻托盘** | 单实例互斥体、全局热键、鼠标穿透、开机自启，全都可用 |
| **DPI 干净** | PerMonitorV2 感知 + 只允许 1x / 2x / 3x 整数倍最近邻缩放，像素不糊 |

---

## 快速开始

### 直接运行

```
build\CfosWidget.exe
```

右下角出现悬浮窗：无边框、真透明、总在最前。程序常驻托盘。

### 从源码编译

```
build.bat
```

用系统自带 .NET Framework 4.x 的 `csc.exe`，不需要装任何 SDK。

### 离线渲一帧

```
CfosWidget.exe --shot out.png liquid_crystal 0.6 8
```

参数依次是：输出路径 / 皮肤名 / 流量强度（0~1）/ 放大倍数（1~16，默认 4）。

放大走最近邻插值，像素边缘是硬的，不会有双线性那种糊边。README 顶部那两张对比图就是 `8` 倍渲出来的（936×488），原始一帧是 117×61。

---

## 界面上这几个数字分别是什么

| 位置 | 读数 | 含义 |
| --- | --- | --- |
| 左上 | `↑↓` 箭头 + `30ms` | **Ping 指示灯组**。每次探测亮起、然后平滑渐隐到彻底消失，下次探测再亮。持续不亮 = 网络不通 |
| 右上 | 蓝色小方块 | **连接数图标**。按数量切 `tcp0..tcp3.png`：1–5 用 `tcp1`，6–50 用 `tcp2`，>50 用 `tcp3` |
| 上排 | `30ms` | **延迟**，单位毫秒。默认 ping 223.5.5.5，可在 ini 里改 `ping=` |
| 右上 | `2` | **连接数**。与 cFosSpeed `cons` 同源，即它正在流控的连接，**不是**系统 socket 总数 |
| 中排 | `161K` | **下载速率**。单位自带：`K`=KB/s、`M`=MB/s、`G`=GB/s |
| 下排 | `121K` | **上传速率**，单位同上 |

**数字精度**按量级走：小于 100 保留一位小数（`5.7M`、`50.0K`），大于等于 100 取整（`139K`、`100M`），GB 级两位小数（`1.50G`）。数字框放不下时从最低位往左裁，但**保留单位**：`161.0K` → `161K` → `61K`。

单位 K/M/G 用的是 cFos 位图字体自带的字形（`fontbitmapchars=0 123456789KMGT.`），不是另画的小字。

---

## 托盘右键菜单

| 菜单项 | 说明 |
| --- | --- |
| **网卡选择** | 每次打开菜单都重新枚举，USB 网卡 / WiFi 开关 / VPN 热插拔自动出现，不用重启。未连接的标 `⊘`，末尾附「重新扫描网卡」 |
| **皮肤切换** | `skins\` 下任意皮肤目录，切换即时生效 |
| **鼠标穿透** | 打开后鼠标事件穿到下层窗口，悬浮窗不再挡点击。快捷键 **Ctrl+Alt+W** |
| **缩放** | 100% / 200% / 300%，只用整数倍 |
| **动画刷新** | 40 / 60 / 100 / 200 ms —— 柱子与波纹的采样间隔 |
| **数字刷新** | 150 / 240 / 400 / 800 / 1000 ms —— 纯数字的更新节奏 |
| **Ping 间隔** | 500ms / 1 / 2 / 3 / 5 / 10 / 30 秒，改完立即补测一次，不空等 |
| **Ping 目标** | baidu / 腾讯 DNS / 阿里 DNS / Cloudflare / Google，换完立即补测 |
| **Ping 图标随探测闪烁** | 默认开。图标是"刚测过"的指示灯；关掉则只要有读数就常亮 |
| **总在最前 / 锁定位置 / 开机自启动** | 独立开关 |
| **隐藏到托盘 / 退出** | 双击托盘图标也可显隐 |

动画与数字两档都是**按时间插值**而非按帧：40ms 和 200ms 的手感一致，只是采样密度不同。

窗口本身左键可拖动（未锁定、未穿透时），右键也能直接弹出同一套菜单。

---

## 鼠标穿透：一个单向门

`WS_EX_TRANSPARENT` 一旦生效，窗口就收不到任何鼠标消息 —— 包括点自己菜单的那一下。所以做了三条退路：

| 退路 | 行为 |
| --- | --- |
| **全局热键** | 默认 `Ctrl+Alt+W`，任意窗口下生效。注册失败（错误 1409 = 被占用）会自动按 `Ctrl+Alt+P` → `Ctrl+Alt+F10` → `Ctrl+Shift+F10` 依次重试，菜单里显示实际生效的那个 |
| **托盘图标** | 右键永远可点。穿透状态下图标变橙并加一道斜杠，悬停文字变成「网速悬浮窗（穿透中 · Ctrl+Alt+W）」 |
| **气泡提示** | 每次切换都弹一次，告诉你当前状态和怎么关掉 |

热键用 `RegisterHotKey` + `WM_HOTKEY` 实现，带 `MOD_NOREPEAT` 防长按连发，退出时 `UnregisterHotKey` 释放。

---

## 单实例：双击 exe 等于"切到悬浮窗"

启动先抢 `Local\CfosWidget.SingleInstance` 互斥体。抢不到就把事件 `Local\CfosWidget.Show` 置位后退出，已在运行的那个收到事件会把窗口叫回前台。所以永远不会叠出第二个托盘图标、第二个热键、第二个分层窗口。

### 右键卡顿的三个来源，逐个掐掉

| 原因 | 现象 | 处理 |
| --- | --- | --- |
| `spd cons` 同步调用（最多阻塞 1200ms）跑在 UI 线程 | 一右键就僵住，网络忙时更明显 | 探测挪到工作线程，`Sample()` 只读缓存 |
| `GetAllNetworkInterfaces()` 每次开菜单都枚举（VPN/网卡变动后 100–300ms） | 菜单弹出前明显停顿 | `ListNicsCached()` 用后台快照，菜单只读内存 |
| 10ms 定时器无条件 `UpdateLayeredWindow`（约 100 次/秒） | 菜单跟手差、拖动选择发涩 | 计数器没前进就跳过重绘；菜单 `Opened`/`Closed` 期间直接停表 |

顺带修的两处泄漏：托盘图标不再每次状态变化就重建（原来每切一次漏一个 HICON + 一个 GDI 位图，现在 `GetHicon()` 配 `DestroyIcon`）；右键菜单原来 `OnDown` 和 `Opening` 各建一次整棵树，现在只建一次。

**实测**（本机 16 核）：单实例生效（第二个进程 3 秒内自行退出，`liveCount=1`）；UI 线程消息泵 p50 ≈ 0.5ms、p90 ≈ 11ms、峰值 ≈ 16ms；空闲 CPU 单核约 13%；句柄数稳定不增长。

---

## 皮肤引擎做了什么

完全按 `net.ini` 描述渲染，不是硬编码坐标：

1. `[all]` 的 `transparentcolor`（品红抠色）、`background2=glass.png`
2. `[num_chars]` 位图字体：`frameheight`、`fontbitmapchars=0 123456789KMGT.`、`char_width`（`2:7 M:10 .:6`）
3. `[disp6]/[disp7]` → `hdisp1` 定位速率条；`[disp8]/[disp9]` 数字框右对齐（`format=r`），超框自动截位
4. `[disp2]/[disp3]` 数据流方块：`rx_bullet.png` 是 **14 帧往返滑动**（逐帧实测 blockTop 8→1→7），按 `mod=14` 循环，常驻不闪
5. `[shape_disp]` ripple：空闲停 `idleframe`，有流量按强度加速
6. `[disp21..24]` 连接数图标按阈值切 `tcp0..tcp3.png`，并各读自己的 `idleframe`（这组是 2 帧，帧 0 空白、帧 1 才是蓝色三角+方框）
7. history 皮肤的 `alpha_val`（55→230）逐段还原

---

## 深入：那两个小方块

### 官方文档怎么定义的

来源：cFosSpeed 官方皮肤文档（atlas-cfosspeed.com/documentation），原文：

> The `rx_data_cnt` parameter is a **simple counter** (meaning it will increase continuously while cFosSpeed is running), we'll need to perform a `mod`(ulo) calculation to keep the value within the range of `min` and `max`.
> The `bitmap` contains 14 `frames` to comprise a complete **up-and-down cycle that can be run indefinitely**.

对应 ini：

```ini
[disp1]
value=rx_data_cnt
method=animation
rect=44,41,55,59
bitmap=rx_bullet.tga
mod=14
min=0
max=13
frames=14
```

四个结论：

1. `rx_data_cnt` 是**单调递增的计数器**，不是速率、不是字节数
2. `mod=14` 把它映射到 14 帧，形成**无限循环的上下往返动画**
3. 两个方块分别绑 `rx_data_cnt` 和 `tx_data_cnt` —— **两个独立计数器**，这就是它们节奏不同步的原因
4. 动画速度只取决于计数器自增快慢，与当前速率无直接关系

按 `animation` 方法定义，帧号是 `frame = ((value + offset) / div) mod n`，再按 `min`/`max` 与 `frames` 映射。

### 14 帧的实际形态

逐帧实测方块在 11×18 格内的垂直位置：

```
frame  0  blockTop=8      frame  7  blockTop=1
frame  1  blockTop=7      frame  8  blockTop=2
frame  2  blockTop=6      frame  9  blockTop=3
frame  3  blockTop=5      frame 10  blockTop=4
frame  4  blockTop=4      frame 11  blockTop=5
frame  5  blockTop=3      frame 12  blockTop=6
frame  6  blockTop=2      frame 13  blockTop=7
```

一次「上浮—下沉」往返，幅度 7 像素。原版一直保持这个循环，**空闲时也不冻结、不变暗**（实测 200 次采样亮度恒定在 101.4–102.2）。

实现要点：

- 帧位置用浮点，渲染时对相邻两帧交叉淡入（`BlendCycle`），消除硬切台阶
- 推进速率由吞吐**对数映射**而来（`log10(1 + KB/s) * 2.5`，封顶 24 fps），48 KB/s 以下视为背景噪声、方块静止
- 增益是对着原版实测调的：同等负载下原版 span=1~3 像素、无 >1px 跳变。最初设 5.4 时 span=7、26 次大跳，明显过冲
- 恒定 alpha 1.0，不做显隐渐变 —— 原版就是一直亮着

### 两种驱动模式

菜单 → **数据流方块驱动**，可切换：

| 模式 | 行为 | 依据 |
| --- | --- | --- |
| **自由循环**（默认） | 固定 22.5 帧/秒连续滚动，与流量无关 | 实测原版空闲时也每秒变化 22.5 次（171 次 / 7.6 秒），是自由循环动画 |
| **按流量驱动** | 48 KB/s 以下静止，之上按 `log10` 加速，封顶 22.5 fps | 视觉上更"有意义"，但不还原原版 |

因为 22.5 fps 高于主重绘时钟（100ms = 10fps），方块用**独立的 30ms 定时器** `bulletTimer`，只推进方块位置并单独重绘。

早期用"移动范围"（span）判断是错的：原版 span 只有 1–3 像素，但变化频率高达 22.5 次/秒 —— 是快速小幅抖动，不是缓慢漂移。

### 节奏模型：实测重建的"爆发—驻留"

连续 300 次采样（20ms 间隔）记录原版空闲形态：

```
8,10,10,10,10, 9,9,9...(24 个), 6,6,5,5, 7,7,7,7,7,7,7, 8,8,8...(20 个)
```

即：**在一个位置稳定停留 400–600ms，然后一次跳 2–3 格**，整体变化率只有 14%。不是匀速滚动，而是爆发与驻留交替。

用加权随机停留时长复现：62% 长驻留（380–700ms）、26% 中等（120–300ms）、12% 连续快跳（30–90ms）。

踩过的坑：`stepRx`/`stepTx` 是字段，每 tick 用完没清零，导致它们恒为 1、每个 tick 都推进，驻留逻辑被完全吃掉。加 `stepRx = 0` 复位后节奏才对。

```
原版  ：changeRate 14%   8,10,10,10,10,9x24,6,6,5,5,7x7,8x20
本程序：changeRate  9%   5,5,5,5,5,5,5,5,5,4,4,4,4,4,3,3,3,3,3,3,3,3,4,4...
```

### 两条流各走各的

同时录制原版 rx/tx 方块（150 帧，25ms 间隔）做变化相关性分析：

```
both=17  onlyRx=6  onlyTx=90  stable=36
```

| 流 | 变化率 | 平均驻留 | 最长停留 |
| --- | --- | --- | --- |
| RX（下载，橙） | 18% | 5.0 帧 | 11 帧 |
| TX（上传，绿） | 73% | 1.3 帧 | 3 帧 |

下载是「停得久、偶尔跳」，上传是「一直在碎跳」。之前两条流共用一套 dwell 定时器导致同步跳动，现在各自持有 `dwellRx`/`dwellTx` 与独立分布参数。调参后实测 RX 14%/6.8 帧、TX 62%/1.6 帧，与原版比例一致。

### 频率标定：为什么 RX 无法精确复刻

同一套探针连测 10 轮，记录原版方块在约 7 秒内的位置变化次数：

```
ORIG RX: 44, 90, 142, 42, 48, 37, 34, 55, 150, 140
         最小 34，最大 150，波动 4.4 倍
ORIG TX: 137-156，非常稳定
```

- **TX 是稳定的快节奏**，约 20 次/秒，固定参数即可复现 → 已对齐（135–141 vs 144–146）
- **RX 本身波动达 4.4 倍**，同一台机器同一时刻前后两次能差 3 倍以上。它不是固定振荡器，没法用一组参数精确复刻每一刻

当前取原版实测区间的中下部（约 7 次/秒，48–51 次每 7 秒），落在 34–150 的正常范围内。试过的方向：

| 尝试 | 结果 |
| --- | --- |
| 按 `shownRx` 速率线性调节 | 本机长期多 MB/s，导致 RX 恒定过快（113 vs 34） |
| 按速率分档 | 同样问题，原版多 MB/s 时仍只有 34 次 |
| 固定快节奏 | 超过原版（121 vs 42） |
| **固定中低速** | 落在区间内（48–51），**最终选择** |

### 长条为什么几乎不动

长条用 `history` 方法。官方原文：

> The parameter here may change very rapidly, which could cause the animation to behave slightly erratically. So, we'll have to smooth things out a bit. This can be accomplished with the `history` method.

```ini
[disp6]
value=rx_speed
method=history
updatetime=240
historysize=1
hdisp1=rx_speed_disp
```

- `updatetime` = 每多少毫秒采一次均值，长条的时间常数来源
- `historysize` = 保留几个历史值（Liquid Crystal 是 1，History 是 9）
- `hdisp1..N` = 每个历史值用什么方法画

这解释了原版长条几乎不动：`updatetime=240` 本身就慢，再叠上 `rx_speed` 已被驱动平滑，长条自然成了慢变量。

### 柱子伸缩幅度：两级平滑都太快

实测 150 帧条宽（每 30ms 一帧）：

```
原版  ：4,4,4,4,... (150 帧全为 4)   span=0  changes=0
改前  ：8,8,6,6,6,...,8,8,6,6,...   span=2  changes=3   ← 来回跳
改后  ：6,6,6,6,... (150 帧)        span=2  changes=1
```

| 阶段 | 改前 | 改后 |
| --- | --- | --- |
| `Smooth()`（采样端 EMA） | 上升 0.55 / 下落 0.30 | 上升 0.16 / 下落 0.10 |
| `Ease()`（显示端半衰期） | 90 ms / 260 ms | 900 ms / 2600 ms |

原来的时间常数让柱子对每次采样波动都响应，看起来伸缩过大。放大到约 1s 攻击 / 2.6s 回落后，柱子稳定成一条线。

---

## 深入：Ping 指示灯为什么是闪的

早期判据是 `Ping >= 0`（有读数就亮）。加上"丢包保持旧值"的容错后，读数恒 ≥0，灯就常亮了 —— **5 秒间隔和 500ms 看起来完全一样**。

现在由探测序列号驱动：`Sampler.PingSeq` 每次成功测量自增，主循环发现变化就把 `PingPulse` 置 1.0，再按 `pingMs * 0.55`（限幅 300–1800ms）线性衰减。整组一起出现、一起**彻底消失**，不留残影：

| 部件 | 熄灭 alpha | 亮起 alpha |
| --- | --- | --- |
| 箭头图标 | **0**（不绘制） | 255 |
| 延迟数字 | **0**（不绘制） | 255 |
| `ms` 字牌 | **0**（不绘制） | 255 |

alpha 走 **smoothstep** 曲线 `t²(3-2t)`：两端平缓、中间陡，所以是「亮起 → 平滑渐隐 → 完全消失」，没有突跳。亮起时长 `interval × 0.4`，限幅 900–2600ms —— 5 秒间隔约亮 2 秒，30 秒间隔约亮 2.6 秒。

**实测（5 秒间隔，原始合成层）**

```
t=0ms    pulse=1.00  icon=76.4   ← 满亮
t=200ms  pulse=0.71  icon=59.6
t=400ms  pulse=0.43  icon=48.6
t=600ms  pulse=0.14  icon=43.1
t=800ms  pulse=0.00  icon=42.5   ← 只剩面板底色，完全消失
t=5000ms pulse=0.00  icon=42.5   ← 一直黑到下次探测
```

桌面实测三部件各自亮度区间：

```
icon : 55.6 → 74.9   (Δ 19.3)
digit: 51.1 → 64.5   (Δ 13.4)
ms   : 47.8 → 77.9   (Δ 30.1)
```

整条顶部区域亮灭差 12.6，熄灭帧与面板底色一致。不想要这个闪烁就在菜单关掉「Ping 图标随探测闪烁」，会退回常亮。

**连续渐变实测**（40ms 采样，5 秒间隔）：

```
86.0 85.8 85.7 85.4 85.0 84.7 84.3 83.7 83.0 84.1
82.0 82.0 81.5 80.8 80.1 79.5 78.8 78.2 78.2 77.5
77.0 76.4 75.7 75.7 75.1 74.3 75.4 75.4 73.3 73.1 72.7
```

30 个中间值单调递减，无跳变。

---

## 数据从哪来

### 速率

`NetworkInterface.GetIPv4Statistics()` 的**滑动窗口差分**（默认 9 槽 ≈ 540ms，上限 0.7s），再叠一层快升慢降的 EMA。

为什么不用逐帧差分：Windows 的网卡计数器由驱动批量刷新，60ms 瞬时差分会剧烈跳动。实测同一台机器：

```
逐帧差分：avg=6092KB/s  min=7KB/s   max=74774KB/s   摆幅 11485x
滑动窗口：avg=5611KB/s  min=17KB/s  max=38349KB/s   摆幅  2244x
```

另外两个坑一并修了：Rx/Tx 原本共用同一环形缓冲区游标，导致时间戳与字节数错位、速率周期性塌到 0；现在两个方向各有独立 ring。

### 连接数

优先调 `spd.exe cons`（约 20ms，700ms 缓存），即 cFosSpeed 自己流控的连接列表，与原版面板数字完全一致；`spd.exe` 不可用时回退 `GetExtendedTcpTable` 并排除 127.0.0.0/8 回环。

为什么不能直接数系统连接：本机 `netstat` 有 87–96 个 ESTABLISHED，其中 70 个是 127.0.0.1 回环；cFosSpeed 的 `cons` 只列它实际流控的 3–4 条。差一个数量级，所以必须问它本人：

```
spd.exe cons      → 3 条
本程序 --connstat → conn=3
原版面板显示      → 3
```

### 延迟

ICMP，目标可选（baidu / 腾讯 DNS / 阿里 DNS / Cloudflare / Google），间隔可调。

Windows 的 `Ping` 只返回整数毫秒，本地链路抖动远小于 1ms，所以读数会全挤在同一个整数上（实测 223.5.5.5 连续 12 次全 40）。为此叠加了高精度秒表取均值，把亚毫秒抖动量恢复出来，读数才会在 40/41 之间真实起伏。

单次丢包**不会**让数字消失：失败立即重试一次，连续 3 次都失败才判定无信号。之前偶发一次超时就把值写成 -1、图标变灰、数字变 `--`，看起来像"切刷新率把 ping 弄挂了"。

---

## 渲染与清晰度

| 处理 | 原因 |
| --- | --- |
| `glass.png` 画在**底层**当面板，只在最外 2px 重描一圈描边 | 之前当顶层玻璃罩会把所有像素蒙一层雾，这就是模糊的根源 |
| 分层窗口 `UpdateLayeredWindow` + premultiplied BGRA | 真透明，透出真实桌面而不是黑底 |
| 进程 DPI 感知（PerMonitorV2） | 系统不再对窗口做位图拉伸 |
| 缩放只允许 1x / 2x / 3x，最近邻 | 非整数倍会产生插值毛边 |
| 柱子跨帧淡入 | 皮肤只有 26 帧，硬切会一顿一顿。现在画满低帧再淡入高帧，20 级采样实测亮度单调递增 33.8 → 129.4，每级 +4.6~5.1，无回落 |
| 数字 6% 死区 + 独立慢时钟 | 尾数不再高频跳动 |

---

## 打包：300 KB 单文件绿色版

`build.bat` 会把两个皮肤目录压成 `skins.zip`，再用 `csc /resource` 嵌进 exe，产出**单个约 300 KB 的 exe**，不依赖任何外部文件夹。

```
pack-skins.ps1   →  build/skins.zip   (约 227 KB)
csc -resource:skins.zip,skins.zip ...  →  build/CfosWidget.exe  (300 KB)
```

运行时行为（`src/SkinPack.cs`）：

1. 首次启动把内嵌 zip 解到 `%TEMP%\cfoswidget-skins\<内容hash>\`，用 hash 做目录名，换版本自动重建
2. `SkinPack.ResolveDir(name)` 优先返回解出的目录，其次回退到 exe 旁的 `skins\` 文件夹
3. 所以既能单文件裸跑，也支持往 exe 同级放一个新 cFos 皮肤目录而不重编译
4. `Skin.Load()` 接受裸名字（`liquid_crystal`）或完整路径，内部统一走 `ResolveDir`

配置文件 `cfoswidget.ini` 写在 exe 同目录（首次改设置时生成）：

```ini
[widget]
nic={413C5F7C-...}
skin=liquid_crystal
x=2400
y=1250
locked=0
passthrough=1
ontop=1
interval=60
textinterval=240
pinginterval=5000
pingblink=1
zoom=1
ping=119.29.29.29
```

---

## 图标

`icon\icon.ico` 就是 **cFosSpeed 原版图标**：用 `PrivateExtractIcons` 从 `C:\Program Files\cFosSpeed\cfosspeed.exe` 按 16/24/32/48/64/128/256 七种尺寸导出后重新封装成 multi-size ico（139 KB，PNG 压缩条目）。

图形是蓝色鳍状底 + 橙色羽毛（下载）+ 绿色羽毛（上传），与界面配色天然一致。

编译时 `csc -win32icon:icon\icon.ico` 写入 exe 资源；托盘图标通过 `Icon.ExtractAssociatedIcon` 复用同一份，开启穿透时叠加橙色斜杠作状态提示。原始导出素材在 `icon\orig\s*.png`。

---

## 源码结构

| 文件 | 职责 |
| --- | --- |
| `src/Program.cs` | 分层窗口、托盘、穿透、拖拽、配置、缩放 |
| `src/Skin.cs` | 皮肤加载、帧缓存、alpha 合成、位图字体排版 |
| `src/Metrics.cs` | 网卡枚举、速率、TCP 计数、ping |
| `src/SkinPack.cs` | 内嵌皮肤包的释放与路径解析 |
| `src/Ini.cs` | cFos 风格 ini 解析 |

---

## 已知取舍

- **只统计 IPv4**：.NET Framework 的 `NetworkInterface` 没有 `GetIPv6Statistics`，实测该接口不提供 IPv6 计数。本机 IPv4 与网卡总量差异在噪声级（rx 差 5 万 B/s @ 26 MB/s），影响可忽略。
- **速率按对数映射柱高**（`log10(1+kb)/4.5`），低速高速都看得见。空闲时读数在 20–30 KB/s 附近小幅跳动（绝对摆幅约 8 KB/s），这是真实后台流量而非噪声；满速下载实测稳定在 29–34 MB/s，平台段波动 ±10%。
- **只做显示**，不含 cFosSpeed 的流量整形。
