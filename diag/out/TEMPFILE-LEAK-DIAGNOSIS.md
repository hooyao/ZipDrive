# 临时文件残留诊断 —— 交接文档(换机器续接用,自包含)

> **问题**:解压(extract)大文件时关闭 ZipDrive(Ctrl+C 优雅关闭),`%TEMP%\ZipDrive-{pid}\` 里的 sparse backing file 残留;关闭时出现 `Failed to delete chunked cache backing file` 的 log。**旧 Dokan 版删得干干净净,WinFsp 版残留。**
>
> **用户诉求**:让 WinFsp 版和 Dokan 版行为一致。**明确不要 `FileOptions.DeleteOnClose` 这种新机制。**
>
> **诊断分支**:`feat/winfsp-migration`(WinFsp 代码 + 全部诊断)。对照 Dokan 版 = commit `0af681e`,WinFsp 版 = `7b5bceb`(= 本分支 caching/清理相关文件)。

---

## 0. 状态

**根因已由 git 逐行比对定位(高置信),但"触发机制"需一次实测坐实(看关闭日志)。** 修复方向已明确且对齐"两版一致 + 不用 DeleteOnClose"。下一步:实测坐实 + 实现修复。

---

## 1. 根因(一句话)

**清理代码两版逐字节相同 —— 残留不是 caching 层的代码回归。** 唯一 WinFsp 独有的、git 可证的差异在**关闭时序 / 清理时间预算**:

- 删 backing file 的唯一时机 = `CacheMaintenanceService.ExecuteAsync` 的**收尾块**(`stoppingToken` 取消后跑 `_fileCache.Clear()` → 取消 extraction + 等 writer 关闭 + 删文件 → `DeleteCacheDirectory()`)。
- `CacheMaintenanceService` 最后注册 → **最后停止**。它前面先跑 `WinFspHostedService.StopAsync` 的 `_host.Dispose()`(WinFsp 卸载)。
- **WinFsp 卸载疑似撞上已归档的 Photos/SMB hang(同分支根因),吃满默认 5 秒 ShutdownTimeout** → 轮不到 `CacheMaintenanceService` 收尾 → extraction writer 句柄没被取消、backing file 没被删 → **残留**。
- Dokan 版关闭走 `WaitForFileSystemClosedAsync`,StopAsync 阶段不长阻塞,收尾清理有时间跑完 → 删得干净。

**引信两版共有,点火只在 WinFsp**:extraction 的 writer `FileStream`(`FileAccess.Write, FileShare.Read`,**无 `FileShare.Delete`**)在整个 extract 期间开着;只要它没在删除前关闭,`File.Delete` 必抛 sharing violation → 残留。两版都有这个引信,但只有 WinFsp 的关闭卡顿会抢走"等 writer 关闭 + 删文件"的时间。

---

## 2. 证据链(全部 git diff 可证)

### 2.1 清理代码两版等价甚至更强(证伪"caching 代码回归")

| 项 | Dokan `0af681e` | WinFsp HEAD | 方向 |
|---|---|---|---|
| 等 extraction writer 停 | `ExtractionTask.Wait(2s)` | `Wait(5s)` | WinFsp 更宽容 |
| 删 backing file | 单次 `File.Delete`+catch | `DeleteBackingFileWithRetry`(5×,Sleep25,抗 IOException/UnauthorizedAccess) | WinFsp 更健壮 |
| 删除职责位置 | `ChunkedFileEntry.Dispose` | `ChunkedDiskStorageStrategy.Dispose` | 纯搬迁,语义等价 |
| `ExtractAsync` writer FileStream | `FileMode.Open,FileAccess.Write,FileShare.Read,buf81920` | **逐字节相同** | — |
| `GenericCache.Clear` / `ClearAsync` | | **逐字节相同** | — |
| `ArchiveVirtualFileSystem.UnmountAsync` | 只清 structureCache+archiveNodes,**不动 fileContentCache** | **逐字节相同** | — |
| `ArchiveNode.Dispose`(drain) | 只 `_drainCts.Dispose()`,**不取消 extraction** | **逐字节相同** | — |
| `CacheMaintenanceService` 收尾 | Clear→DeleteCacheDirectory | 纯 CRLF 差异,内容相同 | — |

### 2.2 reader 不背锅(两版一致)

`FileContentCache.ReadAsync`(HEAD:117)用 `using ICacheHandle<Stream>` 每次读 borrow+dispose,`CacheHandle.Dispose` 会 dispose 掉 `ChunkedStream`(关 reader FileStream)。两个 adapter 都不在 handle context 里缓存长命 stream。**关闭时唯一 linger 的句柄是 extraction writer。**

### 2.3 唯一 WinFsp 独有差异 = 停止信号机制(git 可证)

```
Dokan  0af681e  DokanHostedService.ExecuteAsync 尾:
   await _dokanInstance.WaitForFileSystemClosedAsync(uint.MaxValue);  // 驱动事件驱动
   StopAsync: RemoveMountPoint + _dokanInstance.Dispose() + _dokan.Dispose()

WinFsp 7b5bceb  WinFspHostedService.ExecuteAsync:186:
   await Task.Delay(Timeout.Infinite, stoppingToken);   // 纯等 token
   StopAsync:208  _host?.Dispose()   // WinFsp 卸载,疑似 Photos-hang 卡点
```

共同前提(两版都成立,已 grep 确认):全仓库无 `ShutdownTimeout`/`ConfigureHostOptions`/`ServicesStopConcurrently`/`UseConsoleLifetime` 覆盖 → **默认 5 秒 ShutdownTimeout、HostedService 逆序串行停止**。注册顺序两版相同(`CacheMaintenanceService` 先注册 → 最后停)。

---

## 3. 待实测坐实(换机器后第一步,不改代码)

复现"解压大文件时 Ctrl+C 关闭"后,查关闭日志尾部三条 Information:
1. `CacheMaintenanceService stopped`
2. `Final cache cleanup completed`(= `_fileCache.Clear()` 跑完)
3. `Deleted cache directory: ...`(= `DeleteCacheDirectory()` 跑完)

**判读**:
- 三条**缺失/不全** → **坐实根因**:收尾清理被 5s ShutdownTimeout(被 WinFsp 卸载卡顿耗尽)打断。与"Dokan 正常、WinFsp 残留"完全吻合。走第 4 节修复。
- 三条**都在**但仍残留 → 回头查 `Wait(5s)` 没等到 writer(此时才是 caching 层,但两版同源,应两版都复现;可在 Dokan 版做对照)。

也可给 `WinFspHostedService.StopAsync` 的 `_host.Dispose()` 前后加时间戳日志(诊断用),直接看 WinFsp 卸载耗时是否吃满 5s。

---

## 4. 修复方向(对齐"两版一致 + 不用 DeleteOnClose")

核心思路:**别让临时文件清理依赖"关闭时的剩余时间预算"** —— 让它在关闭早期、或不被 WinFsp 卸载卡顿拖累地完成。三个候选(按推荐):

### 方案 A(推荐)—— 清理不再压在"最后停止的 CacheMaintenanceService 收尾"
把"取消所有 extraction + 删 backing file + 删 cache 目录"提前到**卸载卷之前**或**独立于 shutdown 预算**执行。具体可选:
- 在 `WinFspHostedService.StopAsync` **开头**(卸载 `_host.Dispose()` 之前)主动触发一次 file cache 的 `Clear()` + `DeleteCacheDirectory()`(或经 VFS/DI 拿到 `IFileContentCache`)。这样即使卸载随后卡住,临时文件已经删了。
- ⚠️ 注意:清理要先**取消 extraction 并等 writer 关闭**(现有 `entry.Dispose()` 已做),否则删不掉。
- 与 Dokan 一致性:Dokan 版能删干净正是因为清理有充足时间;本方案把"充足时间"显式保证。

### 方案 B —— 抬高 ShutdownTimeout,让收尾清理有时间跑
`Program.cs` 加 `builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30))`(或更长)。让默认 5s 不再打断收尾清理。
- 优点:改动最小。
- 缺点:治标 —— 如果 WinFsp 卸载卡几十秒,关闭会明显变慢;且没解决"清理压在最后"的结构问题。

### 方案 C —— 让 UnmountAsync 主动取消 extraction + 清 file cache
`ArchiveVirtualFileSystem.UnmountAsync` 现在**不碰** file cache(两版都是)。可让它在 unmount 时就取消所有 in-flight extraction(经 ArchiveNode drain 或直接 cache.Clear),使 writer 尽早关闭。
- ⚠️ 要确认不影响正常读路径的语义(unmount 本就代表要关了)。

**都不需要 `FileOptions.DeleteOnClose`。** 推荐 A(结构上根治 + 保证时间)或 A+B 组合。实现后按 CLAUDE.md 工作流:Build → 写测试 → 跑测试 → Pass;并实测"解压大文件时关闭无残留"。

---

## 5. 关键文件索引

| 文件 | 关注点 |
|---|---|
| `src/ZipDrive.Infrastructure.Caching/ChunkedFileEntry.cs` | Dispose 229-249(Cancel+Wait5s,无 File.Delete);ExtractAsync writer FileStream |
| `src/ZipDrive.Infrastructure.Caching/ChunkedDiskStorageStrategy.cs` | Dispose 196-203;DeleteBackingFileWithRetry 206-225(残留 log 出处 :224);DeleteCacheDirectory 243 |
| `src/ZipDrive.Infrastructure.Caching/CacheMaintenanceService.cs` | ExecuteAsync 收尾块 67-88(Clear→DeleteCacheDirectory);这是删文件唯一时机 |
| `src/ZipDrive.Infrastructure.Caching/GenericCache.cs` | Clear 583(遍历 Dispose,无 refcount 检查) |
| `src/ZipDrive.Infrastructure.Caching/FileContentCache.cs` | ReadAsync 117(reader per-read borrow);Clear 188;DeleteCacheDirectory 203 |
| `src/ZipDrive.Infrastructure.FileSystem/WinFspHostedService.cs` | ExecuteAsync 186(Task.Delay Infinite);StopAsync 208(_host.Dispose 卸载) |
| `src/ZipDrive.Application/Services/ArchiveVirtualFileSystem.cs` | UnmountAsync 148-161(不清 file cache) |
| `src/ZipDrive.Cli/Program.cs` | 158/165 HostedService 注册顺序;169 host.RunAsync();无 ShutdownTimeout |
| 对照 | `git show 0af681e:src/ZipDrive.Infrastructure.FileSystem/DokanHostedService.cs`(WaitForFileSystemClosedAsync) |

---

## 6. 一句话给续接的人

清理代码两版一模一样,别去改删除逻辑;残留是因为 WinFsp 关闭卡顿(Photos-hang 同源)吃满 5s ShutdownTimeout,让最后停止的 `CacheMaintenanceService` 收尾清理没跑完。先实测看关闭日志那三条在不在坐实,然后按方案 A 把清理提前到卸载之前/独立于 shutdown 预算。不要用 DeleteOnClose。
