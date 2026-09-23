# OMRINA 品牌与资源约定

## 项目定位

OMRINA 的正式展开是 **Optical Mark Recognition Integration Agent**。它计划成为运行在本机的 Integration Agent，连接浏览器或 Web 应用与本地扫描设备。当前交付包括 Windows WinUI 3 / Windows App SDK target、Uno Skia Desktop target、平台无关的模板与扫描契约，以及只读的本地健康检查；浏览器连接、任务、本地授权、状态事件和 WebSocket 能力仍在规划中。

OMRINA 不是面向终端用户的完整答题应用。模板、识别、评分和复核是由本地引擎逐步提供的能力，桌面 UI 主要用于配置、状态查看和需要用户确认的操作。

当前桌面范围为 Windows、macOS 和 Linux；不把 Android 或 iOS 纳入本项目目标。Windows 仍是主要开发和实机测试平台。项目已引入 Uno.Sdk 6.7.30，并为 `Omrina.Desktop` 提供 Windows 与 `net10.0-desktop` 双 target：Windows target 使用 Windows App SDK / WinUI 3，macOS 与 Linux 采用 Uno 的 Skia Desktop target。macOS/Linux 系统打印尚未适配，macOS/Linux 实机尚未测试，三平台测试与 CI 暂缓。

## 名称与大小写

| 场景 | 规范写法 | 例子 |
| --- | --- | --- |
| 品牌展示 | `OMRINA` | README 标题、About 页面标题、网站标题、启动画面 |
| 代码与程序集 | `Omrina` | `Omrina.Core`、`Omrina.Protocol`、`Omrina.Scanning` |
| 可执行文件 | `Omrina` | `Omrina.exe` |
| 仓库 | [`LSCube7/omrina`](https://github.com/LSCube7/omrina) | Git 仓库名称 |

`OMRINA` 保留 OMR 与光学标记识别的直接关联；`Omrina.*` 用于新的命名空间、项目、程序集和公共代码 API。迁移期间，旧的 `AnswerSheet` 目录、模板类型名、模板哈希前缀和本地存储目录可以作为 canonical 或兼容格式保留；它们不改变新的品牌展示名和项目层命名约定。

## 架构层次

核心代码按职责分层，平台相关实现不能渗入平台无关层：

| 层 | 职责 | 平台约束 |
| --- | --- | --- |
| `Omrina.Core` | 当前提供模板数据模型、A4 几何和 SVG 输出；扫描任务、状态和配置仍属规划范围 | 纯 .NET；不得依赖 `Microsoft.UI.*`、`Windows.*` 或 Win32 |
| `Omrina.Protocol` | 当前只定义 M0 健康检查的协议常量和响应模型；完整的 HTTP、WebSocket、授权、设备和任务协议仍属规划范围 | 保持跨平台 |
| `Omrina.Scanning` | `IScannerService` 等扫描抽象与结果模型 | 不直接暴露 WIA、TWAIN、SANE 或 ImageCaptureCore |
| `Omrina.Server` | 当前提供仅监听回环地址的健康检查 HTTP 服务和来源校验；事件与任务调度仍属规划范围 | 保持跨平台；不读取任意文件路径或执行任意命令 |
| `Omrina.Platform` | 已引入图像输入与解码等平台边界接口；具体文件选择、扫描和打印能力由桌面 target 隔离实现 | 通过接口隔离具体系统 API；macOS/Linux 系统打印尚未适配 |
| `Omrina.Desktop` | Uno 单窗口桌面入口，包含 Windows 与 `net10.0-desktop` 双 target | Windows 使用 WinUI 3；macOS/Linux 实机与设备能力尚未验收 |

平台判断应集中在 Platform 层或明确的实现边界内。业务层不应到处散落 `OperatingSystem.IsWindows()`，也不应让 `Windows.*` 或 Win32 类型穿过 Core、Protocol、Scanning 抽象和 Server 边界。

## 视觉与 UI 约定

OMRINA 的标志采用扁平、克制的日系科技方向：破损的识别环、中央识别标记和像素块，表达光学识别、填涂标记以及数字化传输。主色方向为深石板色 `#2B3A4A` 与青绿色 `#4DD0C5`，允许使用柔和的青绿色渐变。

品牌色只用于品牌图形和品牌资源。当前 Uno 桌面 UI 使用系统主题和系统色，不为品牌宣传而重设按钮、输入框、InfoBar 等系统控件的颜色。

UI 文案保持简洁、冷静、系统化，描述状态和下一步操作，例如：

```text
OMRINA is running
Scanner connected
Waiting for requests
Connected to browser
No compatible scanner detected
Scan job started
Scan completed
```

避免营销式欢迎语和过度拟人化表达。App Icon 与 Tray Icon 使用同一套品牌图形；托盘状态通过系统状态样式和文字表达，不依赖品牌色作为唯一信息。

## 未来品牌资源入口

当前只约定资源入口，不在本阶段新增实际图片或把设计稿提交到仓库。未来资源统一从 `assets/brand/` 管理，平台项目只引用这些源资源并按目标平台生成所需格式：

| 资源用途 | 未来入口 | 约定 |
| --- | --- | --- |
| 主标志 | `assets/brand/omrina-mark.svg` | 唯一矢量源；用于 README、About 和需要完整标志的页面 |
| App Icon | `assets/brand/app-icon/` | 从主标志派生各平台尺寸；保持清晰轮廓和透明背景 |
| Tray Icon | `assets/brand/tray/` | 与 App Icon 使用同一标记；提供适合系统托盘的单色或系统色版本 |
| About 页面 | `assets/brand/about/` | 可包含主标志、版本信息和项目定位；不放入用户扫描图或真实样本 |

资源文件名、尺寸和平台格式以后由对应的 Uno / 打包实现补充。添加资源时应同时说明用途、许可和生成来源；不要在文档中引用本机路径，也不要把真实扫描样本作为品牌资源上传。

## 当前桌面 Logo 入口

当前桌面只预留 Logo 入口，不接入实际品牌图形：

- `apps/desktop/App.xaml` 的 `OmrinaBrandMarkTemplate` ResourceDictionary 键当前内容为空 `<Grid />`，供未来品牌资源替换。
- `apps/desktop/MainWindow.xaml` 标题旁的 `BrandMarkSlot` `ContentControl` 默认 `Collapsed`，因此当前窗口不会显示空白 Logo 占位。
- 当前没有将 `design.png` 作为 Logo 资源读取、绑定或提交，也没有使用品牌色覆盖 WinUI 系统控件。
- Logo 入口调整后的 Windows x64 no-restore build 为 0 个警告、0 个错误；本次没有新增 UI 视觉或交互验收结论。

## 当前交付边界

Uno.Sdk 6.7.30 已引入，Windows 与 `net10.0-desktop` 双 target 以及 `Omrina.Platform` 平台边界接口已落地。Windows 实机扫描和本地流程优先；macOS/Linux 系统打印尚未适配，macOS/Linux 实际设备与 UI 尚未测试。三平台 CI 当前暂缓，后续恢复时应至少覆盖 restore、build、unit tests、publish 和基础 smoke tests，再单独安排真实 macOS/Linux 设备验收。

M1 已完成生成模板的打印、填涂并通过本地应用扫描的实物流程，目视确认页面四角定位与方向标记完整；纸面尺寸测量仍未完成。M2 后续加入本地识别、评分和人工复核，并用这一张扫描图做单样本识别检查；单张样本不能代表总体识别准确率或跨设备验收。
