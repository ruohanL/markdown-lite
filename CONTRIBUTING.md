# 贡献指南

欢迎参与 MarkdownLite 的开发。

## 开发环境

- Windows 10 1809+ / Windows 11
- .NET 8 SDK（可用 `pwsh scripts/get-sdk.ps1` 取便携版）
- Python 3（仅图标脚本需要，纯标准库，无第三方依赖）

## 提交前请确认

1. **构建零警告**：`dotnet build MarkdownLite.sln -c Release` 必须无警告（CI 以 `-warnaserror` 强制）。
2. **测试全过**：`dotnet test MarkdownLite.sln -c Release` 全部通过。
3. **保持只读承诺**：不引入任何编辑、保存、导出、联网功能。
4. **`scripts/` 下的 `.ps1` 文件需以 UTF-8 带 BOM 保存**（Windows PowerShell 5.1 对无 BOM 文件按 ANSI 解析，中文注释会导致语法错误）。

## 代码风格

- 遵循仓库根目录的 `.editorconfig`（私有字段 `_camelCase`、文件范围命名空间、4 空格缩进）。
- 界面颜色一律走资源令牌（`Brush.*` / CSS 变量），不要在 XAML 或 CSS 里写死颜色。
- 新增弹窗请复用 `Dialogs/AppDialog`，不要用系统 `MessageBox`。

## 提交信息

用一行简洁说明改动主题；涉及多方面的改动建议拆成多个提交。中文或英文均可。
