# winfsp-c-repro — 纯 C WinFsp 实验(排除 winfsp-native binding 层)

> 目的:验证"同文件慢读串行化"到底在**WinFsp 内核 FSD / I-O Manager 层**,还是在 **winfsp-native (.NET binding)** 层。
> 做法:用纯 C + 官方 WinFsp SDK 写一个最小 FS,零 .NET binding。若纯 C 也复现串行化 → 锁在内核,binding 无辜;若不复现 → 问题在 binding,可在用户态修。
>
> 顺带测:blocking vs STATUS_PENDING 读、FileInfoTimeout=0(无 kernel cache,ZipDrive 现状) vs >0(有 cache,走 FspFsvolReadCached 那条**函数内就释放锁**的路径)。

## ✅ 已完成(可复现)
- **编译通过**:`build.cmd`(VS18 cl.exe + 官方 WinFsp SDK `inc`/`lib`,DELAYLOAD winfsp-x64.dll + FspLoad 从注册表加载 SxS 目录的 DLL)。
  - ⚠️ build.cmd 必须是 **CRLF** 行尾(Write 工具写的是 LF,cmd.exe 会把批处理拆碎报 `'ild' not recognized`)。改完用 `[System.IO.File]::WriteAllText` 转 CRLF。
- **UNC 挂载成功(不占盘符)**:`winfsp-c-repro.exe --prefix=\host\share` → `MOUNTED at: \\host\share`。
- **实验矩阵已全部跑完**,结论已写入 `../out/C-REPRO-RESULTS.md`。

## ✅ 已解决(原"待解决"两项)
1. **UNC 挂载(替代盘符 Z:)**:根因是 `FspFileSystemSetMountPoint(fs, 0)` 内部等价于 `L"*:"` → 自动分配盘符(fs.c:198 / mount.c:591)。
   修法:net 设备 + `VolumeParams.Prefix` 模式下**根本不调用 SetMountPoint**,卷经网络提供者在 `\\<prefix>` 暴露(同官方 memfs-net)。
   另加 `--prefix=` 让每次实验用唯一名,规避重定向器 negative-cache;`AllowOpenInKernelMode=1` 让 MUP 内核态探测 share 成功。
2. **"并发探测挂载掉"的真正根因**:不是 ReadDirectory,而是**接口缺 Create/Overwrite 回调**。
   `FspFileSystemOpCreate`(fsop.c:907)要求 Create+Open+Overwrite 三者都非空,否则任何 create IRP(含对已存在文件的 FILE_OPEN)
   在派发到 Open 前就回 `c0000010`(STATUS_INVALID_DEVICE_REQUEST)——所以没有一个文件能打开,看起来像"挂载掉了"。
   修法:加只读 Create/Overwrite 桩(返回 ACCESS_DENIED)。改完 4×3 次实验零掉挂载。

## 结论(详见 ../out/C-REPRO-RESULTS.md)
**纯 C 也 100% 复现"同文件慢读串行化阻塞 open",被挡的精确是 OPEN(~2.8s),read 一旦 open 返回仅 0.1ms;
异文件 open 不受影响(~1.2ms,per-file 粒度)。STATUS_PENDING 不解锁,kernel cache(冷 open 场景)也不解锁。
→ 锁在 WinFsp 内核 FSD 层,winfsp-native binding 无辜,用户态无法绕过。**

## 工具
- `run-experiment.ps1 -Label <名> -ReproArgs @('--tailDelayMs=3000') -Runs 4`:唯一 prefix 挂载 → 探测 N 次(带超时看门狗)→ finally 保证清理。
- `--debug` 开关:每回调带时间戳/线程 ID 的 stderr 日志 + FSD 级 request/response 追踪。
- `--dir=<空目录>`:安全兜底,目录挂载点(不占盘符),本次未用。

---

## (历史)交接时的原始待解决记录


## ⚠️ 待解决(交接给下一步)
1. **挂到了盘符 Z:,不是 UNC**:`FspFileSystemSetMountPoint(fs, 0)` 传 0 让 WinFsp 自动选盘符。**用户明确要求不挂盘符**。需改成 UNC mount:
   - memfs 的 UNC 做法:`DevicePath = FSP_FSCTL_NET_DEVICE_NAME` + `VolumeParams.Prefix = L"\\winfsp-crepro\share"`(已设),但 SetMountPoint 要传 `L"*"` 或具体 UNC,不是 0。查 memfs-main.c 的 `-u` UNC 分支确认正确调用。
2. **探测时 Z: 挂载掉了**(进程还活着但卷消失):并发读触发了 C 代码的 bug —— 最可能是:
   - `ReadDirectory` 的 `AddDirInfoEntry` 缓冲区/marker 处理(每次都从头加两个 entry,没处理 Marker,可能越界或死循环);
   - 或 pending 线程路径(本次是 blocking 模式,pending 没走,但值得复查);
   - 或 `FspFileSystemGetOperationContext()->Request->Hint` 在某些回调里为空。
   建议:先加 stderr 诊断日志到每个回调,重挂,单步跑探测看哪个回调崩。

## 实验矩阵(挂载稳定后要跑的)
| 模式 | 命令 | 预期(若锁在内核) |
|---|---|---|
| blocking, 无cache | `--tailDelayMs=3000` | 同文件 open+head 读被挡 ~2850ms(复现) |
| pending, 无cache | `--pending --tailDelayMs=3000` | 同上(STATUS_PENDING 不提前释放内核锁,agent 已源码证实) |
| blocking, 有cache | `--tailDelayMs=3000 --timeout=1000` | **关键**:若走 FspFsvolReadCached → 可能不挡(锁函数内释放) |

判据:纯 C **也**复现 open 被挡 → 锁在 WinFsp 内核 → binding 无辜,partial 死结成立;
纯 C **不**复现 → 问题在 winfsp-native,可在用户态修(重大转折)。

## 文件
- `repro.c` — 最小 C FS(FSP_FILE_SYSTEM_INTERFACE 用 C99 designated init)。
- `build.cmd` — 编译(CRLF!)。
- `probe.cs`/`probe.csproj` — .NET 探测器(慢尾读 + 同文件并发 open+head 读计时)。

## 安全
- 挂载**必须** UNC 或临时盘符即用即卸,不占常驻盘符。
- 每次实验后:杀 `winfsp-c-repro` 进程,`fsptool-x64.exe lsvol` 确认无残留卷。
- idle loop 是 `for(;;) Sleep(1000)`,靠杀进程卸载;进程被杀后 WinFsp 自动清理卷。
