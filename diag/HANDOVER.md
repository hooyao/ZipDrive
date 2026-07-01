# HANDOVER — WinFsp Photos 卡死诊断(换机器续接用,自包含）

> 换机器后**先读这一个文件就够**。它自包含,链接到仓库内的证据/代码/复现。
> 用户全程用**中文**交流(tool use / 代码注释 / 内部推理用英文)。
> 用户是项目作者,技术强,验证驱动 —— 能实测/实验证明的不要靠读代码猜。我因过度断言被纠正过多次。

---

## 0. 一句话状态

**ROOT CAUSE 已用受控实验最终确证(不是推断)。修复方向(P0)也已实验验证有效。**
下一步:把 P0 修复实现进 ZipDrive 真实代码 + 加测试。诊断阶段结束。

---

## 1. 问题

Dokan→WinFsp 迁移(commit `7b5bceb`)后,挂载 NAS/SMB 上的 ZIP(含图片+视频,如 `GRAPHIS.Gals-14/16.zip`,
内有 400–610MB 的 mp4)。资源管理器转缩略图时,双击一张**已有缩略图的图片**:Win11「照片」能显示该图,
**随后整个窗口卡死无响应几十秒**,后台视频解压完就恢复。**旧 Dokan 版丝毫不卡。**
唯一变量是 presentation adapter(Dokan→WinFsp),共享的缓存/解压代码两边逐字相同。

---

## 2. ✅ ROOT CAUSE(实验确证)

**Windows I/O Manager 对「同步 handle(non-overlapped)」打开的文件,用 per-file 锁(FCB)把该文件的
所有读串行化。** ZipDrive 的一个「读」会一直阻塞到目标 chunk 顺序解压完成(视频要几十秒);在这几十秒里,
**同一个视频文件的其它读全被这把锁挡住**。Windows「照片」在自己的线程上顺序读这个视频,于是**卡死在自己的
读上**,直到解压完成、锁释放。

- **图片不卡**:图片是不同文件、小、走内存 tier 秒回;锁是 **per-file** 的,不波及。
- **Dokan 不卡**:Dokan 对同一文件允许并发读,不走 I/O Manager 这条同步串行化路径(WinFsp 作者原话)。

### 病灶的两层
1. **慢**(两边相同):读视频尾巴(moov atom 在文件末尾)→ 落在最后一个 chunk → mp4 是 **DEFLATE**
   (压缩比 0.996,白解压)只能顺序解压 → 读尾要等整文件(几十秒)。
2. **卡**(唯一差异 = 放大器):WinFsp 卷上,同步 handle 的同文件读被 I/O Manager per-file 串行化 →
   一个几十秒的慢读把照片对这个视频的所有后续读全堵死 → UI 冻结。Dokan 无此串行化。

---

## 3. 🧪 验证实验(仓库内,可重跑)——决定性证据

代码:**`diag/winfsp-slowread-repro/`**(自包含 .NET 10 项目,UNC mount,不挂真 zip/盘符)。
跑法见该目录 `README.md`。核心结论:

### 实验 B（`SlowReadRepro serialize`)—— 决定性
```
── BLOCKING tail read (当前 ZipDrive 行为) ──
  tail read (video.bin)           : 3011ms
  concurrent HEAD SAME file (sync): 2850ms  <== 同文件被 FCB 锁串行化堵死（照片会冻）
── PARTIAL-RETURN tail read (P0 修复) ──
  tail read (video.bin)           :  300ms
  concurrent HEAD SAME file (sync):  145ms  OK（照片保持响应）
```
另测(同一实验早期版本):同文件 **overlapped** handle 读 = 1.8ms(不堵);**不同文件** = 0.7ms(不堵)。
=> 串行化只发生在「同步 handle + 同一文件」。

### 实验 A（`SlowReadRepro AsyncDelay --threadCount=4 --slowConcurrency=8`)—— 排除项
8 个并发慢读(真异步)背景下 fast(不同文件)读 p99≈2.3ms,不受影响 => **排除派发线程耗尽**。
仅 `SlowInOpen`(慢在同步回调 OpenFile,threadCount<慢操作数)会堵——但真实 dump 证明 ZipDrive OpenFile
不慢,故非本例病因。

---

## 4. 🔬 dump 分析(仓库内 `diag/out/*.txt`;原始 .dmp 太大未入库,见第 8 节)

WinFsp 卡死 dump 5 张(22:12,`diag/dumps.winfsp/`,973MB 未提交)+ 预生成 SOS 报告(已提交):
- `diag/out/dump03-clrstack-all.txt`:卡死瞬间托管线程 15 个,**6 个 worker 空闲、1 个在解压,无一卡在回调/chunk-wait**。
- `diag/out/dump03-dumpasync-full.txt` + 5 张的 dumpasync 统计:**全程只有 `ReadFileAsync` 挂起(2/2/2/1/4 个),
  从没有 OpenFile/GetFileInfo/ListDirectory 等同步回调挂起。**
=> 卡死时 ZipDrive 托管侧**空闲**;请求堵在 ZipDrive 之外(内核 FCB 锁),没进 ZipDrive 回调。与「照片卡在自己
的读上」完全吻合。

### 日志(`diag/dumps/chunkwait-winfsp.log` / `chunkwait-dokan.log`,UTF-16)
- 两边**都**大量 chunk-wait BLOCK 读尾巴(needsChunk=总数-1,extractedChunks=1);Dokan 甚至等更久(5.6–7.3s)却不卡。
  => chunk-wait 本身**不是**病根。
- WinFsp 卡死期间有 2–5 秒 ZipDrive **收不到任何读**(jpg=0 mp4=0);同一视频 `gra_suzu-ma06.mp4` 一个文件
  有 5 次读排队 => per-file 串行化。
- 处理日志前先 `iconv -f UTF-16 -t UTF-8`。

---

## 5. 📖 源码印证(仓库外,用户机器有:`../winfsp`, `../winfsp-native`, `../RamDrive`)

- `../winfsp/src/sys/read.c:356` `FspFileNodeTryAcquireSharedF(... AcquireFull ...)`:WinFsp FSD 的 non-cached
  读只取 **shared** 锁 —— **FSD 自己不串行化同文件读**。=> 串行化在其之上的 Windows I/O Manager(对同步
  handle 的标准行为,NTFS 也一样),不是 WinFsp bug。
- `../winfsp-native/.../FileSystemHost.cs` `OnRead`:异步读(`SynchronousIo=false`)正确返回 `STATUS_PENDING`
  (line ~552),不钉派发线程。ZipDrive 已用此路径。
- `../winfsp/src/dll/fs.c:379` 派发线程数默认 = CPU 核数(clamp 4–16)。
- WinFsp 作者原话(Google Groups):`WinFsp allows concurrent READs to the same file`,而 non-overlapped
  handle 由 I/O Manager 串行化;Dokan 对同文件给并发线程。
- `../RamDrive/CLAUDE.md`:教了 WinFsp 安全测试法 —— **UNC mount**(`host.Prefix` + `host.Mount(null)`),
  不占盘符、崩溃不僵死。`EnableKernelCache=false → FileInfoTimeout=0` 是关 kernel cache 的 backout switch。

---

## 6. ✅ 修复方向(P0,已实验验证有效)

**核心:让任何单个读永不长期持有 FCB 锁。**
`ChunkedStream.EnsureChunkReadyAsync` 等待超过阈值(如 500ms–1s)时,**返回已就绪的部分字节**
(部分读在 Windows 完全合法:`ReadFile` 允许返回比请求少的字节,消费者会自动重发读剩余部分)。
读快速返回 → FCB 锁快速释放 → 照片对同一视频的后续读能推进 → 不卡。

- **与 adapter/格式无关**,WinFsp 和 Dokan 都受益。
- **不用碰 WinFsp 挂载参数**(FSD 已是 shared 锁,I/O Manager 那层改不动;handle overlapped 与否由消费者决定,
  ZipDrive 控制不了)。
- 实验 B 的 PARTIAL-RETURN 组已证明有效(同文件读 2850ms→145ms)。

### ⚠️ 实现时注意
- 返回的必须是**到目前为止真实已解压的字节数**,不能伪造/返回 EOF 骗过尾读(会让视频 moov 解析失败)。
- ChunkedStream 已是逐 chunk 读,天然支持部分读;改点在 `EnsureChunkReadyAsync` 的等待策略:
  文件位置 `F:...\src\ZipDrive.Infrastructure.Caching\ChunkedStream.cs`,方法 `EnsureChunkReadyAsync`
  (当前是无限 `await WaitForChunkAsync`;当前还带着诊断日志,见第 7 节)。
- 需要处理「一个字节都还没就绪」的情况(offset 落在未解压 chunk 且该 chunk 一点没写):此时不能返回 0
  (0=EOF)。可能要短等到至少 1 字节,或对这种读改成「触发/等待该 chunk 优先解压」——但 DEFLATE 只能顺序解压,
  所以尾读注定要等前面全解压。**这正是要和用户确认的设计点**(见第 9 节)。

### 已排除且无效(别重试)
信号量限并发(不是并发问题);调 FileInfoTimeout(cache 本就关);加派发线程数(慢读走 PENDING 不占派发线程);
direct-read / 乱序 chunk(DEFLATE 顺序约束)。

---

## 7. 当前代码改动状态(诊断用,**未提交到 main,不要合进 main**)

分支 `diag/winfsp-photos-hang`。工作树里的诊断改动(忠实复现 bug + 加日志,**不含修复**):
1. `src/ZipDrive.Infrastructure.Caching/CacheTelemetry.cs` — 加静态 `DiagLogger` + `SetDiagnosticLogger()`。
2. `src/ZipDrive.Infrastructure.Caching/ChunkedStream.cs` — `EnsureChunkReadyAsync` 加 `Chunk-wait BLOCK/DONE` 日志。
3. `src/ZipDrive.Cli/Program.cs` — `host.Build()` 后 wiring `SetDiagnosticLogger`。
4. `src/ZipDrive.Infrastructure.Caching/ZipDrive.Infrastructure.Caching.csproj` — 加 `InternalsVisibleTo "ZipDrive"`
   (Cli 程序集名是 **ZipDrive** 不是 ZipDrive.Cli)。
5. 实现 P0 修复前,建议先把这些诊断日志留着(帮助验证修复);修复验证完再一起清理。

Dokan 对照 build 的 worktree:`F:\MyProjects\ZipDrive-dokan-diag`(分支 `diag/dokan-chunkwait`,基于 `0af681e`)。
换机器后这个 worktree 不在了 —— 若要重做 Dokan 对照,`git worktree add ... 0af681e` 重建,移植同样 4 处日志。
(但 root cause 已确证,通常不需要再做。)

---

## 8. 换机器要注意的(重要)

以下是**大文件/仓库外的东西**,不会随 git 过去,换机器后没有;需要的话在新机重建:
- `diag/dumps.winfsp/*.dmp`(973MB,原始 dump,**未入库**)—— 证据结论已提炼进 `diag/out/*.txt`,通常不需原始 dump。
- `diag/dumps/*.dmp`(Dokan 看图 dump)、`publish-dokan/`(85MB)、`publish-coreclr/`、`tools/`(723MB,dotnet-dump 等)、
  `winfsp-decompiled.cs`、`old-dokan-adapter.cs` —— 均**未入库**。
- 仓库外参照源码:`../winfsp`、`../winfsp-native`、`../RamDrive`、`../winfsp-native`(用户机器有;新机器要同样 clone)。
- 诊断工具:`dotnet-dump`(`$USERPROFILE/.dotnet/tools/dotnet-dump.exe`),新机 `dotnet tool install -g dotnet-dump`。

**已入库、换机器能看到的**:本文件、`diag/out/ROOT-CAUSE-CONFIRMED.md`、`diag/out/VERDICT.md`、
`diag/out/dump0*-*.txt`(SOS 报告)、`diag/winfsp-slowread-repro/`(复现代码,可重跑)、
`diag/dumps/chunkwait-*.log`(若在 diag/dumps 且被提交;注意 UTF-16)、其余 diag/*.md。

---

## 9. 下一步(换机器后从这里继续)

1. 读 `diag/out/ROOT-CAUSE-CONFIRMED.md`(最终报告)。可选:`cd diag/winfsp-slowread-repro && dotnet run -c Release -- serialize` 重跑实验确认环境 OK。
2. **和用户确认 P0 修复的设计点**(第 6 节 ⚠️):尾读落在「一点都没解压的 chunk」时怎么办——
   短等到至少 1 字节再返回?还是别的策略?DEFLATE 顺序约束下,尾读注定要等前面解压完,所以「部分返回」
   对**纯尾读**可能仍会卡(因为尾巴那块最后才解压)。这点要想清楚:也许真正解法是
   **视频类大文件首次访问即整文件 materialize + 让读快速返回已解压前缀**,或**限制单个读的最大等待、超时返回已就绪部分**。
3. 实现 P0 到 `ChunkedStream.EnsureChunkReadyAsync`(+ 可能 `ChunkedFileEntry`),遵守 CLAUDE.md 工作流:
   Build → 写测试 → 跑测试 → Pass。
4. 修复后可用 `diag/winfsp-slowread-repro` 的 PARTIAL-RETURN 思路做单元/集成验证,或让用户在真机复现确认不卡。
5. 清理诊断改动(第 7 节的 4 处 + 日志),或按用户要求保留。
