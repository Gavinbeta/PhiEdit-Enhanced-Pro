# 项目长期约定

- 完成任何修改任务后，必须启动 PhiEdit 主窗口供用户查看，需求完成时供用户查看的窗口不得置顶，不能只报告构建或测试结果。
- 编辑区必须是真正的一块 `CanvasControl`，音符区与判定线区由同一画布分区绘制，不允许拆成两个独立画布再伪装。
- 参考源码放在 `reference/` 且保持 Git 忽略；只复用规则，不照搬原程序 1350×900 的固定像素常量。
- 界面禁止使用 Emoji。
- 版本号格式固定为 `[x].[y].[z]_BETA[i]`。`x` 只有用户明确要求时才变更；`y` 用于新增大功能；`z` 用于大功能的小功能追加；`i` 用于缺陷修复。每完成一个功能或修复都要按规则变更版本。特别地，如果版本号的 `y` 与 `z` 都为 0 ，则不需要加 `_BETA[i]`。当前版本为 1.3.2_BETA6。
- 在写任何程序之前，应该先完整阅读参考程序的有关部分，需要参考的编辑器代码是"E:\Programs\PhiEdit-Enhanced-Pro\reference\PhiEdit-Base-main.zip"；专业渲染器的代码是"E:\Programs\PhiEdit-Enhanced-Pro\reference\phira-render-main.zip"中，对应的解压版本在"E:\Programs\PhiEdit-Enhanced-Pro\reference\rpe-next-source"和"E:\Programs\PhiEdit-Enhanced-Pro\reference\phira-render-source"。