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
| 传输 | 手写 WebSocket（RFC 6455）+ 行/帧 JSON 协议，TCP 长连接 |
| 发现 / 更新 | UDP 广播（发现）+ HTTP version.json（更新） |
| 全局键盘 | SharpHook（libuiohook 封装）钩子捕获 + 事件模拟注入 |
| 远程控制 | RustDesk（AGPL-3.0，独立进程聚合分发） |

---

## 许可证

本体代码采用 [MIT License](LICENSE)，版权 `Copyright (c) 2026 mihuzuimin`。

第三方组件各自遵循原许可证，完整声明见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES)：

- **RustDesk** — AGPL-3.0（未修改的独立进程随包分发，不改变本体 MIT 性质）
- **SharpHook** — MIT
- **MiSans 字体** — 小米 MiSans 免费商用许可
- **.NET 运行时** — MIT
