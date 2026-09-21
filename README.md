# JsonFormatter

Windows 桌面 JSON 格式化查看器 — WPF + .NET 8，单文件 exe，启动 < 1 秒

## 功能

- **左右双栏**：左侧粘贴源码，粘贴后自动解析（280ms 防抖），右侧树形视图
- **数组自动折叠并标注数量**：`gradeList [3]`，点箭头逐级展开
- **单击源码定位**：光标点在左侧任意 key/value 上，右侧立即展开并高亮对应节点（整棵子树联动）
- **修改对比标红**：与上一次解析内容对比 — 改值只红值、改 key 名只红 key、新增则整行红；点「示例」/「清空」后重置基线不误标
- **隐藏空值**：`格式化结果` 标题右侧开关，一键隐藏 `null` / `""` / `[]` / `{}` 字段，按下态高亮
- **两种压缩**：`压缩`（中文转 `\uXXXX`）与 `压缩原文`（保留中文）
- **三套主题**：清新绿 / 纸质黄 / 干净白，运行时即时切换
- **设置**：右下角齿轮 — 字体大小（11–20 实时生效）、默认全屏启动、主题选择，自动持久化到 `%APPDATA%\JsonFormatter\settings.json`
- 右键树节点：复制值 / 复制该节点 JSON；超大文档自动降展开深度防卡顿

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（仅支持 Windows）。

```bash
# 运行
dotnet run -c Release

# 发布单文件 exe（约 1MB，框架依赖）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true

# 自包含单文件（目标机器免装运行时，约 60MB）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 项目结构

```
JsonFormatter/
├── App.xaml(.cs)        # 应用入口、主题资源、全局异常日志
├── MainWindow.axanl(.cs)# 主窗口：布局、解析、定位、diff、压缩
├── SettingsWindow.xaml  # 设置窗口（主题/字号/全屏）
├── Themes/              # 三套主题资源字典（Token 化配色）
├── ThemeManager.cs      # 主题切换（chrome 字典 + 树 VM 配色）
├── JsonNodeVM.cs        # 树节点视图模型（标数/diff/高亮/隐藏空值）
├── JsonPathLocator.cs   # 源码光标 → JSON 路径的词法扫描器
└── Settings.cs          # 设置持久化
```

## 设计

界面遵循克制用色的产品设计规范（见 PRODUCT.md / DESIGN.md）：中性色骨架 + 单一强调色（≤10%），key 近黑（≥4.5:1 对比度），值按类型着色（字符串绿/数字蓝/布尔橙/null 灰斜体），修改标红同时加粗。面板无阴影（1px 边框 + 圆角），按钮全界面同一形状词汇，设置窗口为 iOS 风格开关与主题色卡。

## License

MIT
