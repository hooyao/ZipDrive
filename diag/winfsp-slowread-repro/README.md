# winfsp-slowread-repro — 最小 WinFsp 复现实验

用来定位并验证 ZipDrive「Win11 照片浏览 ZIP 内视频时整窗口卡死几十秒」的 root cause。
**不挂真 zip、不挂真盘符** —— 用 WinFsp 的 UNC mount(`host.Prefix=\winfsp-...` + `host.Mount(null)`），
崩溃也不会僵死系统(照抄 RamDrive 集成测试的安全挂载方式)。

## 前置
- .NET 10 SDK(`global.json` pin 10.0.103,rollForward latestFeature)
- 已安装 WinFsp(https://winfsp.dev/rel/,装 Developer files)
- `WinFsp.Native 0.1.3-pre.3`(ZipDrive 同版本;应已在全局 NuGet 缓存)

## 构建 + 运行
```bash
cd diag/winfsp-slowread-repro
dotnet build -c Release

# 实验 B(决定性):同一文件的读会不会被一个慢读串行化堵住
dotnet bin/Release/net10.0/SlowReadRepro.dll serialize

# 实验 A:慢读会不会堵「别的文件」(派发线程/隔离)
dotnet bin/Release/net10.0/SlowReadRepro.dll AsyncDelay  --threadCount=4 --slowConcurrency=8 --slowDelayMs=2000
dotnet bin/Release/net10.0/SlowReadRepro.dll ThreadSleep --threadCount=4 --slowConcurrency=8 --slowDelayMs=2000
dotnet bin/Release/net10.0/SlowReadRepro.dll SyncOverAsync --threadCount=4 --slowConcurrency=8
dotnet bin/Release/net10.0/SlowReadRepro.dll SlowInOpen  --threadCount=4 --slowConcurrency=8
```

## 文件
- `SlowFs.cs` — 最小内存 FS:`slow.bin`(读时延迟)+ `fast-*.bin`(秒回)。
  4 种慢模式:AsyncDelay / ThreadSleep / SyncOverAsync / SlowInOpen(慢在 OpenFile 同步回调)。
- `SerializeExperiment.cs` — 实验 B + P0 修复验证。FS 有 `video.bin`(尾读慢)+ `other.bin`(秒回);
  `partialFallback=true` 模拟修复(超时返回部分数据)。
- `Program.cs` — 驱动 + 延迟统计(p50/p99/max)。

## 关键结论(期望输出)

### 实验 B(`serialize`)—— 决定性
```
── BLOCKING tail read (current ZipDrive behavior) ──
  tail read (video.bin)           :   3011ms
  concurrent HEAD SAME file (sync):   2850ms  <== BLOCKED (Photos would freeze)   ← 同文件被串行化堵死
── PARTIAL-RETURN tail read (proposed P0 fix) ──
  tail read (video.bin)           :    300ms
  concurrent HEAD SAME file (sync):    145ms  OK (Photos stays responsive)        ← 快速返回就不卡
```
外加(旧输出保留在 git 历史):overlapped handle 同文件读 1.8ms 不堵;不同文件 0.7ms 不堵。
=> **Windows I/O Manager 对同步(non-overlapped)handle 的同一文件读加 per-file FCB 锁串行化。**

### 实验 A —— 排除项
AsyncDelay 8 个慢读背景下 fast 读 p99≈2.3ms（不同文件不受影响）=> 排除派发线程耗尽。
只有 `SlowInOpen`（慢在同步回调 OpenFile）在 threadCount 小于慢操作数时会堵——但真实 dump 证明
ZipDrive 的 OpenFile 不慢（dumpasync 无同步回调挂起），故非本例 root cause。

详见 `../out/ROOT-CAUSE-CONFIRMED.md`。
