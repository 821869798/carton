<div align="center">
  <p><img src="src/carton.GUI/Assets/carton_icon.png" alt="carton logo" width="100" /></p>
  <h1>carton</h1>
  <p><strong>A lightweight, native, high-performance cross-platform desktop client for sing-box</strong></p>
  <p>Built with Avalonia &amp; .NET 10 · Native Rendering · No Electron / WebView · Ultra-low Memory</p>

  <p>
    <a href="https://github.com/821869798/carton/releases/latest"><img src="https://img.shields.io/github/v/release/821869798/carton?style=for-the-badge&color=blue" alt="Latest Release" /></a>
    <a href="https://github.com/821869798/carton/releases"><img src="https://img.shields.io/github/downloads/821869798/carton/total?style=for-the-badge&color=2ea44f" alt="Downloads" /></a>
    <a href="https://t.me/+fwutL7igOTk3ZmFl"><img src="https://img.shields.io/badge/Telegram-Group-26A5E4?style=for-the-badge&logo=telegram&logoColor=white" alt="Telegram" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0--or--later-blue?style=for-the-badge" alt="License: GPL-3.0-or-later" /></a>
  </p>

  <p>
    <a href="./README.md">简体中文</a> ·
    <a href="./README.en.md">English</a> ·
    <a href="#installation--download">Installation</a> ·
    <a href="#screenshots">Screenshots</a> ·
    <a href="#highlights">Highlights</a> ·
    <a href="#technology-stack">Tech Stack</a> ·
    <a href="#building-and-development">Development</a> ·
    <a href="https://github.com/821869798/carton/releases/latest">Latest Release</a>
  </p>

  <p><img src="docs/imgs/banner.png" alt="carton overview" width="860" /></p>
</div>

`carton` is a desktop client powered by `sing-box`. It aims to stay close to the official SFM experience in interaction flow and information layout, while putting more weight on performance, responsiveness, and a few practical enhancements.

Currently supports `Windows` and `Linux`. There are no plans to publish a `macOS` version, because SFM already exists on macOS.

Design goals:

- Keep the experience close to official SFM to reduce migration cost
- Prioritize native performance, rapid responsiveness, and low resident memory
- Start `sing-box` with your own config and rules, without unnecessary overwrites
- Add useful enhancements without disrupting the main workflow

## Installation & Download

> [!IMPORTANT]
> **Official Distribution Notice**: Please **only download or install carton from official channels**. Do NOT trust or use any unauthorized third-party mirrors, re-packagers, or unverified sources to avoid security risks.
> Currently, **no online Linux package repositories are provided** (e.g. APT, DNF, AUR, PPA, Flatpak, Snap). Any online Linux repository claiming to host carton is unofficial and not maintained by this project.

### Windows

- **Install via WinGet (Recommended)**:
  ```powershell
  winget install Unifan.Carton
  ```
- **Download via GitHub Releases**:
  Visit the [GitHub Releases Page](https://github.com/821869798/carton/releases/latest) to download the latest setup installer (`Setup.exe`) or portable package.

### Linux

- **No online package repository is currently available.**
- Please download official pre-built binaries (`carton-*-linux-*.tar.gz`) directly from the [GitHub Releases Page](https://github.com/821869798/carton/releases/latest), unpack and run; or build from source using the instructions below.

## Config Override Behavior

`carton` does not directly overwrite your entire `sing-box` config at startup. It builds a runtime config on top of your original file and only changes a small set of fields that are directly tied to desktop-side toggles.
This is intentional because many users strongly dislike third-party GUIs overwriting carefully prepared configs and rules. `carton` tries to touch as little of your hand-written or subscription-generated content as possible.

- `log`: only updates `log.level`; if the original config has no `log` object, `carton` adds a minimal one
- `inbounds`: only touches `mixed` and `tun` inbounds
- For an existing `mixed` inbound, it only updates `listen`, `listen_port`, and `set_system_proxy`; other fields stay as-is
- For an existing `tun` inbound, the configured `address` is preserved and a default address is added only when it is missing; `auto_route` and `strict_route` are updated to `true`
- If the config does not already contain the corresponding `mixed` or `tun` inbound, `carton` adds it at runtime; if `tun` is turned off, the corresponding `tun` inbound is removed

> `carton` is not an official SFM client and is not affiliated with the sing-box team.

## Screenshots

| Dashboard | Groups |
| --- | --- |
| ![Dashboard](./docs/imgs/dashboard.png) | ![Groups](./docs/imgs/group.png) |
| Connections | Profiles |
| ![Connections](./docs/imgs/connection.png) | ![Profiles](./docs/imgs/profile.png) |

## Highlights

### Main workflow close to official SFM

- Six core pages: Dashboard, Groups, Profiles, Connections, Logs, and Settings
- Common actions such as start, stop, status check, and group switching are kept in the main workflow
- Built-in Clash API / WebUI entry to match existing usage habits

### Performance-oriented

- Native desktop rendering with fast startup and smooth UI responsiveness
- Minimal memory footprint, significantly lower than Web/Electron-based alternatives
- Optimized for long-running background operation with minimal resource overhead

### Config and subscription management

- Create, import, and edit local configs
- Import remote subscriptions with manual update and auto-update intervals
- Save per-profile runtime options before startup
- If you do not have a `sing-box` subscription URL, you can use [`sublink-worker`](https://github.com/7Sageer/sublink-worker); it provides the online tool [`app.sublink.works`](https://app.sublink.works) to convert various subscription formats or protocol links into `sing-box` configs. If you are using an airport subscription provider, set `User Agent` to `clash` or `xray`

### Node and group enhancements

- Read and display outbound groups
- Support node switching, latency testing, and URLTest refresh
- View and switch groups directly from the tray menu
- Optionally disconnect affected connections after node switching

### Practical extras

- System proxy toggle
- Runtime options for TUN, listen port, LAN access, and log level
- Real-time traffic, memory usage, session duration, connections, and logs
- sing-box kernel download, update, custom kernel installation, and kernel switching
- App update channels, backup export/import, and portable data directory switching
- Chinese and English UI with theme settings

## Tech Stack

- `Avalonia UI`
- `.NET 10`
- `CommunityToolkit.Mvvm`
- `sing-box`
- `Velopack`

## Development and Build

### Platforms

- `Windows`
- `Linux`

### Requirements

- `.NET 10 SDK`
- `Rust toolchain` (`cargo`) for Windows local TUN helper debugging and release packaging
- Windows NativeAOT publishing requires `Desktop development with C++` or an equivalent MSVC / Windows SDK toolchain
- If you want to generate the Windows installer, `NSIS` is also required and `makensis` must be available; GitHub Actions installs NSIS automatically

### Local build

```powershell
dotnet build carton.slnx
```

### Development run

```powershell
dotnet run --project src\carton.GUI\carton.GUI.csproj
```

Windows Debug builds automatically build and copy `carton-helper.exe` to the GUI output directory when `cargo` is available. Without Rust installed, the app can still start, but Windows TUN elevated startup is unavailable in local debugging.

### Unit tests

```powershell
dotnet test src\carton.GUI.Tests\carton.GUI.Tests.csproj
cargo test --manifest-path src\carton.Helper\Cargo.toml
```

Pushes and pull requests to `main` run the build and tests above via `.github/workflows/ci.yml`.

### Windows NativeAOT publish

```powershell
scripts\build\test-publish-win-aot.bat win-x64 Release
```

Or use the packaging script that also creates the installer:

```powershell
scripts\build\build-release-win-x64.bat
```

In practice:

- `scripts\build\test-publish-win-aot.bat` performs the NativeAOT publish only
- `scripts\build\build-release-win-x64.bat` runs `scripts\build\build-release-win-x64.ps1` and additionally creates the portable archive and NSIS installer

### Linux NativeAOT publish

```bash
./scripts/build/test-publish-linux-aot.sh linux-x64 Release
```

This script writes output to `artifacts/publish/<rid>`.

The repository already contains multiple runtime targets, while the current ready-to-use scripts are mainly organized around the Windows AOT build flow.

## License

This project is licensed under the terms of the GNU General Public License v3.0 or later (GPL-3.0-or-later). See [LICENSE](./LICENSE) for details.

### Third-party code

This repository vendors the following third-party source. Its licence and attribution are
retained alongside the sources:

| Component | Location | Upstream | Licence |
|---|---|---|---|
| Downloader 5.9.5 | [`src/carton.Core/Downloader/`](./src/carton.Core/Downloader/) | [bezzad/Downloader](https://github.com/bezzad/Downloader) | MIT, see [LICENSE](./src/carton.Core/Downloader/LICENSE) |

For why it is vendored, the local modifications, and how to re-sync with upstream, see
[`src/carton.Core/Downloader/README.md`](./src/carton.Core/Downloader/README.md).
