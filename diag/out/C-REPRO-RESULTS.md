# C-REPRO-RESULTS — 纯 C WinFsp 锁行为隔离实验结果

> 目的:用**纯 C(零 .NET / 零 winfsp-native binding)**的最小 WinFsp 文件系统,验证
> "同一文件的慢读串行化阻塞该文件其它 open/read" 这一行为到底在 **WinFsp 内核 FSD / I-O Manager 层**,
> 还是在 **winfsp-native (.NET binding) 层**。
>
> 环境:Windows 11 Enterprise 26200,WinFsp(SxS,`\Device\WinFsp.Net` 网络设备),VS18 cl.exe,.NET 10。
> 挂载方式:**UNC / net prefix**(`\\<uniqueprefix>\share`),**从不使用盘符**。每次实验用唯一 prefix,即用即卸。

---

## 结论(一句话)

**纯 C 也 100% 复现"同文件慢读串行化阻塞 open",且被阻塞的精确是 OPEN 阶段(~2.8s),不是 read。锁在 WinFsp 内核 FSD 层,winfsp-native binding 无辜;用户态无法绕过。kernel cache(FileInfoTimeout>0)不改变结论。**

---

## 实测数字

方法:启动一个 video.bin 尾部(offset=FILE_SIZE-64KB ≥ 32MB)的慢读(FS 内 `Sleep(3000)` 或 pending 线程 `Sleep(3000)`),
等 200ms 确保慢读在 FS 内在途,然后从**独立线程**并发:
- 探测 A:open + 读 **同一文件** video.bin 的 head(offset=0,瞬时区);open 与 read 分开计时。
- 探测 B:open + 读 **另一文件** other.bin 的 head(始终瞬时)。

每配置 3-4 次,数字高度稳定(±几 ms)。单位 ms。

| 配置 | tail 慢读 | 同文件 OPEN | 同文件 head read | 异文件 OPEN | 异文件 read |
|---|---|---|---|---|---|
| **M1 blocking, 无 cache**(`--tailDelayMs=3000`) | ~3008 | **~2806 ← 阻塞** | ~0.1 | ~1.2 | ~0.1 |
| **M2 PENDING(async), 无 cache**(`--pending --tailDelayMs=3000`) | ~3008 | **~2805 ← 阻塞** | ~0.1 | ~1.3 | ~0.1 |
| **M3 blocking, 有 cache**(`--tailDelayMs=3000 --timeout=1000`) | ~3007 | **~2805 ← 阻塞** | ~0.1 | ~1.2 | ~0.1 |

(FileInfoTimeout=1000 即 kernel metadata/data cache 打开。数字取多次运行代表值;完整逐次输出见本文件末尾"原始输出"。)

### 从数字直接得出的三点

1. **纯 C 复现串行化**:同文件 open 被慢读挡了 ~2.8s(≈ tailDelay 3000 − 探测启动前的 ~200ms 领先量),与旧 winfsp-native 观察一致。
   → 说明串行化**不依赖** .NET binding。

2. **被挡的精确是 OPEN,不是 read**:拆开计时后,同文件 **OPEN ~2805ms**,而 open 一旦返回,**head read 仅 0.1ms**。
   这正好吻合源码机制:非缓存 Read 在 FSD 持 FileNode **shared** 锁跨整个用户态读往返(read.c),
   同文件的 Create/Open 要拿 Main **exclusive**(create.c),被 shared 持有者挡住 → open 冻结。

3. **per-file,不是 per-volume**:慢读 video.bin 期间,**另一文件 other.bin 的 open 仅 ~1.2ms**,完全不受影响。
   → 串行化的粒度是单个 FileNode,不是整卷。这解释了"照片"卡死只发生在正在被慢读的那个视频文件上。

4. **STATUS_PENDING 不解锁**:M2 用异步 pending 模型(Read 回调立即返回 STATUS_PENDING,由 worker 线程 Sleep 后
   `FspFileSystemSendResponse` 完成),同文件 open 仍被挡 ~2805ms。
   → 证实 pending **不会**让内核提前释放 FileNode 锁;锁按 IRP 生命周期持有,直到响应回到内核。

5. **kernel cache 不改变结论**:M3 打开 FileInfoTimeout=1000,同文件 open 仍被挡 ~2805ms。
   在本 repro 的测法里,冷 open(句柄尚未建立、数据未进 cache)这条路径不走 FspFsvolReadCached 的"函数内释放锁"快径,
   因此 cache 开关对"冷 open 撞上在途慢读"这个场景无救。**见下方"建模偏差/不确定"第 3 条**——这一条要诚实标注边界。

---

## 为什么这能把锁定位到内核(而非 WinFsp 用户态 DLL)

除了"纯 C 复现"这个黑盒证据,还有一条白盒佐证。WinFsp 用户态 DLL 的操作守卫(`FspFileSystemOpEnter`,
src/dll/fsop.c)默认策略是 **FINE**(`FSP_FILE_SYSTEM_OPERATION_GUARD_STRATEGY_FINE`,fs.c:157;本 repro 未改过):

- **Read 请求**:在 FINE 下**完全不取**任何 OpGuardLock。
- **FILE_OPEN 的 Create**:只取 **shared** OpGuardLock。

即在 WinFsp 用户态 DLL 层,Read 与 FILE_OPEN **互不加排他锁、互不阻塞**。既然纯 C(默认 FINE)仍然复现 open 被 read 挡死,
那这个串行化**只可能来自内核 FSD**(FileNode 的 Main/shared 资源锁),不可能来自用户态 DLL 守卫,更不可能来自 .NET binding。

判据落点:**纯 C 复现 → 锁在 WinFsp 内核 FSD → binding 无辜 → 用户态无法绕过。partial 死结成立。**

---

## 对 C 代码 / 工装做的修改(相对交接时的状态)

交接时的 `repro.c` 能编译、能挂载,但挂到了盘符 Z:,且并发探测时挂载会掉。本次修改:

1. **改 UNC/net 挂载(核心安全修复)**。
   - 根因:`FspFileSystemSetMountPoint(fs, 0)` 内部等价于 `L"*:"` → 自动分配盘符(就是 Z: 的来源,见 fs.c:198 / mount.c:591)。
   - 修法:net 设备(`FSP_FSCTL_NET_DEVICE_NAME`)+ `VolumeParams.Prefix` 模式下,**根本不调用 SetMountPoint**;
     卷通过 WinFsp 网络提供者在 `\\<prefix>` 暴露(与官方 memfs-net 一致)。崩溃只会丢掉一个 UNC 卷,不留僵尸盘符。
   - 加了 `--prefix=\host\share` 参数,每次实验用唯一名,规避重定向器的 negative-cache(否则同名短窗口内二次查找会被缓存成"不存在")。
   - 另加 `--dir=<空目录>` 作为安全兜底(目录挂载点/reparse,同样不占盘符),本次未使用。

2. **修复"并发探测挂载掉"的真正根因:缺少 Create/Overwrite 回调**。
   - 现象:开 FSD debug(`FspDebugLogSetHandle`+`SetDebugLog(-1)`)后看到每个 `>>Create` 都回 `IoStatus=c0000010`
     (STATUS_INVALID_DEVICE_REQUEST),且我的 `Open` 回调**从未被调用**。
   - 根因:`FspFileSystemOpCreate`(fsop.c:907)要求接口里 `Create`(或 CreateEx)**且** `Open` **且** `Overwrite`(或 OverwriteEx)
     三者都非空,否则**任何** create IRP(包括对已存在文件的 FILE_OPEN)在派发到 Open 之前就被拒。
     原接口只有 Open/Close,所以**没有一个文件能被打开**——这才是"挂载看似掉了"的真相(卷在,但所有 open 失败)。
   - 修法:加只读的 `Create` / `Overwrite` 桩(都返回 STATUS_ACCESS_DENIED,因为 ReadOnlyVolume=1;已存在文件的 open 走 Open 桩)。
     改完后 dir 列表、head 读、并发探测全部稳定,4×3 次实验零掉挂载。

3. **补齐让 net 挂载可用的 VolumeParams / 接口**:
   - `AllowOpenInKernelMode = 1`(net 重定向器/MUP 探测 share 时会内核态 open;缺此项 UNC 路径不解析)。
   - `PersistentAcls = 1` + 真实自相对安全描述符(`O:BAG:BAD:P(A;;FA;;;WD)`,SDDL 构造)+ `GetSecurity`/`GetSecurityByName`
     正确实现 size-query 协议(缓冲区不足回 STATUS_BUFFER_OVERFLOW 并回填所需长度)。
   - `ReadDirectory` 改为按 Marker 过滤、缓冲不足时不写结束标记(让 FSD 带 Marker 再问),消除潜在越界/重复。

4. **诊断能力**:每个回调加带时间戳+线程 ID 的 stderr 日志(`--debug` 开关),Read 回调打 ENTER/EXIT。
   这既是排障手段,也顺带能证明"head-read 回调在 open 返回前根本没被调用"。

5. **工装**:`probe.cs` 改为接受 UNC 根参数、拆分 open/read 计时、同时测同文件与异文件;
   新增 `run-experiment.ps1`:唯一 prefix 挂载 → 探测 N 次(每次 30s 超时看门狗)→ `finally` 保证杀进程 +
   `fsptool lsvol` 校验无残留卷。所有命令均有超时,不会无限挂起。

`build.cmd` 未改(仍 CRLF)。`repro.c`/`probe.cs` 用 LF,对 cl.exe / dotnet 无影响。

---

## 建模偏差 / 不确定 / 诚实标注

1. **repro 用 `Sleep` 模拟慢读,ZipDrive 真实是 chunked 解压阻塞**。二者对 FSD 的效果等价(Read 回调迟迟不返回,
   IRP 生命周期被拉长,FileNode shared 锁持有跨整个时长),但绝对时长的来源不同。这不影响"谁挡谁"的结论,只影响具体 ms 数。

2. **~2806 vs 3000 的差**:探测在慢读 started 后 Sleep(200ms) 才发起,所以同文件 open 只需再等剩余 ~2800ms。属预期,非异常。

3. **M3(kernel cache)的边界必须说清**:本 repro 测的是**冷 open**(全新句柄、数据不在 cache)撞上在途慢读,这条路径不吃
   FspFsvolReadCached 的"函数内释放锁"快径,所以 cache 开也照挡。我**没有**在本 repro 里单独构造"文件已在 kernel cache、
   后续读命中 cached 路径"的对照(那需要先完整读一遍 warm 起来、且 tail 数据真进 cache)。因此我只能断言:
   **"冷 open 撞在途慢读"在 cache on/off 下都挡**;对"warm cache 命中读是否也持锁"这个更细的问题,本实验**未直接测量**,
   不做结论。若需要,可加一个"先顺序读满 → 再并发 warm 读 tail"的探测再测。

4. **guard 策略**:结论里用到"默认 FINE 下 Read 不取 guard、FILE_OPEN 取 shared"。这是读 WinFsp 源码(fsop.c/fs.c)得出的,
   本 repro 未主动改 guard 策略(默认即 FINE),与 memfs 默认一致。若 ZipDrive 的 winfsp-native 显式设了 COARSE,行为会更严
   (但那只会更容易复现串行化,不会推翻内核锁的结论)。

5. **数字是单机单次会话**。未跨重启、未变 CPU affinity(dispatcher 线程数默认按 affinity 取)。多线程 dispatcher 已默认启用
   (StartDispatcher(fs, 0)),所以"异文件不被挡"确实是 FSD per-FileNode 粒度,而非"只有一个 dispatcher 线程"造成的假象。

---

## 安全/清理确认

- 全程 **UNC 挂载,零盘符**。每个实验唯一 prefix,`finally` 块强制 `Stop-Process` + `fsptool lsvol` 校验。
- 所有 6 次矩阵运行结束均打印 `cleanup OK: no residual`。
- 报告写完时复查:`fsptool lsvol` 空,无 `winfsp-c-repro` / `memfs` 残留进程。
- 一次只挂一个 FS,内存 FS、只读,未碰真实文件/盘。

---

## 原始输出(代表性逐次)

### M1 blocking / 无 cache(split open/read,3 次)
```
tail read (video.bin tail, slow)     :   3006.7ms
SAME-file  OPEN (video.bin)          :   2806.6ms  <== BLOCKED (serialized)
SAME-file  HEAD read (video.bin @0)  :      0.1ms  OK (not blocked)
OTHER-file OPEN (other.bin)          :      1.2ms  OK (not blocked)
OTHER-file HEAD read (other.bin @0)  :      0.1ms  OK (not blocked)
（run2/run3 同,OPEN 2805.1 / 2805.4）
```

### M2 PENDING(async) / 无 cache(split,3 次)
```
tail read (video.bin tail, slow)     :   3005.2ms
SAME-file  OPEN (video.bin)          :   2806.0ms  <== BLOCKED (serialized)
SAME-file  HEAD read (video.bin @0)  :      0.1ms  OK (not blocked)
OTHER-file OPEN (other.bin)          :      1.4ms  OK (not blocked)
OTHER-file HEAD read (other.bin @0)  :      0.1ms  OK (not blocked)
（run2/run3:OPEN 2806.3 / 2800.9）
```

### M3 blocking / WITH kernel cache t=1000(split,3 次)
```
tail read (video.bin tail, slow)     :   3010.3ms
SAME-file  OPEN (video.bin)          :   2805.7ms  <== BLOCKED (serialized)
SAME-file  HEAD read (video.bin @0)  :      0.1ms  OK (not blocked)
OTHER-file OPEN (other.bin)          :      1.0ms  OK (not blocked)
OTHER-file HEAD read (other.bin @0)  :      0.1ms  OK (not blocked)
（run2/run3:OPEN 2805.5 / 2803.7）
```

### 早期未拆分版(open+read 合计)也一致
```
M1: SAME open+head ~2807ms BLOCKED, OTHER ~1.4ms OK   (4/4 runs)
M2: SAME open+head ~2807ms BLOCKED, OTHER ~1.4ms OK   (4/4 runs)
M3: SAME open+head ~2800ms BLOCKED, OTHER ~1.4ms OK   (4/4 runs)
```

---

## Warm kernel cache 补充实验

> 追加动机:上一轮 M3 用 `--timeout=1000` **根本没触发** FSD 缓存读路径。源码硬前提:
> `FspFsvolReadCached`(read.c)断言 `FileInfoTimeout == FspTimeoutInfinity32`,
> 而 `FspTimeoutInfinity32 = (UINT32)-1 = 0xFFFFFFFF`(driver.h:502)。1000 ≠ 无穷 → 读仍走 non-cached 持锁路径。
> 本节用**真正的** kernel cache(`FileInfoTimeout = 0xFFFFFFFF`,即 memfs `-t -1`)正面测。

### 核心问题的一句话结论
**开了真正的 kernel cache(FileInfoTimeout=∞)后,一个 cache-miss 的慢尾读进行时,同文件的 OPEN 仍然被挡 ~2800ms —— kernel cache 救不了 open。**
→ warm cache 这条路**死**。卡的是 open 在内核 create-completion 侧拿 Main **exclusive**(create.c:1326),
撞上慢读持有的 Main **shared**(read.c:224→292→310,跨用户态往返不放),与元数据/数据是否 warm **无关**。
三重确证升级为:**WinFsp 下"留在 WinFsp 又不改锁模型"无解**,P0 只能是 partial return / 退 Dokan / 接受。

### 代码改动(本节新增)
- `repro.c` 加 `--infinite-cache`(或 `--timeout=-1`):`vp.FileInfoTimeout = 0xFFFFFFFF`。这是**唯一**能开 FSD 缓存读的值。
  (`*TimeoutValid` 覆盖位全留 0,故 FileInfoTimeout 同时统管 FileInfo/Security/VolumeInfo/DirInfo,与 memfs 一致。)
- `repro.c` 加 `--slowAll`:让 video.bin **所有** offset 的读都慢(场景 C),不止尾读。
- 启动打印新增 `kernel cached-read path: ENABLED/DISABLED` 明确标注是否满足 ∞ 前提。
- `probe-warm.cs`:场景 A(同 offset 读两次)、场景 B(先 warm 元数据 → 唯一 offset 慢尾读在途 → 并发同/异文件 open,拆分计时)。
  **关键**:场景 B 每次 run 用**唯一** tail offset(32MB + r×1MB),否则 ∞ cache 下第二次同 offset 尾读会命中缓存、不再是 miss。
- `run-warm.ps1`:∞/finite 挂载 + `--debug`,并从 repro stderr **计数每 run 实际派发的 Read 回调**(区分 head/slow),
  用回调计数作为"cache 是否真生效"的 ground truth,而非只看时延。全程唯一 prefix + finally 强制清理。

### 场景 A — 先证 kernel cache 真的生效(B/C 的前提)
∞ cache 挂载,对 video.bin 的 HEAD(offset 0)读两次;用 repro `--debug` 日志数 Read 回调派发数。

| run | HEAD#1 | HEAD#2 | 该 run 内 video.bin 的 Read 回调派发数(head offset0) |
|---|---|---|---|
| 1 | 10.6ms | 4.3ms | **1**(第一次冷读派发用户态) |
| 2 | 4.8ms | 4.7ms | **0**(offset0 数据已在 run1 进 kernel cache,两次读**全**命中,零派发) |

**判据达成**:run 2 的 offset-0 读**零** Read 回调 → `FspFsvolReadCached`+`FspCcCopyRead` 纯内核拷贝、不派发用户态、
锁瞬间放。**kernel cache 确实生效**。B/C 有意义。

### 场景 B(核心)— cache-miss 慢尾读持锁期间,同文件 OPEN 是否还被挡
∞ cache 挂载。每 run:先 warm video.bin 元数据(open+getlen+close,快) → 唯一 offset 慢尾读在途 → 并发同文件 OPEN+HEAD、异文件 OPEN。

| run | tailOffset | 慢尾读 | 同文件 OPEN(元数据已 warm) | 同文件 HEAD read | 异文件 OPEN | 该 run slow 派发数 |
|---|---|---|---|---|---|---|
| 1 | 34603008 | 3018ms | **2806ms ← 阻塞** | 2.2ms | 4.0ms | slow=1 |
| 2 | 35651584 | 3005ms | **2793ms ← 阻塞** | 0.0ms(命中 cache) | 4.3ms | slow=1 |
| 3 | 36700160 | 3017ms | **2805ms ← 阻塞** | 0.0ms(命中 cache) | 4.7ms | slow=1 |

**结论**:开了真正的 kernel cache、且 open 需要的元数据已 warm,同文件 OPEN **仍被挡 ~2800ms**。
- `slow=1` 每 run 都在 → 唯一 offset 尾读确实是真 cache miss、真派发用户态、真持锁 3s。
- run 2/3 的同文件 HEAD read 命中 cache(0.0ms、零派发),**但 OPEN 照挡** → 干净地隔离出:
  **挡的纯粹是 open 拿 Main exclusive,与 open 后要读的数据是否在 cache 无关**。
- 异文件 open 全程 4-5ms 不受影响 → per-FileNode 粒度不变。

### 场景 C 对照 — 是否任何 cache-miss 慢读都挡 open(不止尾读)
∞ cache + `--slowAll`(video.bin 所有 offset 读都慢)。

| run | 同文件 OPEN | 同文件 HEAD read(offset0) | 说明 |
|---|---|---|---|
| 1 | **2791ms ← 阻塞** | **3010ms ← 阻塞**(offset0 首读=miss=慢派发) | head 本身也是慢 miss,自身也被挡 |
| 2 | **2808ms ← 阻塞** | 0.0ms(offset0 已在 run1 warm) | head 命中 cache,但 OPEN 仍被唯一 offset 尾读挡 |

**结论**:挡 open 的不是"尾"这个位置,而是"**任何在途的 cache-miss 慢读**"持着该 FileNode 的 Main shared。
tail vs head 只影响**探测自己的 head read**是否命中 cache;open 被挡来自"当时在途的那个慢读",与其 offset 无关。

### 机制(白盒,已核对源码)
- 缓存读路径 `FspFsvolReadCached`:read.c:224 拿 Main **shared** → read.c:292 `FspCcCopyRead` →
  read.c:310 释放。**cache 命中**时 `FspCcCopyRead` 纯内核拷贝、微秒返回、锁瞬放(=场景A run2 的零派发);
  **cache miss** 时它在内核内等分页读回来,shared 锁**跨整个等待**持有。
- open 完成侧 `FspFsvolCreateTryOpen`:create.c:1326 拿 Main **exclusive**。这步在**内核 create-completion** 里做,
  无论用户态 Open 是否被派发、无论元数据是否 warm,都要拿。exclusive 撞在途 shared → open 冻结,直到慢读放锁。
- 所以 kernel cache 只能让"**已返回过的字节**的后续读"走内核快径不持锁;对"**第一次**读某 offset(必 miss)"
  与"该 miss 在途时的同文件 open"这两件事,cache **无能为力**。

### 与 ZipDrive / Photos 的对应(诚实标注)
- kernel cache 只能缓存**已返回过**的字节。Photos 首次拉某压缩视频的 moov / 某 offset 一定是**第一次读=cache miss**,
  ZipDrive 惰性 chunked 解压该 offset 需要几十秒 → 这正是本节场景 B 建模的情况。所以 warm cache 对**首次卡死**帮不上。
- repro 用 `Sleep(3000)` 模拟解压耗时,对 FSD 的效果等价(Read 回调迟迟不返回 → shared 锁跨整个时长)。
- 唯一没测且理论上 cache 能帮的场景:**同一 offset 第二次被读**(已 warm)。那次读确实走内核快径不持锁(场景A run2 已证)。
  但它不解决"首次读该 offset 时同文件 open 被挡"——而后者才是 Photos 卡死的实际场景。

### 未决/边界
- 场景 C run1 的 FSD `slow=3`(不止预期的尾读+head)可能含 warm-up 的 getlen 触发的读或重定向器 readahead;
  未逐一归因,但不影响主结论(每 run 都有真 slow 派发、open 都被挡)。
- 未测"open 与慢读到达顺序反过来"(先 open 拿到 exclusive、慢读再来)——那是另一个方向,当前问题是慢读先在途、open 后到,
  与 Photos 场景一致。
- 数字单机单会话;∞ cache 下多 run 靠唯一 offset 保证每次 miss,已在 FSD 回调计数里验证(slow≥1/run)。

### 安全/清理
- 全程 UNC 挂载零盘符,唯一 prefix,finally 强制 Stop-Process + `fsptool lsvol` 校验;所有 run 打印 `cleanup OK: no residual`。
- 补充实验结束复查:`lsvol` 空,无 `winfsp-c-repro`/`memfs` 残留进程。

### 一句话给协调者
**开 kernel cache(FileInfoTimeout=∞)不能让同文件 open 在慢尾读期间不被挡——open 照样冻 ~2800ms,因为卡点是内核 create 拿 Main exclusive 撞上慢读持的 Main shared,与 cache 无关;warm-cache 这条"留在 WinFsp"的路走死。**
