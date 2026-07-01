# WinFsp Photos 卡死 —— 最终诊断报告(权威归档)

> ZipDrive 从 Dokan 迁到 WinFsp(commit `7b5bceb`)后,Win11「照片」浏览 SMB/NAS 上 ZIP 内的压缩视频时整窗口卡死几十秒,旧 Dokan 版丝毫不卡。
>
> **本文件是这次诊断的唯一权威归档。** 根因已由**源码逐行比对 + 三组独立实测**确证(非推断)。所有中途被证伪的假说单列一节记录(第 6 节),以说明排除了哪些弯路。
>
> 日期:2026-07-01。诊断分支:`diag/winfsp-photos-hang`。全程用受控实验,未挂真 ZIP / 真盘符。

---

## 1. 根因(一句话)

**WinFsp 的内核文件系统驱动(FSD)在处理一个非缓存读时,获取该文件 FileNode 的 Main 资源锁(shared),并把锁的所有权转移给异步 Request、横跨整个用户态读往返持有,直到读返回才释放。** ZipDrive 读压缩视频尾部(moov atom)必须等整条 DEFLATE 流顺序解压(几十秒),于是这把 shared 锁被持有几十秒。Windows「照片」为显示该视频而对**同一文件**发起的第二次 `CreateFile`(open),在内核完成侧需要 Main **exclusive** 锁,与被长期持有的 shared 锁不兼容,于是 open 阻塞几十秒 —— UI 冻死在这个 open 上。

**被挡的精确是 OPEN,不是 read。** 锁是 **per-FileNode**(单个文件),不是全卷:别的文件、别的 ZIP 完全不受影响。

**Dokan 不卡**,是因为 Dokan 的 FSD 在把读 IRP 入队(PENDING)**之前**就释放了 FCB 锁 —— 用户态解压那几十秒里,Dokan 对该文件零持锁,所以同文件的 open 不被挡。

> **"慢"和"卡"是两回事。** 两个驱动读同一压缩视频尾部**都**要等几十秒顺序解压(DEFLATE 物理下限,相同)。差别只在:慢读期间**是否持锁连累同文件的 open**。Dokan = 视频加载慢但 UI 不冻;WinFsp = UI 冻死在 open 上。

---

## 2. 决定性代码证据(锁生命周期不对称)

源码:`Q:\MyProjects\zipdrive-source\{winfsp,dokany}`。

### 2.1 WinFsp —— 读锁转移给异步 Request,横跨用户态往返持有

`winfsp/src/sys/read.c`,`FspFsvolReadNonCached`:
```c
356:  Success = ... FspFileNodeTryAcquireSharedF(FileNode, FspFileNodeAcquireFull, CanWait);  // 取 Main+PagingIo shared
444:  FspFileNodeSetOwner(FileNode, Full, Request);   // ★ 锁所有权从当前线程转移给异步 Request
461:  return FSP_STATUS_IOQ_POST;                     // IRP 派给用户态队列
```
释放点在完成回调,即用户态读**返回之后**:
```c
// read.c:671  FspFsvolReadNonCachedRequestFini —— request 完成时才跑
671:  FspFileNodeReleaseOwner(FileNode, Full, Request);   // ★ 只在用户态读完成后才释放
```
- `FspFileNodeAcquireFull` = Main + PagingIo,均 **shared**(`file.c:432/440`,`ExAcquireResourceSharedLite`;`driver.h:1650-1652`)。
- `FspFileNodeSetOwner`(`file.c:533`)底层 `ExSetResourceOwnerPointer`,让锁跨线程存活,dispatch 线程返回后不释放。

### 2.2 open 完成侧要 Main exclusive(被挡的地方)

`winfsp/src/sys/create.c`,`FspFsvolCreateTryOpen`:
```c
1326:  FspFileNodeTryAcquireExclusive(FileNode, Main) && ...   // ★ open 完成阶段要 Main 独占
```
exclusive 与被长期持有的 shared 不兼容 → 同文件第二次 open 阻塞到慢读结束。
(shared+shared 兼容,所以真正被挡的是需要 exclusive 的操作 —— 最典型就是 open。)

### 2.3 Dokan —— 读期间不持有任何 FCB 锁

`dokany/sys/read.c`,`DokanDispatchRead`:
```c
167:  DokanFCBLockRO(fcb);                        // 取 shared FCB 锁(仅为构造 event context)
240:  status = DokanRegisterPendingIrp(...);      // 入队 → 返回 STATUS_PENDING
242:  __finally { if (fcbLocked) DokanFCBUnlock(fcb); }   // ★ 入队后立刻释放,早于用户态处理
```
用户态解压那几十秒里,Dokan 内核侧对该 FCB 零持锁。

---

## 3. 三组独立实测(全部真机跑通,数字稳定)

### 3.1 Dokan 对照实测 —— `diag/dokan-slowread-repro/`
纯 C# + 本地 DokanNet 2.3.0.4,挂临时空目录(非盘符)。慢尾读进行时测同文件并发 open+read:

| 配置 | 慢尾读 | 同文件并发 open+read | 判读 |
|---|---|---|---|
| default(多线程) | 3012ms | **1.3ms** | 不堵 |
| --threadCount=4 | 3014ms | **1.2ms** | 不堵 |
| --threadCount=1 | 3014ms | **2852ms** | 堵* |

\* 单线程的 2852ms 是**同步回调 dispatcher 线程耗尽**(Dokan 的 `ReadFile` 是同步的,唯一线程被 `Thread.Sleep` 慢读占死),**不是** FCB 锁;给到 ≥2 线程就消失。真实 ZipDrive 走异步 STATUS_PENDING、不占派发线程,不受此影响 —— 实验特意用"单慢读+多档 threadCount"把这个混淆变量干净隔离。
→ **Dokan ≥2 线程时同文件 open ~1ms,证实 Dokan 不跨往返持锁。**

### 3.2 纯 C WinFsp 实测 —— `diag/winfsp-c-repro/`(排除 .NET binding)
纯 C + 官方 WinFsp SDK,**零 winfsp-native binding**,UNC 挂载(零盘符)。启动一个 video.bin 慢尾读,并发测同文件 / 异文件的 open 与 read(拆开计时):

| 配置 | 慢尾读 | 同文件 OPEN | 同文件 read | 异文件 OPEN |
|---|---|---|---|---|
| blocking, 无 cache | ~3008ms | **~2806ms 阻塞** | ~0.1ms | ~1.2ms |
| STATUS_PENDING 异步 | ~3008ms | **~2805ms 阻塞** | ~0.1ms | ~1.3ms |

结论:
- **纯 C 零 binding 也 100% 复现** → 锁在 WinFsp **内核 FSD**,winfsp-native binding 无辜,用户态无法绕过。
- **被挡的精确是 OPEN(~2806ms),不是 read**(open 一返回 read 仅 0.1ms) → 坐实 §2.2 的 exclusive 撞 shared。
- **异文件 open ~1.2ms 不受影响** → per-FileNode 粒度,非全卷。
- **STATUS_PENDING 不解锁** → 锁按 IRP 生命周期持有到 SendResponse,异步化救不了。
- 白盒佐证:WinFsp 用户态 DLL 默认 FINE 守卫策略下,Read 不取 guard 锁、FILE_OPEN 只取 shared,用户态互不阻塞 → 串行化只可能来自内核 FSD。

### 3.3 warm kernel cache 实测 —— `diag/winfsp-c-repro/`(决定性对照)
开**真正的** kernel cache(`FileInfoTimeout = ∞ = 0xFFFFFFFF`,唯一能触发 `FspFsvolReadCached` 缓存读路径的值,read.c:248 断言要求)。先证 cache 生效(warm 读零派发 Read 回调),再测慢尾读(cache miss、持锁)期间的同文件操作:

| run | 慢尾读 | 同文件 OPEN | 同文件 head read |
|---|---|---|---|
| 2 | 3005ms | **2793ms 阻塞** | **0.0ms(命中 cache、零派发)** |
| 3 | 3017ms | **2805ms 阻塞** | **0.0ms(命中 cache、零派发)** |

**最干净的隔离**:同一文件、同一时刻,head read 命中 kernel cache(0.0ms、根本不派发用户态),但 OPEN **照样挡 2800ms**。这把"数据是否 warm"与"open 是否被挡"彻底解耦 —— 挡 open 的纯粹是 create 完成侧拿 Main exclusive 撞上慢读持的 Main shared,**与数据/元数据 warm 无关**。
→ kernel cache 只能缓存**已返回过**的字节;首次读未解压 offset 必然 cache miss + 持锁,正是「照片」卡死场景。**kernel cache 救不了。**

---

## 4. 救法裁决(所有"留在 WinFsp"的救法都排除了)

| 救法 | 裁决 | 依据 |
|---|---|---|
| partial return(读超时返回已就绪字节) | **只缓解,不根治** | 第 5 节 |
| STATUS_PENDING 异步化读回调 | ❌ 不解锁 | §3.2 纯 C 实测 |
| 开 kernel cache(FileInfoTimeout=∞) | ❌ 不解锁 | §3.3 warm cache 实测 |
| 整文件预解压 | ❌ 用户否决(语义错) | VFS 惰性/流式设计,不该急切整文件解压;宁可退 Dokan |
| 改 WinFsp 内核锁模型 | ❌ 碰不到 | 内核驱动,需 fork + 驱动签名,脱离官方 WinFsp |

**剩下三条真实的路**:
1. **回退 Dokan** —— 无此锁,实测同文件 open ~1ms,**根治**。代价:刚从 Dokan 迁走。
2. **接受现状** —— 承认 WinFsp 下浏览压缩视频会冻,不改。
3. **partial return(budget 折中 500ms)** —— 明知只缓解不根治,但把"几十秒冻死"压成"秒级卡顿",改动小。

---

## 5. partial return 为什么只缓解、不根治(两个实测边界)

方向:`ChunkedStream.EnsureChunkReadyAsync` 等待超阈值(budget)就返回**已就绪的真实字节**,让读快速返回 → 锁快速释放。

### 5.1 纯尾读死结(根本局限)
partial return 只能返回 `[offset, 解压前沿)` 之间的真实字节。**压缩视频的 moov 在文件末尾,是纯尾读**(dump 实证:`needsChunk=末块, extractedChunks=1`)—— offset 落在最后才解压的 chunk,在它就绪前**一个真实字节都没有**。此时:返 0 = EOF(损坏);伪造字节 = 数据损坏;继续等 = 持锁冻。**对真实病灶(纯尾读)无 partial 可返回。**

### 5.2 多并发退化(D4 实测)—— `diag/winfsp-slowread-repro/` 子命令 `d4`
即便对"跨解压前沿的读",partial 也只是把"一次长持锁"变成"高频短持锁"。测 open 能否挤进 shared 释放的空隙:

| 并发慢读数 | 现状(无修复) | partial 后 open 卡顿 |
|---|---|---|
| 1 个 | 3813ms | **747ms**(≈1 个 budget,可接受) |
| 4 个交错 | 7822ms | **2362ms**(≈3 个 budget,接近冻结) |

机制:open 的 exclusive 要等当前持 shared 的慢读跑完一整个 budget 才能拿到;多个慢读 budget 窗口重叠时,"所有 shared 同时释放"的空隙难出现。
→ **修复效果 = 把几十秒冻死压成 N×budget 卡顿**(N=同文件重叠慢读并发数)。budget 是"open 卡顿时长"与"读放大/加载速度"的直接权衡。

---

## 6. 被证伪的推断(记录排除的弯路)

诊断过程中提出过三批假说,均被后续实测/源码推翻。记录于此以免重蹈:

| # | 曾经的假说 | 被什么证伪 |
|---|---|---|
| 1 | **线程池饥饿**:WinFsp dispatcher 池 [4,16] + .NET ThreadPool 被同步元数据回调占满,SMB 延迟下饱和 | dump 的 dumpasync 显示卡死时只有 ReadFile 挂起,**从无** OpenFile/GetFileInfo/ListDirectory 等回调挂起;托管侧空闲。派发线程未耗尽。 |
| 2 | **I/O Manager 对同步 handle 的 per-file FCB 锁**串行化(HANDOVER/ROOT-CAUSE-CONFIRMED 的框定) | 该锁是 **per-FILE_OBJECT(per-handle)**,只串行化同一 handle 的连续 IRP,不会让两个不同 handle 互堵;且此机制 **Dokan 也有**(dokany read.c:152 一样读 FO_SYNCHRONOUS_IO),不能解释差异。真凶是 FSD 层跨 handle 的 SetOwner 持锁(§2.1)。 |
| 3 | **Windows cache manager 预读串行化 / P0=WinFsp 挂载层复刻 Dokan 隔离**(VERDICT 的推断) | ZipDrive `FileInfoTimeout=0`,kernel cache 本就关;且 §3.3 实测证明**开** kernel cache 也不解锁。挂载层无开关可绕过内核锁。 |
| 4 | **partial return 能救 open**(FIX-PROPOSAL 早期假设) | §5.1 纯尾读死结 + §5.2 D4 多并发退化实测:只缓解不根治。 |
| 5 | **kernel cache 冷 open 场景已排除即全部排除**(C 实验 M3,timeout=1000) | timeout=1000 ≠ ∞,根本没触发缓存读路径(read.c:248 断言要 ∞);warm 路径当时未真正测。§3.3 用 ∞ 补测后才坐实。 |

排除且无效(别重试):信号量限并发、调 FileInfoTimeout、加派发线程数、direct-read / 乱序 chunk 提取(DEFLATE 顺序约束)、STATUS_PENDING 异步化、开 kernel cache、整文件预解压(语义否决)。

---

## 7. 复现资产(均可重跑)

| 目录 | 内容 | 跑法 |
|---|---|---|
| `diag/dokan-slowread-repro/` | Dokan 同文件串行化对照(§3.1) | `dotnet run` (挂临时空目录,非盘符) |
| `diag/winfsp-c-repro/` | 纯 C WinFsp,排除 binding(§3.2)+ warm cache(§3.3) | `build.cmd` 编译;`run-warm.ps1` 跑 warm 实验;UNC 挂载零盘符 |
| `diag/winfsp-slowread-repro/` | WinFsp 串行化 + D4 partial 多并发(§5.2) | `dotnet run -- serialize` / `-- d4 --slowThreads=4` |

安全约束(所有 repro 共同遵守):UNC 或临时空目录挂载、**从不占常驻盘符**;每次实验唯一 mount 名、即用即卸;`finally` 强制清理 + 校验无残留卷。

---

## 8. 原始证据索引(dump / 日志,已入库)

- `diag/out/dump0*-*.txt` —— 5 张 WinFsp 卡死 dump 的 SOS 报告(clrstack / dumpasync / pstacks)。关键:卡死时托管侧空闲,只有 ReadFile 挂起,无同步回调挂起。
- `diag/out/ab-dump02-*.txt` —— Dokan 对照 dump 的 dumpasync。
- `diag/dumps/chunkwait-winfsp.log` / `chunkwait-dokan.log`(UTF-16)—— 两个 build(仅 adapter 不同,余代码逐字相同)的 chunk-wait 日志对照。关键事实:两边**都**大量 chunk-wait BLOCK 读尾巴(needsChunk=末块),Dokan 甚至等更久却不卡 → chunk-wait 本身不是病根,持锁才是。

> ⚠️ 原始 `.dmp`(973MB)未入库,结论已提炼进上述 txt。
