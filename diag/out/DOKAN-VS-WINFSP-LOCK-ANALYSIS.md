# Dokan vs WinFsp —— 同文件读串行化的**代码级**根因分析

> 详细支撑文档(源码逐行 + Dokan 实测)。权威摘要见 `FINAL-REPORT.md`。
> 核心问题:**"Dokan 也是 Windows 文件系统驱动,一样走 I/O Manager,为什么它不卡?"**
>
> 结论由**两个驱动的内核源码逐行比对 + Dokan 侧实测**得出(非推断)。
> 源码位置:`Q:\MyProjects\zipdrive-source\{winfsp,dokany,dokan-dotnet,winfsp-native}`
> 对照 repro:`diag/dokan-slowread-repro/`。

---

## 0. TL;DR(一句话)

**两个驱动都用 I/O Manager,没有谁"绕过"它。** 真正的差异是:

- **WinFsp 的 FSD 在一个"读"IRP 上,把 FileNode 的 Main 资源锁(shared)横跨整个用户态往返持有** —— 读多慢,锁就持有多久(视频尾读几十秒)。
- **Dokan 的 FSD 在把读 IRP 入队(PENDING)之前就释放了 FCB 锁** —— 用户态解压那几十秒里,Dokan 对这个文件一把锁都不持有。

被这把长命 shared 锁挡住的**不是别的读**(shared+shared 兼容),而是**同一文件上任何需要 Main 独占锁的操作** —— 最典型的就是 Windows「照片」为读这个视频而发起的**第二次 CreateFile(open)**,它在完成阶段要拿 Main **exclusive**,于是被阻塞到解压结束,表现为整窗口冻死。

这是**两个驱动各自的实现选择**,不是 I/O Manager 层的行为差异。

---

## 1. 两个曾经的误判(已记入 FINAL-REPORT 第 6 节)

早期一度把根因归为"Windows I/O Manager 对同步 handle 的 per-file FCB 锁"。这个框定**不准确**,两点证伪:

1. I/O Manager 的同步 handle 锁是 **per-FILE_OBJECT(per-handle)**,只串行化同一 handle 的连续 IRP,**不会**让两个不同 handle 互堵。而决定性 repro 里慢尾读和被堵的头读用的是**两个不同 handle**,两个 handle 互堵 → 元凶不可能是它。
2. 这把 per-handle 锁 **Dokan 也有**(`dokany/sys/read.c:152` 一样读 `FO_SYNCHRONOUS_IO`)。两边都有的机制不可能解释差异。

=> 差异只能来自**跨 handle 的 FSD 层锁竞争**,即下面第 2 节的真实区别。

---

## 2. 决定性代码证据:锁的生命周期不对称

### 2.1 Dokan —— 读期间**不持有**任何 FCB 锁

文件:`Q:\MyProjects\zipdrive-source\dokany\sys\read.c`,函数 `DokanDispatchRead`

```c
167:  DokanFCBLockRO(fcb);                        // 拿 shared FCB 锁(只为构造 event context)
168:  fcbLocked = TRUE;
...
239:  // register this IRP to pending IPR list and make it pending status
240:  status = DokanRegisterPendingIrp(RequestContext, eventContext);  // 入队 → 返回 STATUS_PENDING
241:  } __finally {
242:    if (fcbLocked)
243:      DokanFCBUnlock(fcb);                    // ★ 立刻释放,早于用户态处理
244:  }
245:  return status;                              // = STATUS_PENDING
```

- `DokanFCBLockRO` = **shared** 锁(`dokan.h:612`,`DokanResourceLockWithDebugInfo(FALSE, ...)`,FALSE=shared)。
- 锁只在**构造 event context** 期间持有,IRP 一入队(`DokanRegisterPendingIrp` → `IoMarkIrpPending`,`event.c:266/301`)就在 `__finally` 里**马上释放**。
- 用户态(ZipDrive 解压视频)那几十秒里,Dokan 内核侧对这个 FCB **零持锁**。
- 设备 I/O 类型:`DO_DIRECT_IO`(`init.c:1076`, `fscontrol.c:882`)。

### 2.2 WinFsp —— 读锁被**转移给异步 Request,横跨整个用户态往返**

文件:`Q:\MyProjects\zipdrive-source\winfsp\src\sys\read.c`,函数 `FspFsvolReadNonCached`

```c
355:  /* acquire FileNode shared Full */
356:  Success = DEBUGTEST(90) &&
357:      FspFileNodeTryAcquireSharedF(FileNode, FspFileNodeAcquireFull, CanWait);  // shared: Main + PagingIo
...
444:  FspFileNodeSetOwner(FileNode, Full, Request);   // ★ 把锁「所有权」从当前线程转移给异步 Request
445:  FspIopRequestContext(Request, RequestIrp) = Irp;
...
461:  return FSP_STATUS_IOQ_POST;                     // 把 IRP 派给用户态队列(FspIoqPostIrp)
```

释放点在**完成回调**里,即用户态读**返回之后**:

```c
// read.c:666  (FspFsvolReadNonCachedRequestFini —— request 完成时才跑)
666:  if (0 != Irp) {
669:    FSP_FILE_NODE *FileNode = IrpSp->FileObject->FsContext;
671:    FspFileNodeReleaseOwner(FileNode, Full, Request);   // ★ 只在用户态读完成后才释放
672:  }
```

- `FspFileNodeAcquireFull` = Main(1) + PagingIo(2) 都拿(`driver.h:1650-1652`),都是 **shared**(`file.c:432/440`,`ExAcquireResourceSharedLite`)。
- `FspFileNodeSetOwner`(`file.c:533`)底层 `ExSetResourceOwnerPointer` —— 把 shared 锁归属转给 Request,使锁能"跨线程存活",dispatch 线程返回后**不释放**。
- 结果:视频尾读几十秒,WinFsp 就把这个 FileNode 的 Main shared 锁**持有几十秒**。

---

## 3. 为什么"持有 shared 锁"会冻住「照片」

关键:**shared + shared 兼容**,所以两个并发读**不会**互堵。真正被这把长命 shared 锁挡住的是**需要 Main exclusive 的操作**。

最典型的 exclusive 需求 = **第二次 open(CreateFile)同一文件**。Windows「照片」为读这个视频会再开 handle,其完成阶段:

```c
// winfsp/src/sys/create.c:1326  (FspFsvolCreateTryOpen)
1325:  Success = DEBUGTEST(90) &&
1326:      FspFileNodeTryAcquireExclusive(FileNode, Main) &&   // ★ open 完成阶段要 Main 独占
1327:      FspFsvolCreateOpenOrOverwriteOplock(Irp, Response, &Result);
```

exclusive 与被长期持有的 shared **不兼容** → 第二次 open **阻塞**到慢读结束 → UI 冻死几十秒。

Dokan 侧:读期间不持锁(§2.1),第二次 open 的 FCB 锁(`create.c:1310` `DokanFCBLockRW`,只在完成阶段短暂持有)完全不受慢读影响 → 不卡。

### 3.1 与 repro 数字精确吻合

`SerializeExperiment.cs` 里所谓"concurrent HEAD SAME file"其实是 **`File.OpenHandle` + 一次读**:
- **BLOCKING**:慢尾读 3011ms;第二次 open+read 被挡 **2850ms**(≈3000 − 150ms 起跑差)。
- **PARTIAL-RETURN**:慢读 300ms 就返回释放锁;第二次 open+read 只等 **145ms**。

完全对得上"open 被长命 shared 锁挡住,锁一放开 open 立刻推进"。

---

## 4. 对 partial-return 修复方向的机理

partial-return(让单个读快速返回已就绪字节、不长期持锁)的机理:

> 读一旦快速完成 → `FspFsvolReadNonCachedRequestFini` 触发 → `FspFileNodeReleaseOwner`(read.c:671)立刻释放 Main shared 锁 → 第二次 open 拿到 exclusive → 「照片」推进。

要点:收益**不是**"让并发读不互堵"(它们本就 shared 兼容不互堵),**而是**"尽快释放 Main shared 锁,让同文件的 open 等 exclusive 操作不被饿死"。

> ⚠️ **但后续 D4 实测证明 partial-return 只缓解、不根治**:纯尾读(moov)无 partial 可返回,多并发下 open 仍卡 ~2s。裁决见 `FINAL-REPORT.md` 第 5 节。

---

## 5. 待实测验证(Dokan 侧对照)

以上 §2/§3 的 Dokan 侧目前是**纯源码论证**;WinFsp 侧已有 `SerializeExperiment` 实测(2850ms vs 145ms)。
为闭环,建 `diag/dokan-slowread-repro/`:把**同一个 serialize 实验**挂到 Dokan 上,预期观测到:

> **Dokan 上,慢尾读进行时,同文件的第二次 open+read 不被显著阻塞**(与 WinFsp 的 2850ms 形成对照)。

⚠️ 混淆变量必须排除:Dokan 的 `ReadFile` 是**同步回调**,一个慢读占一个 dispatcher 线程。
`SerializeExperiment` 只有 **1 个**慢读,只要 Dokan dispatcher 线程 ≥ 2,就不会因线程耗尽而假阳性。
repro 会同时报告线程数,并在多种 threadCount 下测,以区分"FCB 锁差异"与"线程耗尽"。

见 `diag/dokan-slowread-repro/README.md`。

---

## 6. ✅ 实测结果(已闭环 —— 源码论证被实验证实)

repro `diag/dokan-slowread-repro/` 已在装了 Dokany 2.x 的机器上跑通(32 核,无报错、无挂起、无僵尸挂载)。

| 配置 | 场景 | tail read | 同文件并发 HEAD | 判读 |
|---|---|---|---|---|
| **default(多线程)** | BLOCKING | 3012ms | **1.3ms** | 不堵 |
| | PARTIAL | 307ms | **1.1ms** | 不堵 |
| **--threadCount=4** | BLOCKING | 3014ms | **1.2ms** | 不堵 |
| | PARTIAL | 303ms | **1.1ms** | 不堵 |
| **--threadCount=1** | BLOCKING | 3014ms | **2852ms** | **堵** |
| | PARTIAL | 306ms | **144ms** | 不堵 |

**对照 WinFsp 同实验**:BLOCKING 场景同文件并发 HEAD = **2850ms(堵)**。

### 结论

1. **Dokan 给 ≥2 个 dispatcher 线程时,同文件第二次 open+read 在慢读进行中只要 ~1ms** —— Dokan 的 FSD **不**把 FCB 锁跨用户态往返持有。与 §2.1 源码完全吻合。

2. **`--threadCount=1` 时出现的 2852ms 阻塞不是 FCB 锁,而是 dispatcher 线程耗尽** —— Dokan 的 `ReadFile` 是**同步回调**,单线程时那唯一的 dispatcher 线程被 `Thread.Sleep` 慢读占死,第二个读根本没机会派发。给到 2 线程就消失。这正是实验特意用"单个慢读 + 多档 threadCount"要隔离的混淆变量,它干净地被区分开了。

   > ⚠️ 这条对真实 ZipDrive **不适用**:ZipDrive 走异步(STATUS_PENDING),慢读不占 dispatcher 线程。这里的单线程阻塞纯粹是本 repro 用同步 `Thread.Sleep` 制造的 artifact。

3. **两边唯一真实差异**因此坐实为 §2 的锁生命周期:**WinFsp 跨用户态往返持有 FileNode Main shared 锁(read.c:444/671),Dokan 入队前就释放(read.c:242)。**

### 对 P0 修复的验证

PARTIAL 场景(模拟"超时返回部分字节")在**所有**配置下同文件 HEAD 都快速返回(1.1ms / 144ms),包括单线程。这实测确认了修复方向有效:**读快速返回 → 锁快速释放 / 线程快速归还 → 同文件后续操作不被饿死**。

