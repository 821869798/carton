# AUR (`carton-bin`)

Arch/CachyOS/EndeavourOS 用户的"在线安装"入口。包本身不编译任何东西，只是把 Release 里的
`-package.tar.gz`（INSTALLER_BUILD 那一份，app + sing-box 内核 + .NET 运行时都在里面）重新打包。

**不要用 `-portable.tar.gz`**：那是自更新+便携数据目录的变体，`carton-helper` 存在本身就
会打开应用内自更新（会去覆盖 `/usr` 下 root 所有的文件、把 pacman 数据库搞脏），编译期常量
`IsPortableDistributionBuild` 还会让设置页多出一个在本安装方式下不可能生效的"数据存到程序目录"选项。

## 命名现状与接管（2026-09 实测）

`carton-bin` 已被第三方占用（Submitter `Yukari0201`、当前 Maintainer `jacobmathiesen`、
Last Packager `Antiz`，0 votes，停在 0.5.0），包的 Upstream URL 指向本项目；`carton-appimage`
也被 `Yukari0201` 占用。**换个名字重发不合规**：AUR 提交规则要求"预编译产物必须用 `-bin`
后缀"且"不要创建重复包"，判重看的是同一个上游软件，不是包名。

按 `AUR submission guidelines` 的原文（"if it is unmaintained or the maintainer is
unresponsive, the package can be adopted and updated as required"）走接管：

1. **标记 out-of-date**（任何登录用户都能做，会通知维护者）：上游已有更高版本，本包停在 0.5.0；
2. **在包页面留言**（公开、给维护者）：上游新发布的 `-package.tar.gz` 才是给包管理器用的载荷，
   当前用 `-portable.tar.gz` 会让应用自更新去覆盖 `/usr`；请其更新或转交；
3. **等 ≥2 周无回应** → 提 **orphan request**（包页面 → Submit Request → Orphan），
   写清何时留言、何时标记（这是流程要求的"已尝试联系"证据）；
4. 被 orphan 后 **Adopt package**，再按下面"维护侧"第 1 步推送；
5. PKGBUILD 头部把 `# Maintainer:` 换为自己，**前任维护者列到 `# Contributors:`**
   （wiki 硬要求，已在本目录 PKGBUILD 里备好 Contributors 行）。

本目录的 PKGBUILD 可直接用于接管后的推送，只差替换 `Maintainer` 占位符。

> **受阻记录（2026-09-15 实测）**：AUR 注册已全站临时关闭（`HTTP 503 New account
> registration is temporarily closed`），官方明确说**没有人工排队**、**不要脚本轮询注册页**。
> 根因是 2026-06-12 的官方新闻 *Active AUR malicious packages incident*：公告里列明受限的操作
> 包括 **创建新账号**、**推送更新**、**adopt / 创建新包**。所以事件平息前，接管（P1）与发源码包
> （P2）都无法开始，与本项目无关。
>
> 观察渠道（官方指定）：`https://archlinux.org/feeds/news/` 与 aur-general 邮件列表；
> 不要轮询注册页。
>
> 有利因素：本次事件正是"恶意 adopt / 恶意更新"，而本项目是代理客户端、包的 Upstream URL
> 就指向上游仓库 —— 由上游作者接管在安全角度上是最容易被接受的诉求。

## 用户侧（包发布之后）

```bash
yay -S carton-bin     # 或 paru -S carton-bin
```

之后随 `pacman -Syu` 升级，**应用内不会自更新**（见下面 package() 里的两个删除）。

## 维护侧：每次发版要做什么

1. 改 `pkgver`（不带 `v`，与 Release tag 对应）、必要时 `pkgrel`；
2. `updpkgsums` 填好 `sha256sums_*`（现在是 `SKIP`，AUR 不接受）；
3. `makepkg --printsrcinfo > .SRCINFO`；
4. 推送到 AUR：

```bash
git clone ssh://aur@aur.archlinux.org/carton-bin.git
cp packaging/aur/carton-bin/PKGBUILD carton-bin/
cd carton-bin && updpkgsums && makepkg --printsrcinfo > .SRCINFO
git add PKGBUILD .SRCINFO && git commit -m "upgpkg: carton-bin <ver>-1" && git push
```

首次提交前需要一个 AUR 账号并把 SSH 公钥加到账号里。

## 注意

- `source` 里的 `carton.desktop` / `carton.png` 用 `raw.githubusercontent.com/<tag>/...` 取，
  所以**发版的 tag 必须包含 `packaging/linux/carton.desktop`**（本次改动一起进仓库即可）。
- 本地验证：`makepkg -si`，然后确认
  `pacman -Ql carton-bin | grep -E 'carton-helper|carton_portable_data'` **没有输出**
  ——有输出说明自更新文件又混进去了；再确认 `cat /usr/lib/carton/.carton_package` 输出为
  `aur`（应用靠这个盖章才知道自己是 AUR 装的），并打开设置页确认**没有**"数据保存到
  程序目录"这个选项。
