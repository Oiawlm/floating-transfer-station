# Delete 键删除选择设计

## 用户结果

Windows 用户可以在当前展开分类中选中一张或多张卡片后按标准 `Delete` 键删除它们，不必把手移到标题栏垃圾桶，也不必记忆已有的 `Backspace` 快捷键。`Backspace` 保持兼容。

## 方案选择

采用扩展现有 `MainWindow_PreviewKeyDown` 删除分支的方案：把仅接受 `Key.Back` 的条件改为同时接受 `Key.Back` 与 `Key.Delete`，其余守卫和 `DeleteSelectedItemsAsync` 调用不变。这样直接复用已经验证的批量删除、单次原子保存、保存失败回滚、选择恢复和关闭等待路径。

没有采用 `ApplicationCommands.Delete`，因为窗口仍需自行补充按键手势和焦点守卫，并可能与分类名称编辑框的原生 Delete 行为产生路由竞争。也没有新增自定义 `RoutedUICommand`，因为当前只有一个窗口级消费者，引入新命令不会增加用户能力或可靠性。

## 行为边界

- `Delete` 与 `Backspace` 仅在窗口未进入关闭流程、键盘焦点不在 `TextBoxBase` 中、当前存在展开分类且至少选择一张卡片时删除选择并吞掉按键。
- 无选择时两个按键都不清空整个分类，也不触发保存。
- 分类名称编辑器拥有焦点时，`Delete` 和 `Backspace` 保持原生文字编辑语义，绝不删除卡片。
- 面板折叠或窗口关闭时不处理快捷键。
- 删除仍按当前显示顺序捕获选择，并沿用 `BoardMutationService.DeleteManyAsync` 的原子保存与失败恢复语义。
- 不改变内容模型、持久化格式、置顶分区、批量顺序、拖放载荷或卸载范围。

## 数据流与失败处理

窗口收到 `PreviewKeyDown` 后先完成按键、关闭、焦点和活动面板守卫，再通过现有 `CaptureSelectedItemIds` 冻结本次删除范围。只有范围非空才标记事件已处理并等待 `DeleteSelectedItemsAsync`。保存成功后选择清空；保存失败时现有逻辑恢复原对象、精确顺序、选择集合和滚动位置并显示状态，不为 `Delete` 增加第二套失败路径。

## 验证

真实 STA WPF 交互测试同时锁定 `Backspace` 与 `Delete` 的选择删除、空选择不清空、文本编辑器焦点不删卡片、折叠态和关闭态不处理；既有删除失败回归继续锁定两者共享的原子恢复路径。生命周期测试锁定 README、CHANGELOG 与 ROADMAP 的公开说明。最终运行相关测试、格式验证、Release 全量测试、严格 Release 构建和 `git diff --check`；通过真实 WPF 窗口的前后截图确认选中卡片确实被 `Delete` 删除，验证产物只写入被忽略的 `artifacts/`。
