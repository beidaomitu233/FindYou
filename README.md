# FindYou

[下载 Windows 版本](https://github.com/beidaomitu233/FindYou/releases/latest) · [源码仓库](https://github.com/beidaomitu233/FindYou)

FindYou 是面向 Windows 的双向文件互传工具。启动后自动发现设备，点击对方即可进入互传空间；双方都能直接拖入文件或文件夹，传输在后台继续完成。

## 使用流程

1. 双方启动 `FindYou.exe`。
2. 在雷达上点击对方设备，连接成功后自动进入互传空间。
3. 拖入文件或文件夹，或使用右侧两个发送按钮。接收内容自动保存到设置的接收文件夹。

发现、连接和互传是连续流程。连接存在时，顶部“互传”入口始终可从发现、记录或设置返回当前会话。关闭主窗口只隐藏到托盘；托盘“退出”会停止发现、传输服务和整个进程。

## 更新与移动

设置 → 软件更新 → 检查更新。新版本来自公开 GitHub Releases，点击下载后在应用内显示进度，并校验发布附件中的 SHA-256。下载完成后打开文件位置，待传输完成、从托盘退出，再替换原程序。更新失败可重新检查或下载。当前源码版本为 5.1.2，公开发布版仍为 5.1.0；应用不会自动安装或中断传输。

后续版本修改 `FindYou.Update.cs` 中的版本号和程序集版本，运行构建、验收和签名流程后创建 `v主.次.修订` Tag。GitHub Release 必须包含 `FindYou.exe` 和 `FindYou.exe.sha256`（仅 64 位十六进制哈希），并附带依赖许可文件；使用草稿上传齐全后再公开，不将预发布版本推送给客户端。

替换或移动 `FindYou.exe` 前，先从任务栏托盘退出正在运行的旧版本。新程序启动时会核对当前 EXE 路径对应的 Windows 防火墙规则；路径变化后会重新请求 TCP 与 UDP 入站放行。若旧位置的实例仍在后台，新程序会明确提示退出旧实例，不会再静默打开旧界面。已开启开机启动时，成功启动的新程序会把启动项更新为当前路径。

## 网络与传输

- 同一局域网：优先使用 HTTP/1.1 TCP 流式直传，文件不经过网页、JavaScript 或服务器。
- 无法局域网直连：使用 ICE/STUN 建立 WebRTC DataChannel，经 DTLS 加密后在两台设备之间传输。
- 可选发现服务器：交换设备在线状态、会话描述和 ICE 候选，不接收或保存文件内容。
- 严格 NAT、防火墙或禁用 UDP 的网络可能无法直连。若启用 TURN 或文件中继，兼容性会提高，但该部分流量会受中继服务器带宽限制。

HTTP 直传未加密，只应在可信局域网使用。跨网络 P2P 使用 DTLS 加密。完整实现、测速结果和跨网方案见 [传输原理与跨网方案.md](传输原理与跨网方案.md)。

## 运行与构建

发布使用根目录唯一的 `FindYou.exe`。默认数据目录为 `%LOCALAPPDATA%\FindYou`，默认接收目录为 `%USERPROFILE%\Downloads\FindYou`。

首次构建先恢复原生 P2P 依赖，再编译：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\restore-dependencies.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建目标为 x64、.NET Framework 4.8。WPF 界面和运行时依赖嵌入单个可执行文件，不需要 WebView2、Node.js 或浏览器进程。NuGet 仅用于源码构建。

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `FindYou.cs` | Windows 入口、托盘、配置、发现和 HTTP 服务 |
| `FindYou.Window.cs` / `FindYou.Native.xaml` | 原生 WPF 界面和雷达动效 |
| `FindYou.Native.cs` | 原生会话、发送队列和 HTTP 流式发送 |
| `FindYou.Speed.cs` | HTTP 传输中的近期速度采样 |
| `FindYou.Rtc.cs` | ICE、DTLS、SCTP DataChannel 直连 |
| `FindYou.Workspace.cs` / `FindYou.Direct.cs` | 接收批次、授权、落盘和异常清理 |
| `findyou-relay.js` / `relay.html` | 可选的跨网络发现与信令服务 |

SIPSorcery 10.0.16 的许可证包含额外地域使用限制，发布前需阅读随项目保留的完整许可证。其余嵌入依赖及许可见 `THIRD-PARTY-NOTICES.txt`。
