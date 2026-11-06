# 页面生命周期与配置列表内存优化

> 最终验证完成：以下数据来自包含键盘修复的最终 NativeAOT 构建（哈希 `729dd08…`），不是此前的性能原型。12 次独立进程对照、对象回收与交互检查均通过。

## 结论

改动集中在页面释放和配置列表，修复了两项可复现的问题：

1. 配置、日志、设置页面 `Dispose()` 漏退订基类的全局语言事件，导致已经卸载的 ViewModel 仍然存活。
2. 配置页使用普通 `ItemsControl` 面板，一次创建全部配置卡片。改用框架现有的 `VirtualizingStackPanel`，保留原来的卡片模板、按钮、数据集合与滚动布局，仅创建可见范围附近的控件。

**100 条配置的正式 NativeAOT 对照，首次打开配置页：总工作集减少约 24.6 MiB，专用工作集减少约 24.5 MiB（37%）。** 8 条配置、首页和设置页没有可证实的大幅内存下降，不宣称所有场景都更省内存。

没有新增或修改强制 GC、工作集清理、渲染后端、刷新频率、数据上限或发布参数。

## 1. 正式 NativeAOT 对照

环境：Windows 11 build 26300、i5-14600KF（20 个逻辑核）、RTX 5060、.NET SDK 10.0.400。Avalonia 12.2.999-cibuild0101023-alpha；默认渲染后端不变。

- 原版：提交 `92a049d0a0b768810abbf6c0d660b70ac4074224`。
- 优化版：上述提交加本轮产品修改。
- 两版都使用相同 Release / win-x64 / NativeAOT 发布参数，见后面的复现命令。
- 独立便携数据目录、纯色、亮色、英文、1000×720 像素窗口；关闭自动启动内核和应用更新，不启动代理内核。
- 分别准备 8 / 100 条合成的本地配置，列表加载元数据，不用用户真实配置。
- 每组每版 3 个独立进程，交替顺序；最终每进程 1 轮导航与最小化/恢复。共 12 次完整启动、60 个有效采样。之前原型的两轮数据单独保留，不混入最终统计。
- 表格取每次进程**首次打开配置页**的 3 个样本中位数；该阶段尚未执行测试中的最小化/恢复。不会挑最低的卸页/托盘工作集当收益。

| 首次打开配置页 | 原版 | 优化版 | 解读 |
|---|---:|---:|---|
| 8 条：总工作集 | 77.094 MiB | 76.762 MiB | 基本持平 |
| 8 条：专用工作集 | 40.840 MiB | 40.520 MiB | 基本持平 |
| 8 条：私有提交 | 183.250 MiB | 178.672 MiB | 原版有 232.578 MiB 的单次高值，不据此承诺普遍下降 |
| 100 条：总工作集 | 102.824 MiB | 78.215 MiB | 减少 24.609 MiB |
| 100 条：专用工作集 | 66.457 MiB | 41.961 MiB | 减少 24.496 MiB，约 37% |
| 100 条：私有提交 | 204.852 MiB | 174.516 MiB | 中位数减少 30.336 MiB，但打开配置页前已有基线波动，不能全部归因于该页 |
| 100 条：切到配置页的进程 CPU 用时 | 187.500 ms | 31.250 ms | 约减少 83%，不是界面打开耗时或帧率 |

100 条配置的三轮专用工作集：

- 原版：65.281 / 66.457 / 67.102 MiB。
- 优化版：42.098 / 41.961 / 41.914 MiB。

同组首页专用工作集中位数 19.410 → 19.430 MiB，设置页 39.859 → 39.203 MiB，属于基本持平。首轮回到首页后专用工作集 74.035 → 50.852 MiB，说明少建控件也减少了页面往返后的内存压力。

### 指标口径

- **总工作集**：当前驻留在物理内存的进程页面，包含可共享部分。
- **专用工作集**：其中私有驻留页面，接近任务管理器默认的进程内存口径。
- **私有提交 / Private Bytes**：进程私有已提交虚拟内存，包含托管与原生部分；不是单纯的地址空间预留，也不能把共享 DLL 映射直接当成它的组成。
- `NavigationCpuMs`：选择页面和固定 400ms 稳定等待区间内消耗的进程 CPU 时间，区间还含 UI Automation 开销。`NavigationElapsedMs` 不能作为首次绘制时间，`CpuOneCorePercent` 是其后空闲一秒的采样。
- GPU 显存、DWM 开销、实际帧时间未测量。

## 2. 真正减少了什么分配

使用真实 `ProfilesView`、真实 ViewModel 和应用样式，在同一份 headless 测试脚本中测量控件构建/布局。先预热框架；视口 780×590。这里测的是 **UI 线程累计托管分配**，不是常驻内存，更不是把这些数与上述 AOT 工作集相减。

| 配置数 | 卡片数：原版 → 优化版 | 视觉节点数：原版 → 优化版 | 构造分配：原版 → 优化版 |
|---|---:|---:|---:|
| 0 | 0 → 0 | 57 → 57 | 1.612 → 1.613 MiB |
| 8 | 8 → 8 | 298 → 298 | 5.475 → 5.486 MiB |
| 100 | 100 → 8 | 2,782 → 298 | 49.307 → 5.485 MiB |
| 500 | 500 → 8 | 13,582 → 298 | 239.109 → 5.483 MiB |

数据集合并未裁剪。最后一条可以滚动到达，编辑、更新、删除按钮仍绑定到正确的行对象。缩放、往返滚动、行按钮状态变化、删除/清空/重载、语言与主题切换均已做真实控件断言。100 / 500 条列表还检查了连续 Tab 与 Shift+Tab 跨多个视口的双向导航。

## 3. 页面引用保留

每种页面创建、激活、离开并重复调用 `Dispose()` 20 次，然后在独立诊断进程中回收并检查弱引用：

| 页面 | 原版仍存活 | 优化后仍存活 |
|---|---:|---:|
| 配置 | 20 / 20 | 0 / 20 |
| 日志 | 20 / 20 | 0 / 20 |
| 设置 | 20 / 20 | 0 / 20 |
| 连接（对照） | 0 / 20 | 0 / 20 |

原版保留链为 `LocalizationService.Instance.LanguageChanged → PageViewModelBase.OnPageLanguageChanged → 页面 ViewModel`。补调用已有的 `ReleasePageMetadata()` 即可断开，不引入新的生命周期抽象。

空页面对象本身只是 KB 级，本项主要消除反复卸载后的累积保留，不能用它解释此前全部高内存。实际携带的数据量取决于页面使用情况。

**诊断中的 GC 仅用于判定对象是否仍被引用，不用于生成工作集收益。** 产品 AOT 对照脚本没有加入 GC 或 trim 调用。立即关闭窗口时，视图可能仍受挂起回调短暂持有；泵送 UI 600ms 后，优化版所有受测视图和 ViewModel 均能回收。断言没有删除。

## 4. 验证、失败记录与边界

- 完整重编译的既有 GUI 测试：**377 / 377 通过**。
- 两版 NativeAOT 发布成功；哈希如下。
- 最终生命周期与列表交互断言通过。补充键盘检查曾发现：标准虚拟面板下 Tab 只能看到已创建的行，原版则可以继续到下一行。已在 `ProfilesView.axaml.cs` 补充跨行时按需创建目标行的局部处理，保留行内导航并过滤禁用/隐藏按钮；连续 Tab/Shift+Tab 双向跨视口及视图回收均已通过。没有扩大缓存或恢复全量卡片来掩盖问题。
- 首次对照批次在第 5 次启动原版时中断，未见对应 `crash.log` 或 Windows 应用崩溃事件。脚本使用 `Stop-Process` 后未等待进程真正退出，存在与全局单实例锁竞争的窗口。补上 `WaitForExit`、逐次启动检查和退出码信息后，完整 12 次重跑通过。应用的单实例逻辑未改；中断批次单独保留，不混入最终统计。
- 原应用已有启动/卸页/最小化时的 GC 与工作集修剪，本轮保持不变。探索阶段曾出现可见窗口工作集仅 2.539 MiB 的修剪低值，**没有当作优化效果**。恢复和空闲采样保留在原始数据中，但不作为主要节省依据。
- 未做长时间真实代理流量压力测试、低端 GPU/其他机器验证、GPU/DWM 分析或配置编辑器大型正文压力测试。本轮不声称所有内存问题都已解决。

## 5. 复现与证据

```powershell
dotnet test src/carton.GUI.Tests/carton.GUI.Tests.csproj -c Release

dotnet run --project scripts/memory/PageLifetimeProbe -c Release -- --verify
dotnet run --project scripts/memory/PageLifetimeProbe -c Release -- --profiles --verify

# 分别在修改前后发布到 before / after；两次参数完全一致。
dotnet publish src/carton.GUI/carton.GUI.csproj -c Release -r win-x64 -o artifacts/memory-lifecycle/after /p:PublishAot=true /p:SelfContained=true /p:StripSymbols=true /p:DebugSymbols=false /p:DebugType=None /p:InvariantGlobalization=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true

powershell -NoProfile -ExecutionPolicy Bypass -File scripts/memory/mem-page-lifecycle.ps1 -Runs 3 -Cycles 1 -ProfileCount 8 -ResultsDirectory artifacts/memory-lifecycle/final-8
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/memory/mem-page-lifecycle.ps1 -Runs 3 -Cycles 1 -ProfileCount 100 -ResultsDirectory artifacts/memory-lifecycle/final-100
```

可执行文件 SHA-256：

- before：`b5f61ed12e0acd87ac5d41cfbb2c23c85cf43b8c7b5e30bcdc7b6d02beac66d0`
- after：`729dd08eddac9b0b92c0199ff1cbdfe5c5d69e072d7c3b2a72cc83ce225b9970`

本地原始证据在 `artifacts/memory-lifecycle/`（不入版本库）：

- `final-8/`、`final-100/`：最终构建的逐次采样；`final-summary.json`：分场景中位数；`final-build-hashes.json`：所有采样已逐条校验的可执行文件哈希。
- `lifetime-before.jsonl`、`lifetime-after.json`：事件根与弱引用。
- `profile-construction-before.json`、`profile-construction-after-final.json`：控件数量与构造分配；`final-view-verification.log`：包含键盘与完整 GUI 回归的最终专项结果。
- `environment.json`、`optimization.patch`：环境和最终产品差异。
- `comparison-8/`、`comparison-100/`、`summary.json`、`after-prototype/`、`prototype-optimization.patch`：加入键盘修复前的性能原型证据，不混入最终数据。
- `comparison-8-interrupted/`、`interrupted-run.log`：未纳入统计的中断批次。

回归与测量工具说明：[`scripts/memory/PageLifetimeProbe/README.md`](../scripts/memory/PageLifetimeProbe/README.md)。
