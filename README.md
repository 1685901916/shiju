# 拾句 · Windows 划词英语助手

Windows 10/11 x64 便携应用。参考 PiliPlus 学习版的卡片布局，提供全局划词、英文翻译、DeepSeek 流式多轮追问、Windows 系统朗读和本地学习卡片。

## 启动

从 [GitHub Releases](https://github.com/1685901916/shiju/releases/latest) 下载 Windows x64 便携压缩包，解压后运行 `SelectionAssistant.exe`。

双击 `SelectionAssistant.exe`（开发目录内位于 `artifacts/win-x64`）。自包含发布，不需要另装 .NET 或管理员权限。首次打开“模型与偏好”，填写接口、模型和 API Key 后保存；保存不会发送模型请求。也可以点击“体验离线示例”先验证交互，演示使用明确标记的预置文本，不联网。

## 使用

1. 在其他应用中选中文字，旁边出现“译为英文”按钮；点击后自动翻译。
2. 在浮窗里复制或朗读译文，用快捷提问按钮或底部输入框向 DeepSeek 追问。Enter 发送，Shift+Enter 换行。
3. 点击“收藏卡片”，保存原文、译文、来源窗口标题和已完成的问答；在“我的学习卡片”中搜索、重新打开、继续问和删除。继续聊天后点击“更新卡片”保存新问答。
4. “固定”保持浮窗；未固定时切换到其他窗口或按 Esc 收起。右上角关闭主窗口后应用留在系统托盘，托盘菜单可退出。
5. 全局 `Alt+Shift+T` 主动读取选区；`Alt+Shift+V` 显式读取并翻译剪贴板。也能直接在主窗口输入或粘贴。

## 模型配置

- DeepSeek 官方接口：`https://api.deepseek.com/chat/completions`，默认模型 `deepseek-flash`（可修改为账号可用的模型）。
- 兼容服务可填写完整 Chat Completions URL，或基础地址（如 `https://example.com/v1`），程序自动补 `/chat/completions`。
- API Key 使用当前 Windows 用户的 DPAPI 加密存储，不写入源码、普通日志或收藏。
- 默认只有点击按钮、快捷键、发送问题时才发起请求。“选中后直接翻译”可在偏好中开启，开启后会调用你配置的服务。
- 仅选中文字和该句的问答上下文会发送给配置的模型服务；不上传整篇文档，来源窗口标题只存在本地收藏中。
- 翻译或回答中可点击“停止”；切换到新原文会取消旧请求，避免结果混入新卡片。

## 兼容性与数据

取词依赖目标应用提供 UI Automation TextPattern。Chrome、Edge、Word 等应用的不同版本、网页结构和权限可能影响选区可读性，需要在具体环境验证。图片、扫描 PDF、视频画面不支持 OCR；密码框不读取；普通权限应用不能读取某些管理员权限窗口。取不到时使用复制 + `Alt+Shift+V`。程序不会自动模拟 Ctrl+C 或修改剪贴板。

多屏浮窗按显示器工作区和 DPI 放置。选区上限 12,000 字符，单个问题上限 4,000 字符。模型服务需自行配置且可能计费。程序没有自动加载本机其他应用的凭据。

数据目录：`%LOCALAPPDATA%\SelectionAssistant`。`settings.json` 保存接口、模型与加密密钥；`cards.json` 保存收藏与对话（普通本地 JSON，便于备份）。损坏收藏文件不会被静默覆盖。卸载只需退出程序并移走便携文件；需要保留收藏时备份数据目录。

## 构建和验证

需要 .NET 8 SDK（项目 `.dotnet` 目录存在时优先使用该 SDK）。

```powershell
./tools/build.ps1
```

测试套件覆盖端点校验、提示词边界、连续对话、SSE 中文流式解析、非流式兼容、HTTP 错误、断流/截断/空结果、取消与收藏持久化。

可使用 `--data-dir <绝对路径>` 隔离测试数据；`--demo` 启动离线示例；`--fixture` 打开跨进程取词测试文档；`--self-test --report <路径>` 验证 Windows 密钥加密、切换选句时的请求取消、停止追问后的问题恢复和完整对话，不联网。加 `--fixture-probe` 可校验另一个 `SelectionFixture.exe` 测试进程中的选区文字与坐标；`--diagnostics` 只记录取词状态、进程编号和字符数，不记录选中文字或密钥。

参考：[UI Automation GetSelection](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.textpattern.getselection)、[DeepSeek Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/)。
