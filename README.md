# LanChat · 局域网聊天室

一个**无需服务器**的局域网聊天工具：在同一 Wi-Fi / 局域网内建房、搜房、传文件、发起远程协助，
单个自包含 exe 双击即用，目标机器无需安装 .NET 运行时。

> 界面采用 Miuix 风格（胶囊圆角、柔和阴影、橙色主色），内嵌 MiSans 字体，开箱即正确排版。

---

## 功能特性

### 房间与消息
- **创建 / 加入房间**：设房间名与密码；支持私密房间手动加入
- **局域网自动发现**：公开房间可被一键搜索到（UDP 广播）
- **文字聊天**：`Ctrl + Enter` 发送，支持消息右键**引用**、表情面板、系统提示
- **成员管理**：房主可添加/取消**管理员**、禁言/解禁、封禁与踢人，内置封禁列表查看
- 连接状态、禁言状态实时同步，断开有明确提示

### 文件传输
- **发送文件 / 图片**：512KB 流式分块传输，带实时进度条，接收端可一键下载
- **发送文件夹**：自动打包 zip 传输，接收端解压**还原原目录结构**
- 单文件上限 200MB，SHA256 校验完整性

### 远程协助（RustDesk）
- **远程协助功能由 [RustDesk](https://github.com/rustdesk/rustdesk) 实现**：画面传输、解码、连接均由其完成，LanChat 只负责发起连接与快捷键/剪贴板转发
- 成员列表选人一键发起 RustDesk 远程连接（内嵌 `Tools\rustdesk.exe`，未修改的独立进程）
- **快捷键转发**（基于开源 [SharpHook](https://github.com/TolikPylypchuk/SharpHook) 全局键盘钩子）：
  - 观看端按下的带修饰键组合（`Ctrl/Alt/Shift/Win + 按键`）实时注入被控端，本机不响应
  - 单键与单独修饰键不受影响，本机照常使用
  - **按住 `Ctrl + Alt` 半秒**即可释放本机热键、关闭转发，随时拿回键盘
- **跨设备剪贴板**：开启同步后，复制的文字点对点同步给远程协助对象，对端直接粘贴（仅文字，需双方开启）

### 其他
- **局域网自动更新**：启动后静默检查新版本（UDP 发现 + HTTP 下载 + SHA256 校验）
- 主题即时切换、置顶、日志窗口、使用指南内置于应用内

---

## 快速开始

### 普通用户
1. 下载发布包 `LanChat.exe`（自包含单文件，Windows 10/11 x64）
2. 双击运行，设置用户名 → **创建房间**或**搜索/加入**已有房间
3. 同事/朋友的电脑运行同一个 exe 即可互通

### 开发者构建

```bash
git clone https://github.com/mihuzuimin/LanChat.git
cd LanChat
dotnet build -c Release
```

发布自包含单文件（与仓库约定一致：压缩、仅中文资源、无调试符号）：

```bash
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:Compress=true -p:SatelliteResourceLanguages=zh-Hans `
  -p:DebugType=None -p:DebugSymbols=false -o .\publish
```

更详细的说明见 [构建教程.txt](构建教程.txt)。

---

## 已知限制

- 仅限**同一局域网**内使用，不支持跨网段 / 公网
- `Ctrl + Alt + Del`、`Win + L` 属 Windows 系统安全序列（SAS），任何用户态程序均无法拦截或转发
- 剪贴板同步仅支持**文本**（单次上限 10 万字符），不含图片与文件
- 远程协助依赖 RustDesk 的确认机制，需被控端在 RustDesk 窗口内同意

---

## 技术栈

| 部分 | 方案 |
|---|---|
| UI | WPF · .NET 8（单窗口架构，视图切换） |
| 传输 | 手写 WebSocket（RFC 6455）+ JSON 文本帧协议，TCP 长连接 |
| 发现 / 更新 | UDP 广播（房间发现 47820 / 更新搜索 9178）+ HTTP 更新服务 9178 + GitHub Releases |
| 全局键盘 | SharpHook（libuiohook 封装）钩子捕获 + 事件模拟注入 |
| 远程控制 | RustDesk（AGPL-3.0，独立进程聚合分发） |

---

## 通信协议与端口

LanChat 共使用 **6 条通信通道**，各司其职：

| 通道 | 协议 / 端口 | 用于什么服务 |
|---|---|---|
| **房间主连接** | TCP + **WebSocket（RFC 6455）**，房间创建时指定端口（例 `47821`），UTF-8 JSON 文本帧 | 聊天室的**全部业务**：加入验证、聊天消息、成员与权限、附件传输、远程协助信令、快捷键与剪贴板转发 |
| **房间发现** | **UDP 47820** 广播 | 局域网自动搜房：`discover` 请求 → 公开房间回 `room` 应答（房间名 / 主机地址 / 端口 / 是否需密码） |
| **更新搜索** | **UDP 9178** 广播 | 局域网内找新版本：`lanchat-update-query` → 在线实例回 `lanchat-update-offer`（版本号），最多搜索 3 轮、每轮 3 秒 |
| **更新服务** | **HTTP 9178** | 本机作为更新源对外提供：`/api/version` 版本清单（版本号、更新说明、SHA256、是否强制更新、包大小）；`/api/download` 更新包下载 |
| **GitHub Releases** | **HTTPS** `api.github.com` | **首选更新源**：3 秒内比对 Release tag 版本号，发现新版自动从仓库下载 `.exe`；超时无响应才回退局域网 |
| **远程桌面** | RustDesk 私有协议（画面/音频/文件流，本程序不实现） | 远程协助的实际传输：**IP 直连**（TCP `21118`，direct IP access）优先；直连端口探测不通时自动改用 **RustDesk ID** 走官方中继。LanChat 只负责选路信令与按键/剪贴板转发 |

### 房间 WebSocket 协议的包类型（`Type` 字段路由）

| 类别 | 包类型 | 说明 |
|---|---|---|
| 加入与会话 | `validate` · `join` · `joined` · `error` | 先 `validate` 探测房间名与密码，再正式 `join`；`error` 携带拒绝原因 |
| 连接状态 | `system` · `host-closed` · `removed` | 系统提示、房主关闭房间、被踢出 |
| 成员同步 | `member-joined` · `member-left` · `role-changed` · `mute-state` | 成员进出、管理员任免、禁言状态实时同步 |
| 聊天与附件 | `text` · `file` · `image` · `folder` · `attachment-chunk` | 文字（含引用与表情）；附件按 512KB 分块流式传输 + SHA256 校验；`folder` 为 zip 打包传输、接收端还原目录 |
| 管理指令 | `command` | `/mute`、`/unmute` 等管理指令，服务端校验执行者必须是房主或管理员 |
| 远程协助信令 | `rustdesk-start` · `rustdesk-id` · `remote-key` · `clipboard` | 均为 **Target 定向包**（点对点路由）：发起协助请求、回传 RustDesk ID 供选路、快捷键按键转发（`Key` + `InputType`）、跨设备剪贴板同步 |

> **定向路由**：上述定向包在访客与房主之间经房间主连接转发（服务端按 `Target` 字段投递），
> 因此两端即使都位于 NAT 之后也能互通——这与聊天消息的广播语义不同。
>
> **版本兼容**：协议已由早期的「行分隔 JSON」迁移为 WebSocket。**新旧版本不能互通**——
> 用新版加入旧版开的房间会在升级握手阶段失败（提示"WebSocket 升级失败"），两端请使用同一版本。

---

## 引用的开源仓库

本项目为独立开发的 WPF 应用，**未复制其他聊天室/局域网聊天项目的源码**。以下为实际依赖与引用的原始开源仓库及资源：

| 原始开源仓库 / 来源 | 用途 | 许可证 | 引入方式 |
|---|---|---|---|
| [github.com/rustdesk/rustdesk](https://github.com/rustdesk/rustdesk) | 远程桌面控制（远程协助功能） | **AGPL-3.0** | 未修改的官方 `rustdesk.exe` 放在 `Tools\`，构建时内嵌、运行时释放为**独立进程**启动；属聚合分发，不改变本体 MIT 性质 |
| [github.com/TolikPylypchuk/SharpHook](https://github.com/TolikPylypchuk/SharpHook) | 全局键盘钩子捕获 + 按键注入（快捷键转发、Ctrl+Alt 释放热键） | MIT | NuGet 包引用 `SharpHook 8.0.1` |
| [github.com/kwhat/libuiohook](https://github.com/kwhat/libuiohook) | SharpHook 捆绑的原生键盘钩子 DLL（`uiohook.dll`） | **LGPL-3.0-or-later** | 随 SharpHook 分发，动态链接、未修改 |
| [github.com/dotnet/runtime](https://github.com/dotnet/runtime) | .NET 8 运行时与 WPF 框架（自包含发布内嵌） | MIT | `-r win-x64 --self-contained` 随包发布 |
| [MiSans 字体官方页](https://hyperos.mi.com/font/)（小米） | 界面全局字体 | 小米 MiSans 免费商用许可 | `Fonts\` 目录内嵌 OTF 资源 |

> 协议栈零第三方依赖，均为本项目自行实现：WebSocket 握手与 RFC 6455 帧编解码、UDP 发现协议为**纯手写**（`WebSocketChannel.cs`）；zip 打包/还原基于 .NET 内置 `System.IO.Compression`；网络层仅使用 `System.Net.Sockets` 框架库。
> 界面为 Miuix 风格的手写 XAML，仅风格参考，未引用其代码库。

---

## 协作者声明

本项目由以下协作者共同完成，按贡献阶段与分工列示：

| 协作者 | 贡献内容 |
|---|---|
| [LKQGames](https://github.com/LKQGames) | **框架与奠基**：初步框架与界面搭建、首批功能开发，并提供整体开发方向及各功能的实现思路 |
| [mihuzuimin](https://github.com/mihuzuimin) | **迭代与维护**：后续功能的添加与移除、Bug 修复、UI 优化与体验打磨 |

项目的后续演进以两位协作者的共同意愿为准；对具体代码的作者权归属，以 Git 提交记录为准。

---

## 开源协议

### 本体代码：MIT License

全文见 [LICENSE](LICENSE)，版权 `Copyright (c) 2026 mihuzuimin, LKQGames`。

- **你可以**：商用、修改、私用、再分发（含闭源集成），无需事先申请授权
- **你的义务**：在副本中保留 LICENSE 文件与版权声明
- **免责声明**：本软件不提供任何明示或暗示担保，使用风险自负

### 内含第三方组件协议一览

| 组件 | 协议 | 对本体的传染性 | 分发时的义务 |
|---|---|---|---|
| [RustDesk](https://github.com/rustdesk/rustdesk) | **AGPL-3.0** | 无（独立进程聚合，见下） | 随包附 AGPL 全文 + 标注源码地址；**修改** RustDesk 则修改版须 AGPL 开源 |
| [SharpHook](https://github.com/TolikPylypchuk/SharpHook) | MIT | 无 | 保留版权与许可声明（已收录 THIRD-PARTY-NOTICES） |
| [libuiohook](https://github.com/kwhat/libuiohook)（SharpHook 原生 DLL） | **LGPL-3.0-or-later** | 无（动态链接、可替换，见下） | 随包附 LGPL 全文 + 标注源码地址；**修改**该 DLL 则修改版须 LGPL 开源 |
| [.NET Runtime](https://github.com/dotnet/runtime) | MIT | 无 | 保留 .NET 声明（已收录 THIRD-PARTY-NOTICES） |
| [MiSans 字体](https://hyperos.mi.com/font/) | 小米 MiSans 免费商用许可 | 无 | 遵守小米官方协议，不得单独提取字体出售 |

### 两个容易误会的点

**① RustDesk 是 AGPL-3.0，为什么 LanChat 本体还能是 MIT？**
`rustdesk.exe` 是**未修改的官方原始文件**，以独立进程方式启动，与 LanChat 构成
“聚合（mere aggregation）”而非衍生作品——AGPL 不跨进程传染。分发义务（附许可证
全文 + 源码地址）已由随包的 `Tools\LICENSE-rustdesk.txt` 履行；只有当你**修改并
分发** RustDesk 本体时，才需要以 AGPL 公开对应源码。

**② SharpHook 是 MIT，为什么包里还有个 LGPL？**
SharpHook 的托管层是 MIT，但它捆绑的原生钩子库 **libuiohook 是 LGPL-3.0-or-later**。
LanChat 通过 SharpHook **动态链接**加载 `uiohook.dll` 且未作任何修改——LGPL 正是
为这种使用场景设计：本程序代码保持 MIT，只需随包提供 LGPL 许可证文本与源码地址，
且使用者可自行替换该 DLL。

### 分发 LanChat.exe 的合规清单

以下许可证文件已配置为随发布包生成（csproj `Content`），转发时请保持完整：

- `LICENSE` —— 本体 MIT 许可证
- `THIRD-PARTY-NOTICES` —— 全部第三方组件声明（本节的详细依据）
- `Tools\LICENSE-rustdesk.txt` —— AGPL-3.0 全文
- `Tools\LICENSE-libuiohook.txt` —— LGPL-3.0 全文
