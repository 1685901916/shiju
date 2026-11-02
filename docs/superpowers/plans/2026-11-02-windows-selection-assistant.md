# Windows 划词学习助手实施计划

**Goal:** 交付可以在 Windows x64 上直接运行的划词翻译、DeepSeek 多轮对话和学习卡片应用。

**Architecture:** C# / WPF 原生界面，独立 Core 封装消息协议、SSE 解析、收藏与设置模型；Windows 层负责 UI Automation 选区、无激活小按钮、快捷键、系统托盘和 DPAPI 密钥。UI 与请求使用取消令牌隔离，切换选区时取消旧请求。

**Tech Stack:** .NET 8、WPF、Win32、UI Automation、HttpClient、System.Text.Json；无第三方运行依赖。

## 文件划分与步骤

- [x] Core/Models.cs：接口配置、对话与卡片模型，HTTPS 校验及本机 HTTP 测试例外；先写配置校验、选区作为数据隔离和对话上下文测试。
- [x] Core/ChatClient.cs：标准 Chat Completions 流式请求，逐事件解析 SSE，处理取消、空结果、截断、认证和限流；先用自建 HttpMessageHandler 编写并运行失败测试，再实现。
- [x] Core/LibraryStore.cs：原子文件替换、本地卡片增删查、损坏文件不覆盖；使用临时目录先写保存/重开/删除/损坏保护测试。
- [x] Windows/Native.cs、SelectionMonitor.cs：全局鼠标松开和低频选区检测后在独立线程读选区；跳过本程序、密码框，限长并缓存文本与来源；全局 Alt+Shift+T 取词，Alt+Shift+V 显式剪贴板入口。
- [x] Windows/SettingsStore.cs：DPAPI 加密 API Key，普通设置不含明文密钥，配置不发请求。
- [x] App.xaml、MainWindow、TranslateView、SelectionPopup、SettingsWindow：浅色圆角卡片、双语结果、连续追问、停止/重试、复制/朗读、收藏管理与托盘。空配置给出配置入口；演示明确标注且不联网。
- [x] Tests/Program.cs：运行协议与存储回归；本机流式测试服务器验证实际 HTTP；UI 用 agent-desktop 语义操作检查配置、演示翻译、追问和收藏。
- [x] tools/build.ps1、README.md：发布自包含 win-x64 便携版，附启动入口、数据位置、取词兼容性与使用说明。

## 验收

1. Release 编译通过且无警告；Core 自动测试通过。
2. 自包含 EXE 可以启动，展示真实可操作的窗口。
3. 不调用付费模型 API，使用明确标记的离线演示或本机测试服务验证翻译与追问。
4. 使用 agent-desktop 验证卡片、收藏增删、配置保存以及可测的跨进程选区获取。
5. 发布文件保留用户已有数据；交付可点击 EXE、源码、使用说明，明确尚未实测的应用兼容性。

