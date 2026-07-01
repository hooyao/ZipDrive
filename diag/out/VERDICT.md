# 最终判定 — WinFsp Photos 卡死（Dokan vs WinFsp 带 chunk-wait 日志对照）

> 决定性证据：**两个 build 只有 adapter 不同（Dokan vs WinFsp），其余代码逐字相同、配置完全相同
> （`appsettings.dev.jsonc`：cutoff=5MB / mem=50 / disk=500 / TTL=1min / Information）**，
> 各跑一遍同样的复现，对比 `Chunk-wait BLOCK/DONE` 日志。
> - WinFsp（卡死）：`diag/dumps/chunkwait-winfsp.log`
> - Dokan（不卡，用户实测「一点都不卡」）：`diag/dumps/chunkwait-dokan.log`
> 分析人：Claude Code。日期 2026-07-01。**本报告再次修正了根因结论 —— 见下。**

## ⛔ 推翻了上一版的根因

上一版 VERDICT 说：「WinFsp 的读落在未解压 chunk、Dokan 的读不落，所以 Dokan 不卡。」
**Dokan 自己的日志证伪了这条。** Dokan **同样**大量读尾巴、同样死等顺序解压，等的时间**还更长**：

| | **WinFsp（卡）** | **Dokan（不卡）** |
|---|---|---|
| Chunk-wait BLOCK 次数 | 13 | **13** |
| 读的位置 | 全是 `needsChunk=总数-1`（尾巴） | **全是 `needsChunk=总数-1`（尾巴）** |
| BLOCK 时解压进度 | `extractedChunks=1`, progress≈3% | **`extractedChunks=1`, progress≈3%** |
| 单次等待时长 | 2.2 – 6.3 秒 | **5.6 – 7.3 秒（更久）** |

> 「读尾巴 → 死等整文件顺序解压」是**两边都有**的现象，不是差异。**chunk-wait 本身不是病根。**

## ✅ 确证的真正根因（决定性对照）

**差异不在「读没读尾巴 / 等没等 chunk」，而在「一个慢读会不会拖住同卷上其它读」。**

- **Dokan**：8 个视频在 `00:42:30→00:42:37` 同时 BLOCK 死等解压（每个等 5.6–7.3s）的**同一时刻**，
  Dokan 仍在**每秒服务几十张图片读**（`gra_akari-ne013 … ne042`，offset=0，一路不停）。
  → 慢读被**隔离**，没挡住别的读 → Photos UI 流畅。
- **WinFsp**：在它的 block 窗口 `23:49:29→23:49:34`，**唯一流过的 5 条读全是视频 `.mp4` 自己**
  （`gra_suzu-ma03…06.mp4`，offset=0 和 offset=445562880 尾巴），**没有一张图片读穿过去**。
  WinFsp 的读吞吐高峰出现在 `23:50:12/13`（34/47 个读）——是**视频解压完之后**才一次性爆发的，
  正对应「整窗口冻死几十秒、解压完才恢复」。
  → 慢读**没被隔离**，把同卷后续读全堵住 → Photos UI 冻死。

### 一句话

> **同样是「读尾巴要等几秒解压」，Dokan 把这个慢读和其它读隔离开（别的读照常过），WinFsp 没有
> （慢读把同卷读队列堵死）。所以 Dokan 不卡、WinFsp 卡。问题不在缓存/解压层，在 adapter/卷的
> 读并发隔离。**

## 机制推断（adapter 层，未 100% 坐实，但不影响修复）

两边 VFS/缓存代码逐字相同，`WinFspFileSystemAdapter.ReadFile` 还是 `async ValueTask` +
`SynchronousIo => false`（读不钉派发线程）。所以堵塞**不在 ZipDrive 托管侧的线程模型**，而在
**WinFsp 卷怎么被 Windows 使用**：

- `WinFspHostedService` 用 **WinFsp 默认参数** `Mount()`：未设 `FileInfoTimeout`、**未禁用 Windows
  cache manager**。WinFsp 默认让卷走 Windows 缓存 + **read-ahead**。
- 推断：Photos/Explorer 浏览时，Windows cache manager 对视频文件触发**预读**，预读 IRP 走 cache
  manager 有限的工作线程；一个预读卡在「等解压」上几秒，就占住线程、把**同卷后续读（含图片）**
  压在内核队列里 → 表现为整窗口冻死。
- Dokan 默认不挂 Windows cache manager（或读模型不同），慢读天然隔离，故不堵。

> 要坐实可加 WinFsp debug 日志看 IRP 串行情况，或对比禁用缓存后的行为。**但下面的修复方向与此无关** ——
> 无论 Windows 为什么把慢读串起来，根治都是「别让一个读卡几秒」。

## 🔑 关键事实：这批 mp4 是 DEFLATE，且压缩比 0.996（白解压）

实测 `GRAPHIS.Gals-14/-16.zip`（`python zipfile` 读中央目录）：

| 类型 | 压缩方式 | 大小 | 压缩比 | 走哪个 tier |
|---|---|---|---|---|
| mp4（41–66 个/包） | **DEFLATE** | 250–611 MB | **0.996–0.998** | disk-tier chunked（全 ≥5MB cutoff） |
| jpg（1599 个/包） | DEFLATE | 几百 KB | ~0.987 | memory tier（< 5MB，秒回） |

两个推论：
1. **direct-read 行不通**：mp4 是 DEFLATE 不是 Store，**不能 seek 进压缩流中间**
   （用户早就指出「Deflate 只能顺序解压」）。所以「读尾巴只能从头解整条流」是硬约束。
2. **解了个寂寞**：视频本就是压缩格式，deflate 对它无效（611MB→609MB）。为读尾巴 4KB 的
   moov atom，要把整条 611MB deflate 流从头解一遍，解出来还几乎和压缩数据一样大 —— 纯浪费。

> 这解释了「图片秒开、视频拖死」：jpg 小走内存 tier 立即命中；mp4 巨大走 disk-tier，读尾巴 = 等
> 几百 MB 顺序 deflate 解压。

## 修复方向（对症，按推荐顺序 —— 已据 DEFLATE 事实重排）

核心病灶在缓存层：**对一个还没顺序解压到的位置的读，会一直死等（几秒～几十秒）。**
两边都有这个等待，只是 WinFsp 把它放大成 UI 冻死。把「读不再长时间死等」根治掉，两个 adapter 都受益。

1. **P0 — WinFsp 卷复刻 Dokan 的「慢读隔离」（直击根因差异）。**
   这是 Dokan/WinFsp 唯一的真差异。给 `FileSystemHost.Mount` 调整缓存/读策略，让 WinFsp 的慢读
   不再经 Windows cache manager 串行化、拖累同卷其它读。需查 WinFsp.NET 开关（`../winfsp-native`，
   `OnRead`/缓存相关 flag、`FileInfoTimeout`）。**只改 adapter 层，不动缓存/解压，风险最小、最对症。**
2. **P1 — chunk-wait 加超时回退（与格式无关，缓解兜底）。**
   `EnsureChunkReadyAsync` 等待超过阈值（如 500ms–1s）时让出 / 返回已就绪部分，避免任何单个读把
   调用方钉死几秒。低风险，两个 adapter 都受益。
3. **P2 — 大 DEFLATE 文件的解压策略优化（治本但工程量大）。**
   - 给「读尾巴」场景特判：检测到 moov-atom 式的尾部读，可考虑**后台优先把整文件解压完再服务该 handle**，
     或对超低压缩比（如 >0.95）的 DEFLATE 条目，**首次访问即整文件 materialize**（反正要全解）。
   - ⚠️ **direct-read 不可行**（DEFLATE 不能 seek），已排除。
   - ⚠️ **跳到 chunk N 不可行**（DEFLATE 顺序约束），已排除。

> 已排除：**信号量限并发无效**（不是并发抢带宽，是单读死等）；chunk-wait 日志本身不是病根（两边都有）；
> **direct-read / 乱序 chunk 提取**（DEFLATE 硬约束）。

## 关键数据落点

- Dokan BLOCK 全表、WinFsp BLOCK 全表、两边 block 窗口内的读活动对照：见本次分析的日志提取（已记入上文表格）。
- mp4=DEFLATE/0.996、全 ≥5MB cutoff：已实测确认（`GRAPHIS.Gals-14/-16.zip`）。
- 下一步：定 P0（WinFsp 挂载缓存隔离）的具体开关 —— 去 `../winfsp-native` 查 `FileSystemHost`/`OnRead`
  缓存与 read-ahead 相关配置，做一个「禁用可缓存读 / 调 FileInfoTimeout」的 WinFsp build 给用户验证。
