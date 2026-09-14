# scripts

按用途分目录，避免根下一堆平铺脚本。

| 目录 | 内容 |
|---|---|
| `build/` | 发布、打包、内核下载（CI 和 README 里用的都在这） |
| `installer/` | NSIS 安装脚本（`build-nsis-installer.ps1` 会引用） |
| `memory/` | 内存/CPU 实测，日常发布用不到 |
| `tools/` | 图标转换、helper 压测 |

仓库根一律用「脚本所在目录再往上两级」（`../..` / `\..\..`），不要再用一层 `..`。

常用入口：

```powershell
scripts\build\test-publish-win-aot.bat win-x64 Release
scripts\build\build-release-win-x64.bat
```

```bash
./scripts/build/test-publish-linux-aot.sh linux-x64 Release
```
