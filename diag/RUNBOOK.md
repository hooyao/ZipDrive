# 操作手册(中文) — WinFsp 卡死复现与抓 dump

> 本文是给**操作者**看的一步步流程。技术分析见 [`ANALYSIS.md`](ANALYSIS.md),
> dump 怎么读见 [`ANALYZE.md`](ANALYZE.md),修复方案见 [`PROPOSED-FIX.md`](PROPOSED-FIX.md)。
> 这个分支上的 build **故意不含修复**,因为诊断 build 必须能复现 bug 才抓得到卡死现场。

## 现象(一句话)

Dokan→WinFsp 迁移后,挂载 NAS/SMB 上的 ZIP,在资源管理器里转缩略图时双击一张**已有缩略图**的图片:
Windows 11「照片」能打开并显示这张图,**但随后整个窗口卡死无响应**,过一会儿才缓过来。
SMB 上很严重,NVMe 上只卡 2–3 秒。**旧 Dokan 版本丝毫不卡。**

## 根因(一句话)

迁移把文件系统回调的工作从 Dokan 那个**大且按需增长的原生线程池**,挪到了两个**小而固定、从不扩容**的池上
——WinFsp 派发线程池(`[4,16]`,所有元数据回调用 `GetResult()` 同步阻塞它)和 .NET 线程池(全程没 `SetMinThreads`,
跑所有 STATUS_PENDING 读完成 + `Task.Run` 视频后台解压)。SMB 高延迟下两个池都被占满 → 卡。
照片是在**显示完图片后去预读相邻文件**(包括那个还在解压的视频)时,撞上被榨干的文件系统才卡的。

---

## 准备(目标机器一次性)

- **.NET 10 SDK**(任意 `10.0.x`)
- **WinFsp** 已安装 —— <https://winfsp.dev/rel/>
- 拉到本分支

```powershell
git fetch
git checkout diag/winfsp-photos-hang
.\diag\install-tools.ps1     # 装 dotnet-dump/counters/trace/gcdump
```

## 第 1 步:建可追踪 build(非 AOT、自包含、带符号)

```powershell
.\diag\build-diag.ps1
```
产出 `publish-jit\ZipDrive.exe`。打印出 `coreclr.dll present = JIT, NOT AOT` 就对了。

## 第 2 步:启动 ZipDrive(开 Information 日志)

把 NAS 路径填进去,**单独开一个终端**运行,保持可见:

```powershell
.\publish-jit\ZipDrive.exe `
  --Mount:ArchiveDirectory="\\NAS\share\...\mixed.zip" `
  --Mount:MountPoint="R:\" `
  --Serilog:MinimumLevel:Default=Information
```
日志里的 `Read (miss): <压缩包>:<文件>` 会显示正在冷读哪个文件 —— 卡死前最后一条,就是照片当时卡在哪个文件。

## 第 3 步(推荐):另开终端采集 counters

```powershell
.\diag\collect-counters.ps1
```
全程开着,复现完 Ctrl+C 停。饥饿的标志:`threadpool-queue-length` 飙升、`threadpool-thread-count` 几乎不动、`cpu-usage` 低。

## 第 4 步:复现

1. 资源管理器打开 `R:\…\mixed.zip\`,切**大图标/超大图标**,让 Windows 开始转缩略图(图片**和**视频)。
2. 等几张缩略图转出来(此时后台已经在慢慢从 SMB 解压视频)。
3. 双击一张**已经显示缩略图**的图片 → 照片打开并显示 → 随后**卡死无响应**。

## 第 5 步:卡死的当下,间隔抓 dump

照片一无响应,立刻在第三个终端跑:

```powershell
.\diag\collect-dump.ps1                       # 默认 5 张 Heap dump,每 3 秒 1 张(≈12 秒)
# 卡得久就抓更久:
.\diag\collect-dump.ps1 -Count 10 -IntervalSec 2
```
**跨越"卡死→缓过来"最理想**:多张栈一样=硬阻塞,栈在变=慢排空。dump 落在 `diag\dumps\`。

把 ZipDrive 终端里**卡死前最后一条 `Read (miss): …`** 抄进 `diag\dumps\NOTES.txt`。

## 第 6 步:收尾

- 等照片缓过来,Ctrl+C 停掉 counters(第 3 步)。
- ZipDrive 终端按 **Ctrl+C** 干净卸载 `R:\`。
- (可选)第二次复现时跑 `.\diag\collect-trace.ps1 -DurationSec 30`,在 30 秒内触发卡死,得到时间线 trace(用 PerfView/VS 打开)。

## 第 7 步:让那台机器上的 Claude Code 分析

dump 必须在**抓取的同一台机器**上分析(运行时要匹配):

```powershell
.\diag\analyze-dump.ps1      # 对 diag\dumps 里每个 dump 生成 diag\out\*.analysis.txt
```

然后对 Claude Code 说这一句:

> 读 `diag/ANALYSIS.md` 和 `diag/ANALYZE.md`,然后分析 `diag/dumps/` 里的 dump(可先跑 `diag/analyze-dump.ps1`),
> 按 ANALYZE.md 的"特征→根因"对照表判定是 **.NET 线程池饥饿(A)** 还是 **WinFsp 派发线程饥饿(B)**,
> 贴出关键阻塞栈和 `threadpool` 那一行,并区分是饥饿还是 CPU 打满。

## 交给分析方的产物清单

- `diag\dumps\*.dmp`(dump)
- `diag\out\*.analysis.txt`(预生成的 SOS 报告)
- `diag\out\counters-*.csv`(线程池/运行时时间线)
- `diag\dumps\NOTES.txt`(卡死前最后一条 `Read (miss)` + 大致时间)
- ZipDrive 控制台日志(复制粘贴或重定向到文件)

---

## 确认是不是这个病的最快方法(可选,验证修复)

修复在 [`PROPOSED-FIX.md`](PROPOSED-FIX.md) 里**只写没动**。想立刻验证,有个**零重编译** A/B 法:
编辑 `publish-jit\ZipDrive.runtimeconfig.json`,在 `configProperties` 里加一行
`"System.Threading.ThreadPool.MinThreads": 256`,重启再复现 —— 如果不卡了/明显好转,
就坐实了 .NET 线程池饥饿。需要把完整修复也开个分支直接 A/B,跟我说一声。
