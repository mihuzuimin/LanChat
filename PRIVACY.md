# LanChat 隐私政策（Privacy Policy）

**生效日期：2026 年 11 月 12 日**

LanChat 是一款无需服务器的局域网聊天应用。我们（开发者）设计上**不收集、不存储、不上传任何个人信息**。本政策说明应用运行时涉及的数据处理方式。

## 1. 我们不收集的数据

LanChat 没有服务器端账号系统，开发者无法接触任何用户数据。应用**不会**收集：

- 姓名、邮箱、手机号等身份信息
- 位置、设备标识、通讯录
- 使用统计、崩溃日志或遥测数据
- 聊天记录、文件内容（开发者无法访问）

## 2. 数据如何传输（局域网点对点）

| 功能 | 数据 | 传输范围 |
|---|---|---|
| 文字聊天、文件/图片/文件夹传输 | 你主动发送的内容 | 仅在你所在的局域网内，设备之间点对点传输，不经过任何第三方服务器 |
| 房间发现 | 房间名称、端口 | 仅局域网 UDP 广播 |
| 剪贴板同步 | 你开启开关后本机剪贴板内容 | 仅在你已加入的房间内、设备之间点对点传输，开关关闭时不留存不发送 |
| 快捷键转发 | 按键事件 | 仅在你开启远程协助的会话内点对点传输 |

## 3. 远程协助（RustDesk）

远程协助功能由开源组件 [RustDesk](https://github.com/rustdesk/rustdesk) 实现：

- 连接**优先走局域网 IP 直连**，不经过第三方中继服务器
- 仅当直连不通且你主动选择 ID 连接时，才会使用 RustDesk 官方中继（该中继的数据政策由 RustDesk 制定，详见 [rustdesk.com/privacy](https://rustdesk.com/privacy)）
- 快捷键与剪贴板转发仅在你主动发起或接受的远程协助会话期间生效

## 4. 软件更新

应用启动时会静默检查更新，此过程**只发送版本号查询，不发送任何用户数据**：

- 首选访问 GitHub Releases API（api.github.com）
- 3 秒无响应时改为在局域网内广播搜索新版本

更新不会自动安装，需你确认后执行。

## 5. 第三方组件

本应用内嵌的第三方开源组件（RustDesk、SharpHook 等）清单及许可证见仓库中的 [THIRD-PARTY-NOTICES](https://github.com/mihuzuimin/LanChat/blob/main/THIRD-PARTY-NOTICES)。这些组件在本应用中的行为已纳入本政策约束。

## 6. 儿童隐私

应用不以 13 岁以下儿童为目标用户，且因不收集任何数据，亦不涉及儿童个人信息处理。

## 7. 本政策的变更

如政策发生变更，将通过 GitHub 仓库（[github.com/mihuzuimin/LanChat](https://github.com/mihuzuimin/LanChat)）的提交记录与发布说明公示，更新后的生效日期见文首。

## 8. 联系方式

对本隐私政策有任何疑问，请通过 GitHub Issues 联系我们：
[github.com/mihuzuimin/LanChat/issues](https://github.com/mihuzuimin/LanChat/issues)
