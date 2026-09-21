# Design

## Theme

浅色单主题。物理场景：开发者坐在办公室屏幕前调试接口，白色内容面 + 极浅 mint 灰的环境底，长时间盯看不刺眼。色彩策略：**Restrained**——中性色骨架 + 单一 mint 强调色（≤10%），语义色只用于数据类型和修改标记。

## Colors

| Token | 值 | 用途 |
|---|---|---|
| bg.canvas | `#F2F6F4` | 窗口环境底 |
| bg.surface | `#FFFFFF` | 左右内容面板 |
| bg.chrome | `#FBFDFC` | 工具栏 / 状态栏 |
| bg.header | `#F7FAF8` | 面板小标题条 |
| line.strong | `#E2EAE5` | 面板边框 |
| line.soft | `#ECF2EE` | 分隔线 |
| ink.key | `#1E2A23` | 树 key（近黑） |
| ink.body | `#24312A` | 源码正文 |
| ink.caption | `#43544B` | 面板标题文字 |
| ink.hint | `#66796E` | 提示 / 占位（≥4.5:1） |
| ink.status | `#5F7167` | 状态栏中性文字 |
| accent | `#10B981` | 主按钮 / 选中 / 焦点（≤10%） |
| accent.hover | `#0C9871` | 主按钮悬停 |
| accent.pressed | `#0A7F5E` | 主按钮按下 |
| state.string | `#0B7A5C` | 字符串值 |
| state.number | `#2456B8` | 数字值 |
| state.bool | `#B26205` | 布尔值 |
| state.null | `#6E7B76` | null / 空容器（斜体） |
| state.count | `#0B7A5C` | 数组数量标注 |
| state.modified | `#C13A2F` | 修改/新增标红（加粗） |
| select.row | `#CDEBDC` | 选中行 |
| select.subtree | `#E9F5EF` | 子树高亮 |

所有正文/代码文字对白底 ≥4.5:1。禁：1px 边框 + 大范围阴影同用；装饰性渐变；彩色侧边条。

## Typography

单一字体族：界面 `Microsoft YaHei UI`，代码 `Cascadia Mono → Consolas → Microsoft YaHei UI` 回退链。刻度：正文/代码 13px，提示 11–11.5px，标题条 12.5px。不用衬线、不用等宽以外第二代码字体。

## Components

- **按钮**：高 30、圆角 6、内边距 14。默认=白底 1px 边；主操作=accent 实底白字（全界面仅「格式化」一个）。四态齐全（hover/pressed/disabled/focus accent 描边）。
- **面板**：白底 1px 边框圆角 8，**无阴影**。标题条 34px，bg.header，左侧标题 + 右侧提示。
- **树**：18px 缩进步进，chevron 悬停变 accent，选中行 select.row + 子树 select.subtree，行圆角 4。
- **状态栏**：28px，左状态右统计；成功/错误/中性三态颜色。
- **空状态**：大号 `{ }` 字符标记 + 一句主说明 + 一行能力提示，教用户第一步。
- **动效**：仅状态反馈（悬停变色、选中底色），无入场编排，无装饰动画。
