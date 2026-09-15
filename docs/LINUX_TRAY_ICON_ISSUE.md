# Linux 托盘图标消失（StatusNotifierItem 注册丢失）：根因、修复与验证

> 依据：用户实机复现数据（Caelestia/Quickshell + Hyprland，carton 0.6.0）+ StatusNotifierItem 规范
> （<https://specifications.freedesktop.org/status-notifier-item/latest/status-notifier-watcher.html>）
> + Avalonia `release/11.3` 的 `src/Avalonia.FreeDesktop/DBusTrayIconImpl.cs` 源码。
>
> **结论先行**：
> 1. 现象是 **托盘注册丢失**，不是 sing-box 或网络故障——carton 进程、内核、代理全程正常。
> 2. 根因：**"注册"是 watcher（托盘宿主）侧的状态**，宿主重启后登记簿为空且不会回扫总线；
>    而 Avalonia 11.3 的重注册路径在这次场景里没有触发/没生效。
> 3. 已修复（应用侧兜底）：`fix(linux): 托盘宿主重启后自动重新注册托盘图标`（`fc7dad5`），
>    Linux-only，每 5 分钟把 item 重新登记一次，不依赖任何通知，**两类丢失场景都覆盖**。
> 4. 上游 `AvaloniaUI/Avalonia#21980` **未合并**（目标 Avalonia 12.1+），**升级 Avalonia 今天解决不了**；
>    且它只覆盖"watcher 名号变化"这一类，不覆盖"宿主重启而 watcher 名号不变"。

---

## 1. 现象与证据

用户报告（实机）：

| 观测 | 值 |
| --- | --- |
| carton GUI | 仍在运行（00:16:38 启动） |
| sing-box 内核 | 仍在运行，联网正常 |
| carton 的 item 名 | `org.kde.StatusNotifierItem-1228-0` **仍在总线上** |
| 宿主 `RegisteredStatusNotifierItems` | **已经没有 carton** |
| 托盘宿主 Quickshell/Caelestia | 02:46:52 重启过 |

一句话：**图标消失 ≠ 程序挂了**。程序、内核、代理全在，只是"登记"被抹掉了。
手工重新登记可以当场恢复（用户实测）：

```bash
busctl --user call \
  org.kde.StatusNotifierWatcher \
  /StatusNotifierWatcher \
  org.kde.StatusNotifierWatcher \
  RegisterStatusNotifierItem \
  s org.kde.StatusNotifierItem-1228-0
```

这条命令能立刻恢复，是很关键的旁证：**要修的就是"重新登记"这个动作**（本方案的 L0 做的就是把它自动化）。

---

## 2. 背景：StatusNotifierItem 的三方模型

| 角色 | 在本例中是谁 | 职责 |
| --- | --- | --- |
| **StatusNotifierWatcher** | Quickshell/Caelestia | 持有登记簿 `RegisteredStatusNotifierItems`；item 必须登记进来才会被看见 |
| **StatusNotifierHost**（面板） | 同上（同一进程） | 读登记簿，把条目画成图标显示 |
| **StatusNotifierItem**（应用） | carton | 导出 `/StatusNotifierItem` 对象 + **主动登记**自己 |

规范原文（`status-notifier-watcher.html`）：

> `RegisterStatusNotifierItem(STRING service)` … **A StatusNotifierItem instance must be registered
> to the watcher in order to be noticed from both the watcher and the StatusNotifierHost instances.**
> If the registered StatusNotifierItem goes away from the session bus, the StatusNotifierWatcher
> should automatically notice it and remove it from the list of registered services.

关键点：**登记簿是 watcher 的内存状态**。宿主/watcher 重启 → 带回来一本**空登记簿**，
它**不会**去总线上挨个问"谁还有图标要显示"。所以"名字还在总线上"对图标是否显示**没有任何帮助**——
这正是本例的现象。

---

## 3. 根因

### 3.1 Avalonia 11.3 的重注册路径

`DBusTrayIconImpl`（Avalonia 11.3.x）确实监听了 watcher：

```csharp
private async void WatchAsync()
{
    _serviceWatchDisposable = await _dBus!.WatchNameOwnerChangedAsync((_, x) => OnNameChange(x.Item1, x.Item3));
    var nameOwner = await _dBus.GetNameOwnerAsync("org.kde.StatusNotifierWatcher");
    ...
}

private void OnNameChange(string name, string? newOwner)   // 大意
{
    if (_isDisposed || _connection is null || name != "org.kde.StatusNotifierWatcher") return;

    if (!_serviceConnected && newOwner is not null)        // ← 只有"从无到有"才重建
    {
        _serviceConnected = true;
        _statusNotifierWatcher = new StatusNotifierWatcher(...);
        DestroyTrayIcon();
        if (_isVisible) CreateTrayIcon();
    }
    else if (_serviceConnected && newOwner is null)         // ← 只有"从有到无"才销毁
    {
        DestroyTrayIcon();
        _serviceConnected = false;
    }
}
```

`CreateTrayIcon()` 每次都会**换一个新的 item 名**并从零注册：

```csharp
var tid = s_trayIconInstanceId++;
_sysTrayServiceName = $"org.kde.StatusNotifierItem-{pid}-{tid}";
await _connection.RequestNameAsync(_sysTrayServiceName);
await _statusNotifierWatcher.RegisterStatusNotifierItemAsync(_sysTrayServiceName);
```

两个问题：

1. **触发条件太窄**：只处理"跟 `null` 有关"的跃迁。宿主以其他方式重启/面板重载而 watcher 名号没变
   （上游 #13130 描述的正是这种）时，**一次通知都不会来**，什么都不会发生。
2. **每次都换名**：对"扫描总线"的宿主（gnome appindicator 扩展）会造成重复条目（#21978）。

### 3.2 证据链：这次连重建都没发生

如果 `OnNameChange` 的任一分支跑过，`DestroyTrayIcon()` 会 `ReleaseNameAsync()` **把我们的 item 名释放掉**。
而用户观测到 `org.kde.StatusNotifierItem-1228-0` **一直还在** → **恢复分支根本没执行**
（不是"执行了但失败"）。所以这次故障是"**没有任何触发**"这一类。

### 3.3 上游状态（截至 2026-09-15）

| 项 | 状态 |
| --- | --- |
| `AvaloniaUI/Avalonia#13130`（Tray icon disappears on Linux） | **open**（2023-10 开至今，间歇性：锁屏/休眠恢复、宿主重启等） |
| `AvaloniaUI/Avalonia#21980`（Fix duplicated and crashing DBus tray icon） | **open、未合并**，label `backport-candidate-12.1.x`，最后更新 2026-09-14 |
| 它修什么 | `#21978` 重复图标（对象在拿到名字前就导出 + 每次 watcher 重启换新名）、`#21979` 退出崩溃（`async void` 异常 → exit code 134） |
| 它明确不修什么 | 正文原话：`Related to #13130 (item lost when the host restarts while the watcher name stays; **not addressed here**)` |

不过它**顺带把"watcher 名号消失又回来"这条路径做对了**（对我们有用，见 §5-L2）：

```diff
   if (!_serviceConnected && newOwner is not null) {
       _serviceConnected = true;
       _statusNotifierWatcher = new StatusNotifierWatcher(...);
-      DestroyTrayIcon();                                  // 旧：销毁 → 名字被释放
       if (_isVisible) CreateTrayIcon();
   }
...
-      var tid = s_trayIconInstanceId++;                   // 旧：每次都换新名字
+      if (_sysTrayServiceName is null) { ... }            // 新：名字只生成一次，一直复用
       await _statusNotifierWatcher.RegisterStatusNotifierItemAsync(_sysTrayServiceName);
```

即：**保留同一个 item 名 + 重新登记**（与用户手工那条 `busctl` 等价）。

---

## 4. 两类故障路径（先分类，再谈修复）

| 情形 | 是否发出 watcher 通知 | 原生 11.3 | #21980 | carton 兜底（L0） |
| --- | --- | --- | --- | --- |
| A. watcher（宿主）名号**消失后又回来**（整个 Quickshell 重启） | 会 | 走销毁+换新名（宿主可能记岔） | **保留名字重新登记** ✅ | ✅ |
| B. 宿主/面板重启，但 **watcher 名号一直没变**（#13130 原始情形） | 不会 | ❌ 什么都不做 | ❌ 仍然什么都不做 | ✅（不依赖通知） |
| C. 会话总线短暂抖动/重连、锁屏/休眠恢复 | 不一定 | 部分 | 部分 | ✅ |
| D. 宿主彻底不在（没有 watcher） | — | Avalonia 自己的"watcher 出现"分支会补 | 同左 | 无效（也没必要） |

---

## 5. 修复方案

### L0 —— 应用侧兜底（**已实现**，`fc7dad5`）

`src/carton.GUI/Services/TrayMenuService.cs`：

- **只在 Linux 生效**（`OperatingSystem.IsLinux()`），Windows/macOS 一行都不跑；
- 定时器每 **5 分钟**（`LinuxTrayRegisterInterval`）在 UI 线程执行 `ReRegisterTrayIcon("watchdog")`；
- 实现就是一次可见性翻转：

```csharp
icon.IsVisible = false;   // Avalonia 内部：DestroyTrayIcon() → ReleaseNameAsync(旧名)
icon.IsVisible = true;    // Avalonia 内部：CreateTrayIcon() → RequestNameAsync(新名)
                          //              + RegisterStatusNotifierItemAsync(新名)
```

  → 等价于用户手工那条 `busctl … RegisterStatusNotifierItem`，但由程序按时自己做。
  **不重建菜单、不重启进程、不中断代理**；宿主在毫秒级重新看到条目。

- 守卫：仅在 `_isInitialized` 且图标可见时执行；`Interlocked` 防重入；全程 `try/catch`，
  失败只写日志（`MainViewModel.Log` → 日志页 `[INFO] Re-registered tray icon (watchdog)`）。

**取舍（务必知道）**：

| 代价 | 说明 |
| --- | --- |
| 恢复延迟上限 5 分钟 | 一个 `const` 可调（1 分钟更灵敏、churn 更大；30 分钟更安静） |
| item 名递增 | 每次 `-{pid}-{k}`，旧名会被释放（宿主据此移除旧条目） |
| 极短闪烁 | 释放→重新登记之间 ~ms 级空档，正常图标可能"闪一下" |
| 扫描总线型宿主可能短暂两条 | 那是上游 #21978 的宿主行为，与本次改动无关 |
| 宿主完全不存在时无效 | 但那种情况 Avalonia 自己的分支会处理 |

### L1 —— 自研 DBus 监听（**设计已定，未实现**，需要 Linux 侧验证）

用 `Tmds.DBus.Protocol`（已随 `Avalonia.FreeDesktop` 进入依赖图，`~/.nuget/packages/tmds.dbus.protocol`）：

1. 连会话总线，`AddMatch` 监听 `org.kde.StatusNotifierWatcher` 的 `NameOwnerChanged`；
2. 只要 `newOwner != null`（宿主/watcher 出现或重新出现），立刻调
   `RegisterStatusNotifierItem(<我们当前的 item 名>)` —— **不换名字、不销毁对象**，毫秒级恢复；
3. 找不到 item 名时（例如名字丢失）退化为 L0 的可见性翻转。

优点：精确、即时、无名字 churn、无闪烁，还能覆盖"carton 先起、宿主后起"。
代价：约 60 行自研 DBus 代码（新维护面），且**必须在 Linux 上实测**（本机是 Windows，只能做编译级验证）。
验证顺序建议：先只打日志确认能收到信号 → 再打开重注册。

### L2 —— 等上游（根治方向）

等 Avalonia 带上 #21980（目标 12.1+）后升级：名字只生成一次、watcher 变化保留名字重新登记、退出不再崩。
届时的收敛计划：**把 L0 的间隔放大到 30 分钟或直接删除**（若 L1 已上线则删 L0）。
注意升级成本：carton 现在 `Avalonia 11.3.18` → 12.x 属**跨大版本**，需要全 UI 回归；
而且它仍不覆盖情形 B。

### L3 —— 应急（用户自助）

```bash
# 1) 找到 carton 自己的 item 名（按进程 pid 匹配）
pid=$(pgrep -x carton | head -1)          # 拿不到就试 pgrep -f carton
item=$(busctl --user list | grep -o "org\.kde\.StatusNotifierItem-${pid}-[0-9]*" | head -1)
echo "$item"

# 2) 重新登记（不重启 carton，不断代理）
busctl --user call org.kde.StatusNotifierWatcher /StatusNotifierWatcher \
  org.kde.StatusNotifierWatcher RegisterStatusNotifierItem s "$item"

# 3) 确认已回到登记簿
busctl --user get-property org.kde.StatusNotifierWatcher /StatusNotifierWatcher \
  org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems
```

---

## 6. 验证与"是谁救的"

**item 名指纹**（能直接区分是哪条机制在起作用）：

| 构建 | watcher 重启后的 item 名 |
| --- | --- |
| 原生 Avalonia 11.3 | 变成 `-{pid}-1`（若重注册成功）；失败则旧名仍在但不在登记簿 |
| 打了 #21980 的 Avalonia | **全程保持 `-{pid}-0` 不变** |
| carton 的 L0 兜底 | **每 5 分钟递增**（`-0`、`-1`、`-2`…），日志有 `Re-registered tray icon (watchdog)` |

**测试矩阵**（建议每个都做一次）：

1. 正常重启宿主（Quickshell/Caelestia）；
2. `kill -9` 宿主进程；
3. 宿主**连续重启两次**；
4. 启动顺序两种：先宿主后 carton / 先 carton 后宿主；
5. 锁屏 → 解锁；休眠 → 恢复。

**每步怎么判定**：

```bash
# 1) 看登记簿里有没有我们
busctl --user get-property org.kde.StatusNotifierWatcher /StatusNotifierWatcher \
  org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems

# 2) 看名字在不在总线上（与我们自己的 pid 对比）
busctl --user list | grep 'org.kde.StatusNotifierItem-'

# 3) 看 carton 日志页是否有 [INFO] Re-registered tray icon (watchdog)
```

---

## 7. 仍未覆盖的情形（诚实清单）

1. **宿主重启而 watcher 名号不变**（情形 B）：原生和 #21980 都不管；靠 L0 每 5 分钟兜（最长 5 分钟空档），
   L1 上线后可做到毫秒级。
2. **"名字在、但宿主就是不显示"**（宿主侧缓存/去重异常）：客户端无法察觉，仍无解。
3. **L0 的 5 分钟空档**：本身是设计取舍；L1 可消除。
4. **本仓库未在 Linux 上跑过 L0**：只有编译 + `carton.GUI.Tests` 278 用例 + Windows 冒烟；
   Linux 行为需按 §6 实测确认。

---

## 8. 代码 / 提交 / 链接索引

| 类别 | 位置 |
| --- | --- |
| 兜底实现 | `src/carton.GUI/Services/TrayMenuService.cs`（`LinuxTrayRegisterInterval`、`_linuxTrayWatchdog`、`ReRegisterTrayIcon`、`DestroyTrayIcon`） |
| 日志出口 | `src/carton.GUI/ViewModels/MainViewModel.cs` → `Log(string)` |
| 提交 | `fc7dad5 fix(linux): 托盘宿主重启后自动重新注册托盘图标` |
| 相关提交 | `8dd8cbc feat(groups): …`（同批的 groups 改动，含菊花动画只在测速时存在） |
| 规范 | <https://specifications.freedesktop.org/status-notifier-item/latest/status-notifier-watcher.html> |
| 上游 issue | <https://github.com/AvaloniaUI/Avalonia/issues/13130>（图标消失，open） |
| 上游 PR | <https://github.com/AvaloniaUI/Avalonia/pull/21980>（重复图标 + 退出崩溃，open，12.1.x） |

**其他现场信息**：用户机器上有两个 carton 自启动入口（`~/.config/autostart/carton.desktop` 与
`~/.config/caelestia/hypr-user.lua`）。carton 有单实例保护（`Program.cs` → `SingleInstanceService.TryClaim`，
第二个实例只通知已有实例后退出），**不是本次原因**，但建议只保留一个入口以免开机竞态。

---

## 附录 A：Avalonia 11.3 关键代码（摘要）

```csharp
// 构造：先导出对象，再（WatchAsync 里）申请名字
_connection.AddMethodHandler(_statusNotifierItemDbusObj);
WatchAsync();

// CreateTrayIcon：每次新名字 + 注册
var tid = s_trayIconInstanceId++;
_sysTrayServiceName = $"org.kde.StatusNotifierItem-{pid}-{tid}";
await _connection.RequestNameAsync(_sysTrayServiceName);
await _statusNotifierWatcher.RegisterStatusNotifierItemAsync(_sysTrayServiceName);

// DestroyTrayIcon：释放名字 + 摘掉对象
_connection!.ReleaseNameAsync(_sysTrayServiceName);
_connection.RemoveMethodHandler(_statusNotifierItemDbusObj.Path);
```

## 附录 B：PR #21980 要点

- `_sysTrayServiceName` 只生成一次、长期持有；请求/释放用 `_sysTrayServiceNameRequest`/`...Release` 串行化；
- 对象在**拥有名字之后**才导出（`_itemExported`），修掉"扫描总线 → 两条"；
- `OnOwnerChanged` 里删除 `DestroyTrayIcon()`（watcher 回来时**保留名字**只重新登记）；
- `WatchAsync`/`CreateTrayIcon` 全量 `catch`（含 `OperationCanceledException`），修掉 `async void` 结束进程；
- 作者已在 GNOME 48.5 + AppIndicator 扩展上人工验证（重启扩展、hide/show、dispose）；
- 仍未覆盖 #13130 的"宿主重启而 watcher 名号不变"。

## 附录 C：术语

| 术语 | 含义 |
| --- | --- |
| watcher | `org.kde.StatusNotifierWatcher`，登记簿的持有者（本例：Quickshell/Caelestia） |
| host | 真正把条目画成图标的宿主面板（本例与 watcher 同进程） |
| item | 应用侧的 `/StatusNotifierItem` 对象 + 它的总线名 `org.kde.StatusNotifierItem-{pid}-{n}` |
| 注册 | item 调 `RegisterStatusNotifierItem(服务名)` 把自己写进 watcher 的登记簿 |
| 登记簿 | `RegisteredStatusNotifierItems` 属性，watcher 内存里的列表，**重启即清空** |
