# Dokan 对照实验 — 复现操作单(diag/dokan-chunkwait）

> 目标:用**带 chunk-wait 日志的 Dokan 版**跑和 WinFsp **完全相同**的复现,拿到 Dokan 的
> `Chunk-wait BLOCK/DONE` 时间线,和 `diag/dumps/chunkwait-winfsp.log` 直接对比,
> 回答:**同样「读尾巴 + 顺序解压必须等全解压」,为什么 Dokan 不卡、WinFsp 卡?**

## 这个 build 是什么

- 路径:`F:\MyProjects\publish-dokan\ZipDrive.exe`(JIT + PDB,可 dump 分析)
- 代码基线:`0af681e`(WinFsp 迁移**前**最后一个 Dokan commit),worktree 在
  `F:\MyProjects\ZipDrive-dokan-diag`,分支 `diag/dokan-chunkwait`。
- 和 WinFsp 诊断版**唯一的区别**:presentation adapter 是 **Dokan**(`DokanNet.dll`),
  其余缓存/解压/ZIP 代码**逐字相同**。
- 已内置和 WinFsp 那次**完全一致**的复现配置(`appsettings.dev.jsonc`):
  `cutoff=5MB / mem=50MB / disk=500MB / TTL=1min / 维护=10s / Information 日志 /
  ArchiveDirectory=Y:\WD_8T_01\ForUpload\Graphis / MountPoint=R:\`。
- 已加 chunk-wait 诊断日志(和 WinFsp 版同款):
  `Chunk-wait BLOCK: offset=… needsChunk=N/总数 extractedChunks=… progress=…`
  `Chunk-wait DONE: offset=… chunk=N waited=…ms`

## 前置:确认 Dokany 驱动已装

```powershell
Get-Service -Name DokanY* -ErrorAction SilentlyContinue
# 或者看驱动文件
Test-Path C:\Windows\System32\drivers\dokan2.sys
```
没有就先装 Dokany v2.3.1.1000(项目要求的版本)。WinFsp 和 Dokany 可以共存。

## 第 1 步:启动 Dokan 版,日志重定向到文件

**单开一个终端**,在 `F:\MyProjects` 下跑(配置已内置,无需带参数):

```powershell
cd F:\MyProjects
.\publish-dokan\ZipDrive.exe *>&1 | Tee-Object -FilePath F:\MyProjects\ZipDrive\diag\dumps\chunkwait-dokan.log
```

- `*>&1 | Tee-Object` = 屏幕能看 + 同时落盘(和 WinFsp 那次抓 `chunkwait-winfsp.log` 等价)。
- 如果不想看屏幕,直接重定向也行:
  `.\publish-dokan\ZipDrive.exe *> F:\MyProjects\ZipDrive\diag\dumps\chunkwait-dokan.log`
- 启动后日志应出现 `ZipDrive 1.0.0-dev starting`、`Mounting VFS …`、`Discovered 38 archive files`、
  `Mount point: R:\`。挂载成功后 `R:\` 出现。

## 第 2 步:复现(和 WinFsp 那次一模一样的操作)

1. 资源管理器进 `R:\日本Graphis系列写真\GRAPHIS.Gals-14.zip\`(就是含 `gra_non-n*.mp4`
   400–490MB 视频 + 图片的那个),切**超大图标**,让 Windows 开始转缩略图。
2. 等几张图片缩略图转出来(后台此时已经在从 SMB 慢慢解压视频)。
3. 双击一张**已经显示缩略图**的图片 → 「照片」打开显示。
4. **关键观察**:Dokan 版这一步**应该不卡**(用户已确认「dokan 一点都不卡」)。
   照常翻几张图、来回点,把 WinFsp 卡死时的相同动作都做一遍。

## 第 3 步(可选):卡/不卡的当下抓 dump

即便不卡,也建议在「视频还在后台解压」的窗口期抓 3–5 张 dump,作为 Dokan 侧栈证据:

```powershell
cd F:\MyProjects\ZipDrive
.\diag\collect-dump.ps1 -Count 5 -IntervalSec 3
```
> 注意:`collect-dump.ps1` 默认找进程名 `ZipDrive`,Dokan 版进程名一样,可直接用。
> dump 落在 `diag\dumps\`。**为避免和 WinFsp dump 混淆,抓完请把它们挪到
> `diag\dumps.dokan\` 或改名带 `dokan` 前缀。**

## 第 4 步:停止 + 交回

- 资源管理器和「照片」关掉,回启动终端 `Ctrl+C` 停 ZipDrive(会自动卸载 `R:\`）。
- 把 `diag\dumps\chunkwait-dokan.log` 留好,告诉我「Dokan 日志好了」。

## 我拿到 chunkwait-dokan.log 后会做什么

直接对比两条 BLOCK 时间线,落在两种结果之一:

| 结果 | 含义 | 修复方向 |
|---|---|---|
| **Dokan 也大量 BLOCK 读尾巴、但 UI 不卡** | 病灶在「读未解压 chunk 会死等」这件事本身;WinFsp 把这个等待**挡在了 Photos 的 UI 关键路径**上,Dokan 没有(adapter 的读时序/并发模型不同) | 让大文件「读未解压位置」不再死等整文件顺序解压(按需解压 / 超时回退 / 大文件读不阻塞 UI 路径) |
| **Dokan 几乎不 BLOCK**(或 BLOCK 很少/很短) | 两个 adapter **发起读的 offset/时序不同**:WinFsp 让 Photos 预读了视频文件体高位,Dokan 没有(或被内核缓存吸收) | 病灶在 adapter 怎么把 Photos 的预读 IRP 打进 VFS;对齐 WinFsp 的读模式到 Dokan 的行为 |

> 两种都不靠「信号量限并发」(已证无效)。具体修复待对照结果定。

## 善后(实验做完后)

- worktree 删除:`git worktree remove F:\MyProjects\ZipDrive-dokan-diag`
  (分支 `diag/dokan-chunkwait` 是临时诊断分支,可一并删)。
- `publish-dokan\` 是临时产物,可直接删目录。
- 这些诊断改动**都不提交**(用户已明确「临时用一下就好,不用提交」)。
