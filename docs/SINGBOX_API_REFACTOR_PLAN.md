# carton sing-box 原生 API 重构计划（参考 sing-box-dashboard）

> 状态：Phase 0 / 1 / 2 / 3 全部落地；剩余为实机验证项。
> 参考实现：本仓库 `sing-box-dashboard/`（官方 Web 控制台源码，与本客户端对接同一 `daemon.StartedService` gRPC API）。

## 0. 现状与目标

carton 已把传输层从 clash REST/WS 换成 sing-box 1.14+ 的原生 gRPC（`SingBoxGrpcApiClient`）。
本轮 review 对照官方 dashboard 与 sing-box v1.14.0 源码，确认了架构方向正确，但发现多处
"用了新 API、按旧思维用"的问题。本计划分四个 Phase 把它收敛为**流式优先**的客户端，
对齐官方 dashboard 的使用模式。

### 官方 dashboard 的 API 使用模式（事实基准）

| 关注点 | dashboard 做法（源码位置） | carton 现状 |
| --- | --- | --- |
| 订阅间隔 | `SUBSCRIPTION_INTERVAL = 1_000_000_000n`（ns，1s）（`src/api/daemon.ts:111`） | ✅ Phase 0 已修复（原 `1000` 即 1µs） |
| 数据源 | 每类数据一个**长驻流**（StreamStore：groups/connections/log/status），UI 从 store 读快照 | ❌ 轮询：Groups/Connections 页定时新建订阅→读首条→弃流 |
| 连接数据 | `subscribeConnections` 长流 + NEW/UPDATE/CLOSED 差量合并（`daemon.ts:254-313`） | ❌ 每 2s 重订阅取全量快照 |
| 测速 | `urlTest(groupTag)` fire-and-forget，结果由 groups 流推回（`GroupsView.tsx:53-59`） | ⚠️ Phase 0 已修成同模式（基线+等新鲜快照） |
| 日志 reset | `message.reset === true` 清空缓冲（`daemon.ts:228-236`） | ✅ Phase 0 已接通（LogsReset 事件） |
| API 能力门控 | `GetVersion().apiVersion` + `MIN_API_VERSION` 能力开关（`capabilities.ts`） | ❌ 未使用 |
| 组展开状态 | `SetGroupExpand`（服务端持久化到 cache.db） | ❌ UI 本地记状态 |

## Phase 0 —— 正确性修复（本轮已完成 ✅）

| # | 问题 | 修复 |
| --- | --- | --- |
| 0.1 | `Interval=1000` 是纳秒→1µs：流量/内存监控两端 CPU 洪泛、速度恒 0 | `SubscribeIntervalNanoseconds = 1_000_000_000` + `ToSubscriptionIntervalNanoseconds()` 换算，补单测锁定契约 |
| 0.2 | 测速返回 cache.db 里的旧值：`RunGroupDelayTestAsync` 首条快照即 break；`RunOutboundDelayTestsAsync` 只读一条 | 重写：先订阅取**基线 UrlTestTime**，再触发 `URLTest`，等 `UrlTestTime` 前进或 delay 变化的新快照（`IsFreshUrlTestResult`，纯函数+单测） |
| 0.3 | `experimental.Remove("clash_api")` 灭掉 Clash 模式（v1.14.0 `GetClashModeStatus` 在 clashServer==nil 时返回 NotFound）且 `clash_mode` 路由规则永不匹配 | 保留**最小 clash_api**：`external_controller: ""`（不监听 REST 但保留 ClashServer），`DashboardViewModel` + `ConfigManager` 模板 + `GenerateConfigAsync` 三处一致 |
| 0.4 | <1.14.0 内核撞上 `services.type=api`/`store_dns` → FATAL unknown field | **三层硬拦截**（`KernelVersionGuard`，不做向下兼容）：启动时 `StartAsync` 探测拒绝并弹框（`KernelVersionRejected` 事件）；在线下载在下载前按版本号拒绝；自定义安装先探测源二进制再拒绝。拦截消息统一弹窗提示 |
| 0.5 | 初始连接快照含已关闭连接（服务端把 ClosedConnections 也作为 NEW 推送） | `GetConnectionsAsync` 过滤 `ClosedAt > 0`，恢复旧 REST 语义 |
| 0.6 | 日志流 `reset=true` 被忽略：每次重连重放 ~3000 行历史造成重复 | `ISingBoxApiClient.LogsReset` 事件 → `SingBoxManager.KernelLogsReset` → `MainViewModel` 清 LogStore 的 SingBox 部分（`LogStore.RemoveSource` 新增） |
| 0.7 | 每次 API 调用/监控重连 new 一个 GrpcChannel 且永不释放（连接页 2s 一轮 → 连接堆积） | `SingBoxApiClientFactory` 缓存**单例客户端**（内部已按地址重建 channel），`SingBoxManager.Dispose` 时 `Reset()` |
| 0.8 | "Clash API WebUI" 菜单指向已删除的 REST `/ui/`（死链）；`ReloadAsync` 死代码 | 删除菜单项/命令/`BuildClashWebUiUrl`/本地化键/`ReloadAsync`；非 native 分支按钮改指 sing-box dashboard |
| 0.9 | `store_dns: true` 覆盖用户显式配置 | 仅在未显式设置时注入 |

## Phase 1 —— 流式化 Groups/Connections（消除轮询）（已完成 ✅）

> 官方 dashboard 的 StreamStore 模式已在 carton 落地：**长驻流 + 服务端推送 + 事件驱动 UI**。

1. ✅ **`SingBoxManager.Streaming.cs`**：内核 Running 期间维持 `SubscribeGroups`、`SubscribeConnections` 两条长流
   （与 log/status 监控并列，复用重连退避框架），按 dashboard `daemon.ts` 的规则差量合并
   （NEW upsert、UPDATE 累加 delta、CLOSED 移除、reset 重建；初始快照中 ClosedAt>0 的关闭连接被跳过）。
   对外暴露 `CurrentConnections`/`CurrentGroups` 快照与 `ConnectionsUpdated`/`GroupsUpdated` 事件。
2. ✅ **ConnectionsViewModel**：删除 2s DispatcherTimer 轮询，改订阅 `ConnectionsUpdated`；
   关闭连接后不再手动刷新（内核 CLOSED 事件自动到）。
3. ✅ **GroupsViewModel**：删除 2.5s urltest 轮询 timer 及整条 `RefreshUrlTestGroupsAsync` 轮询链，
   改订阅 `GroupsUpdated`（内核在每次 url 测速历史/选择变化时推送全新快照）。
4. ✅ 合并规则单测锁定（`SingBoxManagerStreamingTests`：reset 重建/delta 累加/closed 移除/未知 id 忽略/关闭连接过滤）。

## Phase 2 —— 架构收敛（去双轨）（已完成 ✅）

1. ✅ **状态流合一**：流量+内存两条 SubscribeStatus 流合并为单条 `StartStatusMonitorAsync`
   （`SubscribeAggregatedStatusAsync`）；总流量改用内核 `uplinkTotal/downlinkTotal` 精确值，
   `trafficAvailable=false` 时才降级客户端累加；`SubscribeTrafficAsync`/`SubscribeMemoryAsync`
   及其接口成员已删除。
2. ✅ **`ApiPortPlanner` 收敛**：clash 半边参数删除（clash_api 已无监听端口），单端口规划
   `ApiPortPlan(NativeApiPort)`；`DefaultClashApiPort` 常量移除；测试同步重写。
3. ✅ **`ISingBoxApiClient` 接口扩展**：`SubscribeAggregatedStatusAsync`/`SubscribeConnectionEventsAsync`/
   `SubscribeGroupSnapshotsAsync` 成为正式成员；死方法（`SubscribeGroupsStreamAsync`/
   `SubscribeStatusStreamAsync(intervalMs)`）移除。
4. **测速语义统一**（后续）：单节点测速入口改为"父组 URLTest + 从流式快照取该节点新值"，
   随 GroupsViewModel 后续 UX 打磨一起做。
5. **`SupportsNativeApi` 简化**（后续）：`minor == 14 && patch >= 0` 恒真分支化简为 `minor >= 14`。
6. **`HttpClientFactory` 双轨**（后续）：LocalApi/LocalClashApi 合并，Windows helper 已用绝对 URI 不受影响。

## Phase 3 —— 新能力接入（已完成 ✅，详见 SINGBOX_API_FEATURES.md）

- ✅ **`GetVersion().apiVersion` 能力门控**：`SingBoxGrpcApiClient.GetServerVersionAsync()` +
  `ApiVersion` 属性 + `SingBoxManager.SupportsApiFeature(feature)`（官方 dashboard 的
  MIN_API_VERSION 表：usbip≥2、openVpnAndOpenConnect≥3、taildrop≥4）；
  `SyncRunningStateAsync` attach 外部实例时同步刷新 `CartonApplicationInfo`。
- ✅ **`SetGroupExpand` 服务端持久化组展开**：展开/收起时同步写内核 cache.db，
  重启内核与多端（官方 dashboard）状态一致。
- ✅ **`GetDeprecatedWarnings` 配置体检**：内核运行后自动拉取，把"X 将在 Y 版本移除"
  警告打进用户可见日志（一次运行期一次），内核 1.14 自身弃用 clash_api 的场景直接受益。
- **暂不接入**（功能清单 P3/P4）：NetworkQualityTest/STUN（等独立 UI 入口）、
  Tailscale/Taildrop/USB/IP/OpenConnect/OpenVPN 全家（需要 apiVersion≥4 或产品定位外）。

## 后续简化议题（来自外部 review，价值高）

- **原生内置面板**：sing-box v1.14.0 的 api service 原生支持 `dashboard`
  配置块（`option/api.go:16`，`dashboard: {enabled: true}` 即由 api listener 同源
  伺服官方面板）——同源意味着**无需任何 CORS 白名单**，carton 的
  `SingBoxDashboardBootstrapService`（200+ 行、9092 端口代理面板）与本轮的
  access_control 逻辑均可退役。作为独立议题评估迁移。

## 自测体系（新增 ✅）

> **已知欠账**：runtime 配置 overlay（约 200 行，长在 DashboardViewModel 的巨型
> try 块内）零测试覆盖——access_control 取反 bug 正是因此溜过。方向：抽成
> carton.Core 的 `RuntimeConfigOverlay.Apply(JsonObject root, ...)` 纯函数，
> 照 ApiPortPlannerTests 范式覆盖 clash_api/cache_file 有无、allow_origin
> 数组/字符串/缺失、listen_port 有无等矩阵。作为独立重构议题执行。

三层测试金字塔（`dotnet test` 共 195 项，全部通过）：
1. **纯函数单测**：interval 纳秒换算 / 测速新鲜度判定 / 连接差量合并规则 / 版本门控
   （`SingBoxGrpcApiClientTests`、`SingBoxManagerStreamingTests`、`CartonApplicationInfoTests`）。
2. **Wire 级集成测试**（`SingBoxGrpcWireTests`，进程内 mock 内核）：用 Grpc.AspNetCore +
   Kestrel(h2c) 托管 `MockSingBoxServer`（复刻 sing-box 服务端契约：Bearer 认证、
   GetVersion 无状态、SubscribeGroups 初始快照+按推送节奏、SubscribeConnections
   reset 全量+CosedAt 关闭连接、URLTest 异步经组快照回流、interval=纳秒），
   真实 `SingBoxGrpcApiClient` 走完整 protobuf 序列化 + HTTP/2 传输验证。
3. **配置面回归**：`ApiPortPlannerTests`（单端口语义）等既有套件。

## 验证清单（每个 Phase 的完成定义）

- [x] `dotnet build carton.slnx` 0 警告 0 错误
- [x] `dotnet test` 全过（171 → 195：+24 项 gRPC 契约/wire 级/合并规则测试）
- [x] NativeAOT publish（win-x64，PublishAot=true）成功——gRPC/Protobuf AOT 兼容已实测
- [x] Phase 1：连接页/组页不再有轮询定时器；快照由内核推送驱动（wire 测试锁定推送语义）
- [x] Phase 3：apiVersion 门控 / SetGroupExpand / GetDeprecatedWarnings 接入并有测试
- [x] 内核版本硬门槛：`KernelVersionGuard`（≥1.14.0）三层拦截（启动/在线下载/自定义安装）+ 弹框 + 单测
- [ ] Phase 1 实机验证（需真实内核）：外部 dashboard 切节点后 carton UI <1s 同步；
      500+ 并发连接下差量合并内存平稳
- [ ] 合入前跑一次 `scripts/build/test-publish-linux-aot.sh linux-x64 Release`（CI 发布管线形态）
