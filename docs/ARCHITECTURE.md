# 架构说明

## 分层

```
┌──────────────────────────────────────────────────────────────┐
│ PhiEdit.App（WinUI 3 / Windows App SDK 1.8 / Win2D）          │
│  MainWindow · NoteSurface · EventSurface · ThinSplitter      │
│  只负责呈现与输入，不直接改写谱面数据                          │
├──────────────────────────────────────────────────────────────┤
│ PhiEdit.Core（net8.0，无 UI 依赖）                            │
│  Beats · Charts · Editing · Layout · Rendering · IO           │
│  Playback · Extensions                                          │
├──────────────────────────────────────────────────────────────┤
│ PhiEdit.Core.Tests（xUnit）                                    │
└──────────────────────────────────────────────────────────────┘
```

`PhiEdit.Core` 不引用任何 UI 程序集，因此可以在服务端、CLI 或单元测试中直接复用，
这也是「较强可扩展性」的基础：领域逻辑与呈现完全解耦。

## 关键类型

| 类型 | 职责 |
| --- | --- |
| `Beat` | 拍的三元组（整数, 分子, 分母），与 RPE 文件格式无损互转 |
| `TempoMap` | 拍 ↔ 秒双向换算，支持多段变速点 |
| `ChartDocument` | 谱面文档：元信息、BPM 表、判定线集合 |
| `JudgeLine` | 判定线：基础属性 + 按层组织的事件轨道 + 音符 |
| `Note` / `LineEvent` | 音符与事件；均带 `OwningLine` 回引用 |
| `EditorSession` | 编辑会话：当前判定线 / 层 / 选择集 / 工具 / 网格参数 / 视口 |
| `ChartEditor` | 所有写操作的唯一入口，统一走撤销栈 |
| `UndoStack` | 命令式撤销栈（`IUndoableAction`） |
| `EditViewport` | 拍 ↔ 像素、舞台 X ↔ 像素的双向映射 |
| `VerticalGrid` | 竖线网格：间距、是否含居中线、吸附 |
| `GridLayout` | 时间网格刻度生成（整拍线 / 细分线） |
| `EditorPalette` | 全部绘制颜色与线宽的集中定义，便于主题化 |
| `RpeJson` | RPE JSON 谱面读写 |

## 绘制管线

两个编辑区都是 `UserControl` 内嵌 Win2D 的 `CanvasControl`（`CanvasControl` 是密封类，
因此采用组合而非继承）。每帧由 `Draw` 事件驱动，顺序固定：

```
NoteSurface：背景 → 竖线网格 → 时间网格（整拍线+细分线+拍号）→ 判定线 → 音符
EventSurface：背景 → 时间网格 → 轨道分隔竖线 → 事件条 → 轨道表头 → 底栏数值
```

网格规则集中在 `VerticalGrid` 与 `GridLayout` 中，绘制代码只负责取用，
保证「整拍线红色加粗带拍号、横竖细线为黄色、奇数竖线时居中竖线加粗」这一组规则
只存在一份实现。

## 扩展点

```csharp
public interface IEditorExtension
{
    string Id { get; }
    string DisplayName { get; }
    void Register(IExtensionRegistry registry);
}

public interface IExtensionRegistry
{
    void AddSerializer(IChartSerializer serializer);   // 新增谱面格式
    void AddTool(IToolProvider tool);                  // 新增放置工具
}
```

新增谱面格式（JSON / PEC / PEZ）或新的放置工具，只需实现对应接口并交给 `ExtensionRegistry`，
无需改动界面代码。

## 已知平台差异

| 差异 | 说明 | 处理方式 |
| --- | --- | --- |
| `GridSplitter` 缺失 | WinUI 3 未提供该控件，且会让 XAML 编译器静默崩溃 | 自研 `ThinSplitter` |
| 文本枚举命名 | Win2D 1.4 使用 `CanvasHorizontalAlignment` / `CanvasVerticalAlignment` | `CanvasExtensions.TextFormat` 统一封装 |
| 文件选择器 | `FileSavePicker.FileTypeChoices` 为 `KeyValuePair` 列表 | 代码内适配 |
| 控制台代码页 | 非 UTF-8 时 XAML 编译器不输出诊断信息 | `build.cmd` 先 `chcp 65001` |
