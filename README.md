# MuSync

> 把「音乐软件正在播什么」同步到你的 Steam 状态 —— **Mu**sic **Sync**（音乐软件状态同步 Steam）。

登录 Steam 后，好友看到你的状态是「正在玩」，内容是当前播放的歌曲、歌手与实时进度：
`稻香 - 周杰伦 [#####-----] 2:30/4:15`

## ✨ 特性

- 🎵 **多播放器**：网易云音乐 / QQ 音乐 / 汽水音乐
  - 多个播放器同时运行时自动仲裁：**正在播放的优先**，暂停让位给空闲签名，关闭其一自动切换到另一个
  - 汽水音乐通过 Windows 系统媒体会话（SMTC）读取，无需任何特殊设置
- 🎨 **Windows 11 Fluent 界面**：WPF + [WPF-UI](https://github.com/lepoco/wpfui)
  - Mica 云母背景，深浅色自动跟随系统
  - 左侧 NavigationView 四页导航：主界面 / 播放器 / 挂时长 / 设置
  - 主界面三张播放器卡片（封面、歌名、歌手、专辑、实时进度条）
  - 「挂时长」页读取 Steam 库存，勾选游戏即可累计游玩时长（最多 30 个，也适用集换式卡牌掉落），音乐推送不中断
  - 设置页为 Win11 设置风格的圆角卡片 + ToggleSwitch，改动实时预览
- 📡 **Steam 同步**：SteamKit2 直连，无需 Steam 客户端在线
  - 断线自动重连 + 令牌自动重新登录（指数退避）
  - 检测到你在玩真实 Steam 游戏时自动暂停音乐同步，退出游戏自动恢复
- ✏️ **空闲在线签名**：没有播放音乐或歌曲暂停时，可在 Steam 状态位显示自定义签名；恢复播放自动切回歌曲，玩真实游戏/手动暂停时自动隐藏
- 🪟 **托盘常驻**：开机自启、关闭最小化到托盘、启动隐藏到托盘；托盘悬停提示与右键菜单实时显示播放状态
- 🛡️ **隐私**：Steam 登录令牌以 Windows DPAPI 加密落盘（非明文）
- 📋 **日志**：关键事件写入 `%LocalAppData%\MuSync\logs\`（按天滚动），出问题可查
- 📊 播放器页内置状态监控：各播放器运行状态 / 错误码 / Steam 会话详情

## 📥 使用

1. 下载 [Releases](https://github.com/kernel12345/musync/releases) 中的单文件 `MuSync.exe`（CI 产物为 framework-dependent，需先安装 [.NET 9 桌面运行时](https://dotnet.microsoft.com/download/dotnet/9.0)）
2. 运行后首次启动会弹出 Steam 登录（支持手机令牌 / 邮箱验证码，登录后自动保存会话）
3. 打开音乐播放器即可自动同步

> 与 Steam 客户端同账号共存不会被顶下线（与 ArchiSteamFarm 同为 SteamKit 会话）；
> 首次登录后，Steam 账号管理里会出现名为 `MuSync` 的设备会话，属正常现象。

## 🔨 构建与测试

```powershell
dotnet build MuSync.sln -c Release     # 编译
dotnet test MuSync.sln                  # 运行单元测试（19 个）
```

发布自包含单文件（免安装运行时，可直接发给朋友）：

```powershell
dotnet publish MuSync.csproj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true
```

产物：`bin/Release/net9.0-windows10.0.19041.0/win-x64/publish/MuSync.exe`

GitHub Actions（`main` 分支 push/PR）会自动构建、测试并上传单文件产物；打 `v*` 标签时自动创建 Release。

## 🗂️ 项目结构

```
App.xaml / MainWindow.xaml     # WPF 启动与 Fluent 主窗口（Mica + NavigationView）
Views/Pages/                   # 导航页：Dashboard / Players / GameIdle / Settings
Themes/SettingsCardStyle.xaml  # Win11 风格设置卡片样式
Services/                      # AppServices / TrayIconService
RpcManager.cs                  # 多播放器轮询与活跃源仲裁
SteamStatusManager.cs          # SteamKit2 连接、登录与状态推送
SteamSessionManager.cs         # 会话/令牌（DPAPI 加密）持久化、games_played 统一发送
SteamLibraryResolver.cs        # LicenseList + PICS 库存游戏解析（本地缓存）
Players/                       # 网易云 / QQ 音乐（内存读取）、汽水音乐（SMTC）
Utils/ImageCacheManager.cs     # 封面缓存（IO 与解码走后台线程）
tests/MuSync.Tests/            # xUnit 单元测试
```

## 🔀 与 yySync 的关系

MuSync 基于 [wuyan1337/yySync](https://github.com/wuyan1337/yySync)（MIT）「半新写」：
播放器内存逆向实现沿用原项目成果，状态仲裁、断线重连、日志与安全体系均为本项目独立实现；
UI 层已从 WinForms 全面迁移到 WPF + WPF-UI Fluent 设计。
相对 yySync 的全部改动见 [CHANGELOG.md](CHANGELOG.md)。

## 🙏 致谢与许可

MuSync 是 [wuyan1337/yySync](https://github.com/wuyan1337/yySync) 的「半新写」重构版：
播放器内存逆向实现沿用原项目成果（MIT），代码结构、状态仲裁、重连与日志体系为本项目独立实现。
第三方许可与版权声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

本项目以 MIT 协议发布，见 [LICENSE](LICENSE)。
