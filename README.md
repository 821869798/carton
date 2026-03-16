[English](./README.en.md) | 简体中文

# carton

`carton` 是一个基于 `sing-box` 的桌面客户端，目标是在交互体验和信息组织上尽量靠近官方 SFM，同时把重点放在高性能和一些更实用的增强能力上。

项目当前仍处于早期迭代阶段，但核心方向已经比较明确：

- 体验尽量贴近官方 SFM，减少迁移成本
- 以高性能为优先，保持界面响应和启动速度
- 在不破坏主流程的前提下补充一些额外能力
- 非 Electron、Tauri 以及其他基于 Web 技术的桌面壳

> `carton` 不是官方 SFM 客户端，也不隶属于 sing-box 官方团队。

## 主要特性

### 接近官方 SFM 的使用路径

- Dashboard / Groups / Profiles / Connections / Logs / Settings 六个核心页面
- 启动、停止、查看状态、切换分组等常用操作集中在主流程中
- 内置 Clash API / WebUI 入口，方便和现有使用习惯衔接

### 高性能优先

- 基于 `Avalonia` + `.NET 10`
- 非 Electron、Tauri 和其他基于 Web 技术的桌面框架
- 这样做的一个直接原因，是这类方案在很多实际应用里启动后就很容易来到 `200MB+` 的内存占用
- 提供 `NativeAOT` 发布脚本，面向更快启动和更轻的运行负担
- 页面按需加载，并对后台页面做了释放和刷新控制，降低长期运行时开销

### 配置与订阅管理

- 支持本地配置创建、导入、编辑
- 支持远程订阅导入、手动更新、自动更新间隔
- 启动前可为不同配置保存独立运行参数

### 节点与分组增强

- 读取并展示 outbound groups
- 支持节点切换、延迟测试、URLTest 结果刷新
- 托盘菜单可直接查看和切换分组
- 可选在切换节点后自动断开受影响连接

### 实用附加功能

- 系统代理开关
- TUN / 监听端口 / LAN 访问 / 日志级别等运行时选项
- 实时流量、内存占用、会话时长、连接列表、日志查看
- sing-box 内核下载、更新、自定义内核安装
- 应用更新通道、备份导出/导入、便携模式数据目录切换
- 中英文界面与主题设置

## 技术栈

- `Avalonia UI`
- `.NET 10`
- `CommunityToolkit.Mvvm`
- `sing-box`
- `Velopack`

## 开发与构建

### 环境要求

- `.NET 10 SDK`

### 本地构建

```powershell
dotnet build carton.slnx
```

### Windows NativeAOT 发布

```powershell
scripts\publish-win-aot.bat win-x64 Release
```

或使用带安装包封装的脚本：

```powershell
scripts\build-release-win-x64.bat
```

当前仓库已经包含多个运行时目标，现成的发布脚本主要围绕 Windows 构建流程整理。

## 项目定位

如果你想要的是：

- 尽量接近官方 SFM 的体验
- 更强调性能
- 同时希望补上一些官方客户端之外的实用功能

那么 `carton` 就是沿着这个方向在做。

## License

本项目基于 MIT License 开源，详见 [LICENSE](./LICENSE)。
