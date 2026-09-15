<div align="center">
  <p><img src="src/carton.GUI/Assets/carton_icon.png" alt="carton logo" width="100" /></p>
  <h1>carton</h1>
  <p><strong>基于 sing-box 的轻量、原生高性能跨平台桌面客户端</strong></p>
  <p>基于 Avalonia 与 .NET 10 构建 · 原生渲染 · 无 Electron / WebView · 极低内存开销</p>

  <p>
    <a href="https://github.com/821869798/carton/releases/latest"><img src="https://img.shields.io/github/v/release/821869798/carton?style=for-the-badge&color=blue" alt="Latest Release" /></a>
    <a href="https://github.com/821869798/carton/releases"><img src="https://img.shields.io/github/downloads/821869798/carton/total?style=for-the-badge&color=2ea44f" alt="Downloads" /></a>
    <a href="https://t.me/+fwutL7igOTk3ZmFl"><img src="https://img.shields.io/badge/Telegram-%E4%BA%A4%E6%B5%81%E7%BE%A4-26A5E4?style=for-the-badge&logo=telegram&logoColor=white" alt="Telegram" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0--or--later-blue?style=for-the-badge" alt="License: GPL-3.0-or-later" /></a>
  </p>

  <p>
    <a href="./README.en.md">English</a> ·
    <a href="./README.md">简体中文</a> ·
    <a href="#安装与下载">安装与下载</a> ·
    <a href="#界面预览">界面预览</a> ·
    <a href="#主要特性">主要特性</a> ·
    <a href="#技术栈">技术栈</a> ·
    <a href="#开发与构建">开发与构建</a> ·
    <a href="https://github.com/821869798/carton/releases/latest">最新版本</a>
  </p>

  <p><img src="docs/imgs/banner.png" alt="carton 概览" width="860" /></p>
</div>

`carton` 是一个基于 `sing-box` 的桌面客户端，交互和信息组织尽量贴近官方 SFM，同时更看重性能、响应速度，以及一些更实用的增强功能。

目前支持 `Windows` 和 `Linux`。暂不提供 `macOS` 版本，因为 macOS 上已经有 SFM。

项目设计：

- 贴近官方 SFM 交互与主流程，降低上手和迁移成本
- 原生性能取向，专注更快的响应与更低的常驻资源开销
- 尊重用户原配置，不在运行时大面积覆写配置与分流规则
- 在不打乱核心主流程的前提下，提供实用的增强功能

## 安装与下载

> [!IMPORTANT]
> **官方渠道安全提示**：请**务必只通过官方渠道下载或安装**。请勿轻信任何未经授权的第三方分发渠道、非官方打包或未知来源的安装文件，以防安全风险。
> 目前**尚未提供 Linux 在线安装源**（如 APT / DNF / AUR / PPA / Flatpak / Snap 等），任何宣称包含 carton 的非官方 Linux 在线源均非官方维护。

### Windows

- **通过 WinGet 安装（推荐）**：
  ```powershell
  winget install Unifan.Carton
  ```
- **通过 GitHub Releases 安装包或便携版**：
  前往 [GitHub Releases 最新发布页](https://github.com/821869798/carton/releases/latest) 下载最新的安装包（`Setup.exe`）或便携版压缩包。

### Linux

- 目前**尚未提供 Linux 在线包管理器安装源**。
- 请前往官方 [GitHub Releases 最新发布页](https://github.com/821869798/carton/releases/latest) 下载预编译的二进制压缩包（`carton-*-linux-*.tar.gz`），解压即可使用；或参考下方说明自行编译构建。

## 安装

普通用户直接到 [Releases](https://github.com/821869798/carton/releases) 下载对应平台的包：

| 平台 | 推荐文件 | 说明 |
| --- | --- | --- |
| Windows | `-win-x64-Setup.exe` | 安装版，支持应用内自动更新 |
| Windows | `-win-x64-portable.zip` | 免安装，数据保存在程序目录 |
| Ubuntu / Debian | `-linux-x64.deb` | `sudo apt install ./carton-<版本>-linux-x64.deb` |
| Fedora / openSUSE | `-linux-x64.rpm` | `sudo dnf install ./carton-<版本>-linux-x64.rpm` |
| Arch / CachyOS / EndeavourOS | AUR `carton-bin` | `yay -S carton-bin` |
| 其他发行版 | `-linux-x64.AppImage` | 免安装；Ubuntu 24.04+ 需要额外装 `libfuse2` |
| 其他发行版 | `-linux-x64-portable.tar.gz` | 解包即用，支持应用内自动更新 |

用 `.deb` / `.rpm` / AUR 安装时，应用**不会自更新**（避免覆盖 `/usr` 下的文件、避免与包管理器数据库脱节）：
应用内的"检查更新"仍然可用，检测到新版本时不再引导你去下载自更新，而是提示对应的升级方式
（AUR 直接给出 `yay -Syu`，deb/rpm 提示下载新版安装包覆盖安装）。

## 配置复写说明

`carton` 启动时不会直接整份覆盖你的 `sing-box` 配置，而是在原配置基础上生成运行时配置，只修改少量和桌面开关直接相关的内容。
这样设计是因为很多用户都很反感第三方 GUI 大面积覆盖自己已经配好的配置和规则，`carton` 会尽量少碰你原本手写或订阅生成的内容。

- `log`：只会更新 `log.level`；如果原配置没有 `log`，才会补一个最小可用的 `log` 对象
- `inbounds`：只会处理 `mixed` 和 `tun` 两类入口
- 对已有 `mixed` inbound，只更新 `listen`、`listen_port`、`set_system_proxy`，其他字段保持原样
- 对已有 `tun` inbound，优先保留原配置中的 `address`，缺失时才补上默认地址；`auto_route` 和 `strict_route` 会更新为 `true`
- 如果配置里原本没有对应的 `mixed` / `tun` inbound，运行时才会补上；如果关闭 `tun`，则会移除对应的 `tun` inbound

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

- 原生桌面渲染，秒级冷启动，界面响应流畅
- 极低内存开销，日常运行远低于基于 Web/Electron 的同类客户端
- 深度优化后台常驻与按需调度，长期运行无负担、不卡顿

### 配置与订阅管理

- 支持本地配置创建、导入、编辑
- 支持远程订阅导入、手动更新、自动更新间隔
- 启动前可为不同配置保存独立运行参数
- 如果你没有 `sing-box` 订阅地址，可以使用 [`sublink-worker`](https://github.com/7Sageer/sublink-worker) 进行转换；它提供了 [`app.sublink.works`](https://app.sublink.works) 在线工具，可将多种订阅或协议链接转换为 `sing-box` 配置。若是机场用户，请在 `User Agent` 中输入 `clash` 或 `xray`

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
- Windows 本地调试 TUN 提权或发布包需要 `Rust toolchain`（`cargo`）
- Windows NativeAOT 发布需要安装 `Desktop development with C++` 或等效的 MSVC / Windows SDK 构建工具链
- 如需生成 Windows 安装包，还需要 `NSIS`，并确保 `makensis` 可用；GitHub Actions 会自动安装 NSIS

### 本地构建

```powershell
dotnet build carton.slnx
```

### 开发运行

```powershell
dotnet run --project src\carton.GUI\carton.GUI.csproj
```

Windows Debug 构建会在检测到 `cargo` 时自动构建并复制 `carton-helper.exe` 到 GUI 输出目录；如果未安装 Rust，应用仍可启动，但本地调试时的 Windows TUN 提权启动不可用。

### 单元测试

```powershell
dotnet test src\carton.GUI.Tests\carton.GUI.Tests.csproj
cargo test --manifest-path src\carton.Helper\Cargo.toml
```

向 `main` 的 push / PR 会通过 `.github/workflows/ci.yml` 自动跑上述构建与测试。

### Windows NativeAOT 发布

```powershell
scripts\build\test-publish-win-aot.bat win-x64 Release
```

或使用带安装包封装的脚本：

```powershell
scripts\build\build-release-win-x64.bat
```

其中：

- `scripts\build\test-publish-win-aot.bat` 只执行 NativeAOT 发布
- `scripts\build\build-release-win-x64.bat` 会执行 `scripts\build\build-release-win-x64.ps1`，并额外生成便携压缩包和 NSIS 安装包

### Linux NativeAOT 发布

```bash
./scripts/build/test-publish-linux-aot.sh linux-x64 Release
```

输出目录为 `artifacts/publish/<rid>`。

生成 `.deb` / `.rpm`（包管理器托管，应用内不自更新）：

```bash
# 先用 INSTALLER_BUILD 发布（AppImage / .deb / .rpm 共用这一份）
./scripts/build/test-publish-linux-aot.sh linux-x64 Release artifacts/publish/linux-x64-appimage INSTALLER_BUILD
# 再打包（需要 nfpm，脚本会自动下载到 artifacts/tools/）
./scripts/build/build-linux-packages.sh linux-x64 1.2.3 artifacts/publish/linux-x64-appimage
```

`linux-arm64` 同理，把 rid 换掉即可。AUR 包见 [`packaging/aur/`](./packaging/aur/README.md)。

CI 除 deb/rpm 外还会产出 `-package.tar.gz`（INSTALLER_BUILD 那一份，去掉 `carton-helper`
与便携标记），AUR 的 `carton-bin` 就是用它重打包的：portable 变体虽然也能删文件绕过，
但编译期常量 `IsPortableDistributionBuild` 修不了，设置页会多出一个在本安装方式下
不可能生效的"数据存到程序目录"选项。

仓库里已经包含多个运行时目标，现成脚本主要围绕 Windows AOT 构建流程整理。

## License

本项目基于 GNU General Public License v3.0 或更高版本（GPL-3.0-or-later）开源，详见 [LICENSE](./LICENSE)。

### 第三方代码

本仓库内置（vendored）了以下第三方源码，其许可证与署名均随源码保留：

| 组件 | 位置 | 上游 | 许可证 |
|---|---|---|---|
| Downloader 5.9.5 | [`src/carton.Core/Downloader/`](./src/carton.Core/Downloader/) | [bezzad/Downloader](https://github.com/bezzad/Downloader) | MIT，见 [LICENSE](./src/carton.Core/Downloader/LICENSE) |

内置原因、本地改动与同步上游的步骤见 [`src/carton.Core/Downloader/README.md`](./src/carton.Core/Downloader/README.md)。
