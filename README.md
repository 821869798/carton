[English](./README.en.md) | 简体中文

# carton

[![Telegram Group](https://img.shields.io/badge/Telegram-%E4%BA%A4%E6%B5%81%E7%BE%A4-26A5E4?style=for-the-badge&logo=telegram&logoColor=white)](https://t.me/+fwutL7igOTk3ZmFl)

`carton` 是一个基于 `sing-box` 的桌面客户端，交互和信息组织尽量贴近官方 SFM，同时更看重性能、响应速度，以及一些更实用的增强功能。

目前支持 `Windows` 和 `Linux`。暂不提供 `macOS` 版本，因为 macOS 上已经有 SFM。

项目方向：

- 尽量贴近官方 SFM 的体验，降低迁移成本
- 更看重界面响应、启动速度和长期运行时的资源占用
- 直接使用你自己的配置和规则启动 `sing-box`，只额外提供少量开关式选项
- 在不打乱主流程的前提下补上一些实用能力
- 使用非 Electron / Tauri / Web 技术栈的桌面实现，内存占用更低、性能更好

> `carton` 不是官方 SFM 客户端，也不隶属于 sing-box 官方团队。

## 界面预览

| Dashboard | Groups |
| --- | --- |
| ![Dashboard](./docs/imgs/dashboard.png) | ![Groups](./docs/imgs/group.png) |
| Connections | Profiles |
| ![Connections](./docs/imgs/connection.png) | ![Profiles](./docs/imgs/profile.png) |

## 主要特性

### 贴近官方 SFM 的主流程

- Dashboard / Groups / Profiles / Connections / Logs / Settings 六个核心页面
- 启动、停止、查看状态、切换分组等常用操作集中在主流程中
- 内置 Clash API / WebUI 入口，方便和现有使用习惯衔接

### 性能优先

- 基于 `Avalonia` + `.NET 10`
- 非 Electron、Tauri 和其他基于 Web 技术的桌面框架
- 很多同类方案在实际使用里，启动后内存占用很容易来到 `200MB+`
- 提供 `NativeAOT` 发布脚本，用来进一步改善启动速度和运行开销
- 页面按需加载，并对后台页面做了释放和刷新控制，减少长期运行时的资源占用

### 配置与订阅管理

- 支持本地配置创建、导入、编辑
- 支持远程订阅导入、手动更新、自动更新间隔
- 启动前可为不同配置保存独立运行参数
- 如果你没有 `sing-box` 订阅地址，可以使用 [`sublink-worker`](https://github.com/7Sageer/sublink-worker) 进行转换；它提供了 [`app.sublink.works`](https://app.sublink.works) 在线工具，可将多种订阅或协议链接转换为 `sing-box` 配置

### 节点与分组增强

- 读取并展示 `outbound groups`
- 支持节点切换、延迟测试、URLTest 结果刷新
- 托盘菜单可直接查看和切换分组
- 可选在切换节点后自动断开受影响的连接

### 实用附加功能

- 系统代理切换
- TUN / 监听端口 / LAN 访问 / 日志级别等运行时选项
- 实时流量、内存占用、会话时长、连接列表、日志查看
- 支持 sing-box 内核下载、更新、自定义内核安装与内核切换
- 应用更新通道、备份导出/导入、便携模式数据目录切换
- 中英文界面与主题设置

## 技术栈

- `Avalonia UI`
- `.NET 10`
- `CommunityToolkit.Mvvm`
- `sing-box`
- `Velopack`

## 开发与构建

### 平台

- `Windows` 
- `Linux`

### 环境要求

- `.NET 10 SDK`
- Windows NativeAOT 发布需要安装 `Desktop development with C++` 或等效的 MSVC / Windows SDK 构建工具链
- 如需生成安装包，还需要 `Velopack CLI (vpk)`；`scripts\build-release-win-x64.ps1` 会尝试自动安装或更新它

### 本地构建

```powershell
dotnet build carton.slnx
```

### 开发运行

```powershell
dotnet run --project src\carton.GUI\carton.GUI.csproj
```

### Windows NativeAOT 发布

```powershell
scripts\test-publish-win-aot.bat win-x64 Release
```

或使用带安装包封装的脚本：

```powershell
scripts\build-release-win-x64.bat
```

其中：

- `scripts\test-publish-win-aot.bat` 只执行 NativeAOT 发布
- `scripts\build-release-win-x64.bat` 会执行 `scripts\build-release-win-x64.ps1`，并额外生成便携压缩包和 Velopack 安装包

### Linux NativeAOT 发布

```bash
./scripts/test-publish-linux-aot.sh linux-x64 Release
```

输出目录为 `artifacts/publish/<rid>`。

仓库里已经包含多个运行时目标，现成脚本主要围绕 Windows AOT 构建流程整理。

## 项目定位

如果你更在意：

- 尽量接近官方 SFM 的体验
- 更偏性能取向的实现
- 一些官方客户端之外但又不喧宾夺主的实用功能

那么 `carton` 基本就是按这个方向来做的。

## License

本项目基于 GNU General Public License v3.0（GPL-3.0）开源，详见 [LICENSE](./LICENSE)。
