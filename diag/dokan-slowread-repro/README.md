# dokan-slowread-repro — Dokan 侧同文件读串行化对照实验

对照 `../winfsp-slowread-repro/SerializeExperiment.cs`。回答权威报告(`../out/FINAL-REPORT.md`)里的核心问题:
**Dokan 也走 Windows I/O Manager,为什么它不卡?** —— 用实测把 §2/§3 的源码论证闭环。

代码级论证见 `../out/DOKAN-VS-WINFSP-LOCK-ANALYSIS.md`。

## 安全约束(重要)
- **挂到临时空目录,绝不挂盘符**:`options.MountPoint = %TEMP%\dokan-serialize-<guid>`。
  目录挂载崩了也不会留下僵尸盘符,不会把系统拖死。
- **只读卷**:`DokanOptions.WriteProtection`,所有写操作返回 AccessDenied。
- **确定性卸载**:`instance.Dispose()`(= DokanCloseHandle + 等 dismount),再删临时目录。
- 内存 FS,不碰真 zip、不碰真盘。

## 前置
- .NET 10 SDK
- **Dokany 2.x 驱动已安装**(本 repro 通过 ProjectReference 引用本地 `zipdrive-source/dokan-dotnet` 源码 DokanNet 2.3.0.4;运行时需要内核驱动)。
- 已 `dotnet build -c Release` 通过(骨架已验证可编译)。

## 运行
```bash
cd diag/dokan-slowread-repro
dotnet build -c Release

# 默认(多线程 dispatcher)
dotnet bin/Release/net10.0/DokanSlowReadRepro.dll

# 排除线程耗尽混淆:给 4 线程 / 强制单线程
dotnet bin/Release/net10.0/DokanSlowReadRepro.dll --threadCount=4
dotnet bin/Release/net10.0/DokanSlowReadRepro.dll --threadCount=1

# 需要 Dokan 内核日志时加 --debug
dotnet bin/Release/net10.0/DokanSlowReadRepro.dll --debug
```

## 预期结果(若源码论证成立)
```
── BLOCKING tail read (current ZipDrive behavior) ──
  tail read (video.bin)            :   3000ms
  concurrent HEAD SAME file (sync) :    ~1ms  OK (NOT blocked — matches Dokan prediction)
```
对照 WinFsp 同实验的 `2850ms  <== BLOCKED`。

**判读**:
- HEAD-same-file 保持 ~ms(慢读进行中)=> Dokan **不**串行化同文件读 => 证实"锁只在 WinFsp 侧被长期持有"。
- 若 HEAD-same-file 也涨到接近 3000ms => Dokan **也**堵 => 假说错,需重新诊断。
- `--threadCount=1` 若单独变堵、`--threadCount=4` 不堵 => 那是**同步回调的 dispatcher 线程耗尽**(Dokan 特有,因为它的 ReadFile 是同步的),不是 FCB 锁;实验只发 1 个慢读就是为了让 `>=2` 线程时不受此影响。

## 混淆变量说明
Dokan 的 `ReadFile` 回调是**同步**的(不像 WinFsp/ZipDrive 走 STATUS_PENDING 异步)。一个 `Thread.Sleep` 慢读会占住一个 dispatcher 线程。本实验**同一时刻只有 1 个慢读**,所以只要 Dokan 有 >= 2 个 dispatcher 线程,同文件第二次 open+read 若仍被堵,只能归因于内核 FCB 锁,而非线程耗尽。`--threadCount` 用来把两者分开。
