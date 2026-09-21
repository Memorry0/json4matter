# JsonFormatter

跨平台 JSON 格式化查看器 — Windows / macOS（Avalonia + .NET 8）

## 功能

- **左右双栏**：左侧粘贴源码，粘贴后自动解析（280ms 防抖），右侧树形视图
- **数组自动折叠并标注数量**：`gradeList [3]`，点箭头逐级展开
- **单击源码定位**：光标点在左侧任意 key/value 上，右侧立即展开并高亮对应节点
- **修改对比标红**：与上一次解析内容对比 — 改值只红值、改 key 名只红 key、新增则整行红
- **隐藏空值**：`格式化结果` 标题右侧开关，一键隐藏 `null` / `""` / `[]` / `{}` 字段
- **两种压缩**：`压缩`（中文转 `\uXXXX`）与 `压缩原文`（保留中文）
- **三套主题**：清新绿 / 纸质黄 / 干净白，运行时即时切换
- **设置**：右下角齿轮 — 字体大小、默认全屏启动、主题选择，自动持久化
- 右键树节点：复制值 / 复制该节点 JSON；大文档自动降展开深度

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
# 运行
dotnet run -c Release

# 发布（Windows，单文件）
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

# 发布（macOS Apple Silicon）
dotnet publish -c Release -r osx-arm64 --self-contained false -p:PublishSingleFile=true

# 发布（macOS Intel）
dotnet publish -c Release -r osx-x64 --self-contained false -p:PublishSingleFile=true
```

自包含发布（目标机器无需安装运行时）加 `--self-contained true -p:PublishSingleFile=true`。

## 项目结构

```
JsonFormatter/
├── App.axaml(.cs)        # 应用入口、主题资源、全局异常日志
├── MainWindow.axaml(.cs) # 主窗口：布局、解析、定位、diff、压缩
├── SettingsWindow.axaml  # 设置窗口（主题/字号/全屏）
├── Themes/               # 三套主题资源字典（Token 化配色）
├── ThemeManager.cs       # 主题切换 + 跨平台字体选择
├── JsonNodeVM.cs         # 树节点视图模型（徽章/diff/高亮）
├── JsonPathLocator.cs    # 源码光标 → JSON 路径的词法扫描器
└── Settings.cs           # 设置持久化（%APPDATA%/JsonFormatter）
```

## 设计

界面遵循克制用色的产品设计规范：中性色骨架 + 单一强调色（≤10%），key 近黑（≥4.5:1 对比度），值按类型着色（字符串绿/数字蓝/布尔橙/null 灰），修改标红同时加粗。面板无阴影（1px 边框 + 圆角），按钮全界面同一形状词汇，动效仅状态反馈。

## License

MIT
