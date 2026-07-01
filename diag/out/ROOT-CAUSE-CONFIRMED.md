# ROOT CAUSE 确证 — WinFsp Photos 卡死（实验验证版）

> 方法：读 winfsp / winfsp.native / ZipDrive 源码 + 两组真实 dump + 两个最小 WinFsp 复现实验
> （UNC mount，未挂真 zip / 真盘符）。日期 2026-07-01。分析人：Claude Code。
> 复现实验代码：`F:\MyProjects\winfsp-slowread-repro\`。

## 一句话 root cause

**Windows I/O Manager 对以「同步 handle（non-overlapped）」打开的文件，会用 per-file 锁（FCB）
把该文件的所有读串行化。ZipDrive 的一个「读」会一直阻塞到目标 chunk 顺序解压完成（视频要几十秒）；
在这几十秒里，同一个视频文件的其它读全被这把锁挡住。Windows「照片」在自己的线程上顺序读这个视频，
于是卡死在自己的读上，直到解压完成、锁释放。图片（不同文件、内存 tier 秒回）不受影响，
因为锁是 per-file 的。Dokan 不卡，是因为 Dokan 对同一文件允许并发读，不走这条串行化路径。**

## 实验证据（决定性）

### 实验 A：慢读会不会堵「别的文件」？——不会

最小 FS：`slow.bin` 读时延迟，`fast-*.bin` 秒回。8 个并发慢读（`await Task.Delay`，真异步）
背景下狂读 fast 文件（threadCount=4，故意小于慢读数）：

```
[under 8 slow reads]   n=61349  p50=1.26ms  p99=2.34ms  max=6.30ms
=> fast reads ISOLATED (no hang)
```

**结论**：慢读走 WinFsp 的 STATUS_PENDING 异步路径（`FileSystemHost.OnRead` line 552 `return NtStatus.Pending`），
不钉派发线程；**不同文件**的读完全不受影响。→ 排除「派发线程池耗尽」「慢读堵别的文件」。

### 实验 B：慢读会不会堵「同一个文件」？——会，而且正是卡死

一个慢「尾读」in-flight 时，测同文件 HEAD 读 vs 不同文件读：

```
slow tail read (video.bin)          : 3005.0ms   （基准：故意延迟 3s）
concurrent HEAD SAME file (sync)    : 2847.4ms   <== 被串行化阻塞！（non-overlapped handle）
concurrent HEAD SAME file (overlap) :    1.8ms   （overlapped handle 绕过 FCB 锁，不堵）
concurrent read OTHER file          :    0.7ms   （不同文件，完全不堵）
```

**结论**：
- **同一文件**的 non-overlapped 读被 I/O Manager 的 per-file FCB 锁串行化 → 被慢读堵满 3s。
- 换成 **overlapped（异步）handle** 就不堵（1.8ms）→ 证明是 I/O Manager 对同步 handle 的串行化，
  不是 WinFsp FSD 的锁（FSD 侧 non-cached 读只取 **shared** 锁，见 `winfsp/src/sys/read.c:356`，允许并发）。
- **不同文件**永远不堵 → 锁是 per-file 的。

## 与真实 dump / 日志的交叉印证（全部吻合）

| 现象（真实卡死） | root cause 解释 |
|---|---|
| 卡死时 dump 里 ZipDrive 托管侧**空闲**，只有 2–4 个 Read 挂在 chunk-wait | 卡的是**照片自己**在等 I/O Manager 的 FCB 锁，新读没发到 ZipDrive |
| 卡死时日志 ZipDrive **2–5 秒收不到任何读**（jpg=0 mp4=0） | 同上：读堵在内核 FCB 锁，没进 ZipDrive 的回调 |
| 同一视频 `gra_suzu-ma06.mp4` 一个文件有 **5 次读**排队（offset=0 和尾巴 445562880） | per-file 锁把这个文件的多个读串成一列 |
| 图片秒开不卡 | 图片是**不同文件**、小、走内存 tier 秒回，锁 per-file 不波及 |
| 视频解压完就恢复（与 `Extraction complete` 时刻吻合） | 慢读返回 → FCB 锁释放 → 照片排队的读放行 |
| Dokan 同样读尾巴、同样等 chunk（`chunkwait-dokan.log` 13 次 BLOCK，等更久），却不卡 | Dokan 对同文件允许并发读，不走 I/O Manager 同步 FCB 串行化 |

## 为什么之前的判断都不对（诚实记录）

- ❌ cache manager / read-ahead 串行化：ZipDrive `FileInfoTimeout=0`，kernel cache 本就关着。
- ❌ 派发线程池耗尽：实验 A 证明慢读走 PENDING 不占派发线程；dump 里派发线程空闲。
- ❌ 同步回调（OpenFile/GetFileInfo/ListDirectory）慢：5 张 dump 的 `dumpasync` 里**从没**出现这些回调挂起，只有 ReadFile。
- ❌ direct-read / 乱序 chunk：mp4 是 DEFLATE（压缩比 0.996），只能顺序解压 —— 但这只是「读为什么慢」，不是「慢读为什么冻住整窗口」。真正的放大器是 per-file 串行化。

## 病灶的两层

1. **慢**：读视频尾巴（moov atom）→ 落在最后一个 chunk → DEFLATE 只能顺序解压 → 读要等整文件（几十秒）。
   这一层 Dokan/WinFsp **相同**。
2. **卡**（放大器，唯一差异）：WinFsp 卷上，同步 handle 的同文件读被 I/O Manager 串行化 →
   一个几十秒的慢读把照片对这个视频的所有后续读全堵死 → UI 冻结。Dokan 无此串行化。

## 修复方向（对症，按推荐）

因为「handle 是否 overlapped」由消费者（照片）决定、ZipDrive 改不了，**根治只能是「别让任何一个读长时间不返回」**：

1. **P0 — chunk-wait 加超时回退**（唯一能同时救 WinFsp、且格式无关）：
   `ChunkedStream.EnsureChunkReadyAsync` 等待超过阈值（如 500ms–1s）时**返回已就绪的部分数据**（部分读在
   Windows 上完全合法：`ReadFile` 允许返回比请求少的字节）。这样单个读永不长期持有 FCB 锁，照片的读能推进。
   > 注意：不能返回错误/EOF 伪造数据；要返回「到目前为止已解压到的真实字节数」，未就绪部分等下一次读再取。
2. **P1 — 大 DEFLATE 文件的尾部读特判**：检测到「读接近文件尾 + 该 chunk 远未解压」时，可优先把该文件整体
   materialize（反正 moov 读注定要全解压），并**让首个读快速返回可得部分**，避免长期占锁。
3. **P2（存疑，需实测）— WinFsp 挂载/卷层面**：探索是否有办法让 WinFsp 卷对同文件读不经 I/O Manager 同步
   串行化（复刻 Dokan 行为）。但 FSD 侧已是 shared 锁，串行化在其之上的 I/O Manager，**大概率改不动** ——
   所以 P0 才是正解。

> 已排除且**无效**：信号量限并发（不是并发问题）；调 FileInfoTimeout（cache 本就关）；
> 加派发线程数（慢读走 PENDING 不占派发线程）。

## 复现程序（可重跑）

`F:\MyProjects\winfsp-slowread-repro\`（独立项目，UNC mount，不碰真 zip/盘符）：
- `SlowReadRepro serialize` → 实验 B（同文件串行化，决定性）
- `SlowReadRepro AsyncDelay --threadCount=4 --slowConcurrency=8` → 实验 A（不同文件不受影响）
- `SlowReadRepro ThreadSleep|SyncOverAsync|SlowInOpen ...` → 各种阻塞模式对照
