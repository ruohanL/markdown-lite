# MarkdownLite

Windows 平台的**纯只读** Markdown 阅读器。核心理念：只阅读，不编辑——没有编辑框、没有保存、没有另存为、没有导出，全程不联网。

技术栈：C# .NET 8 · WPF · WebView2 · Markdig · 离线 highlight.js

## 功能特性

| 类别 | 能力 |
| --- | --- |
| 打开方式 | 菜单"文件 → 打开"（`Ctrl+O`）、拖拽 .md 到窗口、命令行 `MarkdownLite.exe "C:\path\file.md"`、可选 .md 文件关联 |
| 渲染 | CommonMark + GFM：标题、段落、列表、表格、任务列表（禁用态复选框）、删除线、自动链接、脚注、引用、代码块、行内代码、图片、链接；YAML front matter 自动隐藏，其中的 `title` 用作窗口标题 |
| 代码高亮 | highlight.js 11.9（GitHub 亮/暗两套主题）以本地文件随应用分发，**断网可用，零 CDN 依赖** |
| 图片 | 相对路径以当前 .md 所在目录为基准解析（经本地虚拟主机映射，不复制、不写盘） |
| 链接 | 外部 http(s)/mailto 用系统默认浏览器打开；相对 .md 链接在应用内继续打开；页内锚点平滑滚动；外链与跨文档链接的锚点均完整保留 |
| 主题 | 浅色 / 深色 / 跟随系统，菜单弹出层同步暗色 |
| 缩放 | `Ctrl++`、`Ctrl+-`、`Ctrl+0`，另支持 Ctrl+滚轮；缩放级别自动记忆 |
| 刷新 | `F5` / `Ctrl+R` 重新读取并重渲染当前文件 |
| 搜索 | `Ctrl+F` 页内搜索：命中高亮、`当前 / 总数` 计数、`Enter` / `Shift+Enter` 上下跳转、`Esc` 关闭 |
| 自动重载 | 当前 .md 被外部修改时自动重新渲染（「文件」菜单可关闭），文件被占用时自动重试 |
| 会话记忆 | 窗口尺寸与位置、缩放级别、每个文件的阅读位置（滚动百分比）均自动记住 |
| 目录 | 侧边 TOC（`Ctrl+T` 或状态栏左下角按钮开关），点击定位；滚动时自动高亮当前章节 |
| 编码 | UTF-8（含 BOM）、UTF-16 LE/BE、GB18030 自动探测回退 |
| 界面 | 无边框分层配色窗口：标题栏（应用图标 + 文件名 + 最小化/最大化/关闭）+ 菜单栏 + 阅读区；状态栏显示文件路径、体积、编码、渲染耗时、缩放与字数（中日韩按字、西文按词） |

## 系统要求

- Windows 10 1809+ / Windows 11（需 Microsoft Edge WebView2 Runtime，Win11 及近期 Win10 已内置）
- 构建需要 .NET 8 SDK；运行自包含发布版则无需安装任何运行时

## 构建

```powershell
dotnet build MarkdownLite.sln -c Release
dotnet test  MarkdownLite.sln -c Release     # 单元测试
```

若本机未安装 .NET SDK，可先取便携版到仓库内（已 gitignore，删除无残留）：

```powershell
pwsh scripts/get-sdk.ps1                 # 之后把 dotnet 换成 .\.dotnet\dotnet.exe
```

## 运行

```powershell
# 开发运行
dotnet run --project src/MarkdownLite -- "samples\示例文档.md"

# 发布为免安装的单文件 exe（自包含，双击即用，约 69MB）
pwsh scripts/publish.ps1                 # 产物：publish\MarkdownLite.exe
```

### 发行版本（两种形态）

| 形态 | 文件 | 适合 |
| --- | --- | --- |
| 安装程序 | `MarkdownLite-Setup-<版本>.exe` | 日常使用：向导式安装到个人目录（免管理员），开始菜单快捷方式，可选 .md 文件关联，卸载干净（自动还原关联） |
| 便携版 | `MarkdownLite-<版本>-portable.exe` | 免安装场景：单个 exe 双击即用，放 U 盘/任意目录均可，不写注册表 |

本地一键产出两种发行资产：

```powershell
pwsh scripts/release.ps1                 # release\ 下：安装程序 + 便携版 + SHA256SUMS.txt
```

## .md 文件关联（可选，免管理员）

安装程序在安装时已提供可选的文件关联；使用便携版或想手工管理时，用下面的脚本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\associate-md.ps1              # 注册到「打开方式」
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\associate-md.ps1 -SetDefault  # 顺带设为默认程序
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\associate-md.ps1 -Unregister # 注销并还原
```

## 图标

`assets/app-icon.png` 是图标**源图**；三个产物（`src/MarkdownLite/app.ico` 应用图标、`src/MarkdownLite/md-file.ico` 文件类型图标、`src/MarkdownLite/app-icon.png` 界面内嵌位图）**已提交入库**，构建与 CI 不会重新生成它们。改动源图后需手动重跑并把产物一起提交：

```bash
python scripts/make-icon.py            # 由 assets/app-icon.png 重新生成三个产物
python scripts/make-icon.py --probe    # 只打印区域识别结果
```

注意 `assets/app-icon.png`（源图）与 `src/MarkdownLite/app-icon.png`（内嵌资源）是两份同内容文件，前者供脚本读取、后者供 XAML 引用，更新时由 `make-icon.py` 同步，不要只改其一。

## 项目结构

```
MarkdownLite.sln
src/MarkdownLite/
├── App.xaml(.cs)              入口、命令行参数、全局样式与主题资源、未处理异常兜底
├── MainWindow.xaml(.cs)       无边框窗口（WindowChrome 两行顶栏）、WebView2 初始化与
│                              全部导航/弹窗/下载拦截、打开渲染流程、TOC/缩放/字数、
│                              最大化边界修复
├── Dialogs/AppDialog.xaml(.cs) 应用内统一弹窗（替代系统 MessageBox）
├── Services/
│   ├── MarkdownRenderer.cs    Markdig 管线 + 原始 HTML 剥离 + URL 白名单改写 +
│   │                          TOC 提取 + 带 CSP 的文档模板（纯静态，可单测）
│   ├── PlainTextReader.cs     只读读取 + BOM/UTF-8/UTF-16/GB18030 编码探测
│   ├── TextStatistics.cs      中日韩按字、西文按词的字数统计
│   ├── DocumentWatcher.cs     外部修改自动重载（FileSystemWatcher + 防抖）
│   ├── ReadingProgress.cs     每文件阅读位置记忆
│   ├── AppTheme.cs            主题令牌写入 Application.Resources + DWM 圆角/Mica
│   ├── AppSettings.cs         设置持久化（仅应用数据目录）
│   ├── FileDropTarget.cs      原生 OLE IDropTarget，接管 WebView2 子窗口拖放
│   ├── ErrorLog.cs / DiagPerf.cs / OpenTiming.cs / LayoutDiagnostics.cs
│   │                          诊断基建（错误日志、分阶段计时、布局与帧率测量）
│   └── WindowRounding.cs      窗口圆角 interop
├── wwwroot/                   离线 Web 资源（viewer.css / reader.js / welcome.html /
│                              highlight.min.js + 亮暗两套 hljs 样式）
└── app.ico / md-file.ico / app-icon.png   图标产物（已入库，见「图标」）
tests/MarkdownLite.Tests/          xUnit：解析、XSS 过滤、相对路径、编码、自动重载、
                               字数统计、设置持久化（91 个用例）
samples/                       验收示例（GFM 全要素 + 安全演示 + 相对路径图片）
scripts/                       publish.ps1 / get-sdk.ps1 / associate-md.ps1 / make-icon.py
.github/workflows/ci.yml       见「CI」
```

## CI

`.github/workflows/ci.yml` 在 Windows runner 上执行以下门禁，任一失败即构建失败：

| 步骤 | 命令 | 作用 |
| --- | --- | --- |
| 还原 | `dotnet restore --locked-mode` | 校验 `packages.lock.json` 与 csproj 版本一致（改包需 `dotnet restore --force-evaluate` 更新锁文件） |
| 构建 | `dotnet build -warnaserror` | 强制零警告 |
| 测试 | `dotnet test` | 91 个用例全过 |
| 前端语法 | `node --check reader.js` | 页面脚本无编译期检查，补一道语法门禁 |
| 发布链路 | `pwsh scripts/publish.ps1` | 验证单文件发布可产出 `MarkdownLite.exe` |

## 性能

**启动速度**：单文件发布有三种参数档位，实测（冷启动到主窗口出现）：

| 档位 | 体积 | 窗口出现 |
| --- | --- | --- |
| 压缩 + JIT（无预编译） | 64.1 MB | 995 ms |
| **R2R 预编译 + 压缩（默认）** | **68.6 MB** | **904 ms** |
| R2R 预编译 + 不压缩 | 158.6 MB | 923 ms |

默认档只多花 4.5 MB 体积换 ReadyToRun 预编译，冷启动最快；不压缩的档位启动几乎不变却重 90 MB，因此默认压缩。

**关于便携版的首次启动**：单文件便携版**第一次运行需要 10~20 秒**（自解压 + Windows 对新程序的一次性安全扫描），之后每次启动都在 1 秒以内；Windows 清理临时文件后可能需要再经历一次，属正常现象。**安装版没有这个问题**——文件直接安装到本地目录，装完启动就是毫秒级，介意首次等待的用户建议选安装版。

**渲染**：文件读取、Markdown 解析/渲染、HTML 落盘全部在后台线程执行，UI 线程只做导航与状态更新，1 MB 级文档的解析渲染为数百毫秒量级（状态栏实时显示耗时）。生成大文件观察滚动表现：

```powershell
1..40000 | ForEach-Object { "## 章节 $_`n`n正文内容 https://example.com 与 `code` 混排。`n" } | Set-Content big.md -Encoding utf8
```

## 安全设计

1. **源文件零改动**：全程以只读方式打开，任何写操作都不指向源目录；渲染产物只写入本机应用数据目录。
2. **原始 HTML 默认剥离**：解析后在 AST 层移除所有 `HtmlBlock`/`HtmlInline` 节点，`<script>`、内联事件不可能进入输出。
3. **URL 协议白名单**：`javascript:`、`vbscript:`、`data:`（非图片）、未知协议一律中和为 `#`；相对路径强制解析到当前文档目录内，`../` 越界直接拒绝。
4. **页面无法联网**：WebView2 仅加载本地虚拟主机，页面自带严格 CSP（`default-src 'none'; connect-src 'none'`）；唯一执行的脚本是随应用分发的 `reader.js`。
5. **导航拦截**：白名单外的导航一律取消并转交系统浏览器；禁用新窗口、下载、iframe、右键菜单、DevTools 与浏览器快捷键。
6. **无编辑路径**：没有保存/另存为/导出命令，复选框渲染为禁用态。

### 如何验证「只读」承诺

用哈希对比即可自证，打开前后必须完全一致，且源目录不新增任何文件：

```powershell
Get-FileHash samples\示例文档.md          # 打开前记录哈希
# ……用本应用打开、滚动、切主题、缩放、按 F5 重载……
Get-FileHash samples\示例文档.md          # 打开后哈希不变
Get-ChildItem samples\                    # 无 .html / 临时文件产生
```

渲染产物与用户数据只落在应用自己的数据目录（`%LOCALAPPDATA%` / `%APPDATA%` 下的程序数据夹），不会写入被阅读文件所在目录。

## 许可证

[MIT](LICENSE)
