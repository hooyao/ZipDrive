# D4 实测结果 —— partial return 到底能不能救 open?(go/no-go 关键)

> 真机实测(WinFsp 2.1,32 核,已装驱动)。实验:`diag/winfsp-slowread-repro`,子命令 `d4`。
> 结论:**方案有效性依赖并发度 —— 单一慢读下有效,多个重叠慢读下退化到接近冻结。这是一个真实边界,不是干净的 GO。**

---

## 0. 实验设计(v2,已排除 v1 的测量瑕疵)

- 一个 64MB `video.bin`,offset ≥ 32MB 的读是"慢尾读"。
- **慢读侧**两种模型:
  - `block`:一个慢读持 WinFsp FileNode Main shared 锁,阻塞整个(封顶)解压时长 —— 模拟**当前** `EnsureChunkReadyAsync` 无限等。
  - `partial`:慢读等一个 budget(默认 800ms)就返回 partial,loop 反复重发 —— 模拟**修复后**"shared 锁高频获取/释放/再获取"。
- **探测**:单线程,测**纯 OPEN**(open 完立刻 close,不读)的延迟 —— 精确隔离 open 完成侧需要的 Main **exclusive** 获取等待(winfsp create.c:1326 `TryAcquireExclusive` + 失败重新入队,iop.c/ioq.c)。
- `--slowThreads=N`:并发几个慢读(交错持 shared)。

## 1. 数据

### slowThreads=1(任意时刻仅 1 个慢读持 shared)
```
BASELINE(无慢读)      OPEN-alone p50=1.4ms   p99=10ms    n=95
CONTROL (一次长阻塞)   OPEN-alone p50=3813ms  p99=3813ms  n=1
D4      (partial+重发) OPEN-alone p50=747ms   p99=747ms   n=4   => GO (p99<1500)
```

### slowThreads=4(4 个慢读交错持 shared —— 更接近 Photos 并发多操作)
```
BASELINE               OPEN-alone p50=1.5ms   p99=10ms    n=95
CONTROL                OPEN-alone p50=7822ms  p99=7822ms  n=1
D4      (partial+重发) OPEN-alone p50=2362ms  p99=2362ms  n=2   => NO-GO (p99>1500)
```

## 2. 揭示的机制(关键)

- **open 的 exclusive 不是"在两次 shared 之间的瞬间空隙"挤进去,而是要等当前持 shared 的 partial 慢读跑完它那一轮 budget、释放锁的那一刻。**
  => **open 被阻塞时长 ≈ 需要等待的 shared 持有窗口。**

- **slowThreads=1**:同一时刻只 1 个 shared,budget 到就全释放 → open 等 ≤1 个 budget(747ms ≈ 800ms budget)。**可接受(冻死→轻微卡顿)。**

- **slowThreads=4**:4 个慢读的 budget 窗口**重叠交错**,ERESOURCE 的 exclusive 要等**所有** shared 同时释放的空隙 → 该空隙难出现 → open 等 ~3 个 budget(2362ms)。**退化到接近冻结。**

## 3. 建模偏差(诚实标注,两个方向都有)

**可能让结果偏悲观**:实验的慢读 loop 背靠背无间隔重发(读完立刻再读);真实消费者拿 partial 后要走 kernel→app→再发 IRP,有间隙,shared 获取没这么密。

**可能让结果偏乐观(反向)**:真实 Photos 确实会**并发**多个操作(缩略图/属性/预览/播放器各开 handle),所以"多个重叠慢读"不是虚构 —— slowThreads>1 有真实性。

=> 真实并发度落在 1~4 之间某处,继续调参数只是猜。**核心权衡已经清楚,不必再调。**

## 4. 对方案的真实含义 & budget 权衡

修复**不是"完全不卡",而是"把几十秒冻死压成 N×budget 的卡顿"**,N = 同文件重叠慢读的并发数:

| budget | 单慢读 open 卡顿 | 副作用 |
|---|---|---|
| 小(如 300ms) | ~300ms(流畅) | 每轮返回数据少 → 消费者重发多 → 读放大、视频加载更慢 |
| 大(如 800ms) | ~750ms(轻卡) | 读效率高,但 open 卡更久;多并发下 ×N 更明显 |

**关键结论**:
1. **单一慢读场景:方案有效**(冻死 → <1s 卡顿)。
2. **多重叠慢读场景:方案只能缓解、不能消除**(2.4s 卡顿,取决于并发数 × budget)。
3. budget 是"open 卡顿时长"和"读放大/加载速度"之间的直接权衡,需实测真实工作负载定。

## 5. 决策输入(给用户)

D4 的答案是**限定性的 GO**:
- ✅ 相比现状(几十秒冻死)是**决定性改善**,任何并发度下都从"分钟级"降到"秒级"。
- ⚠️ 但**做不到"完全不卡"**;多并发同文件慢读时仍有 ~2s 级卡顿。
- ❓ 是否可接受,取决于:(a)真实 Photos 对同一视频的并发慢读有几个;(b)用户对"几十秒冻死→2秒卡顿"是否满意,还是要求"完全流畅"。

若要求"完全流畅",partial return 不够 —— 但**唯一能进一步改善的方向(整文件预解压)已被用户否决(语义错)**。那种情况下的选项只剩:**接受 2 秒级卡顿**,或**放弃 WinFsp 退回 Dokan**(Dokan 无此锁,实测同文件 open ~1ms)。

## 6. 复现
```
cd diag/winfsp-slowread-repro
dotnet build -c Release
dotnet bin/Release/net10.0/SlowReadRepro.dll d4                    # slowThreads=1
dotnet bin/Release/net10.0/SlowReadRepro.dll d4 --slowThreads=4    # 重叠慢读
dotnet bin/Release/net10.0/SlowReadRepro.dll d4 --budgetMs=300     # 小 budget
```
