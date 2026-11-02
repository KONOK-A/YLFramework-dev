# Behavior 测试与验收

此页仅面向源码仓库和明确自有的测试宿主。组件、移动和资产状态机的当前用法统一见 [Behavior 模块文档](../../../../Documentation/Modules/Behavior.md)，不在测试夹具中维护另一份产品说明。

## 自动化入口

从源码仓库根使用 PowerShell 7.6，显式传入当前 Editor 和独立工作根：

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 `
  -Action Run -Suite Quick -Module Behavior `
  -Unity $env:YL_TEST_UNITY -WorkspaceRoot $env:YL_TEST_WORKSPACE

pwsh -NoLogo -NoProfile -NonInteractive -File Tools/Testing/Invoke-YLTests.ps1 `
  -Action Run -Suite Daily -Module Behavior `
  -Unity $env:YL_TEST_UNITY -WorkspaceRoot $env:YL_TEST_WORKSPACE
```

| 层级 | 责任 |
| --- | --- |
| Quick | 真实 NUnit 的逻辑闭包和轻量 FSM，不伪造 Unity 时间或物理 |
| Daily | 自有 Unity 宿主的物理、作者资产及两套 FSM 生命周期 |
| Special | 声明的 NativeInput 窗口事件合同，需要真实桌面与独立交互宿主 |

准确选集由 `TestSuites.json` 和本轮发现结果决定。Test Runner 只用于定向交互诊断，不另维护日常测试名单。执行、前置、结果与取消合同统一见源码仓库 `Tools/Testing/README.md`。

## 额外验证

- 场景与 Prefab 使用新建自有夹具，验证重导入、持久 UnityEvent、Undo / Redo 与准确 GUID；不保存其他脏资产或重绑原工程内容。
- Overlap / CharacterController / Platform 需要真实物理、不同时间与层配置、停用 / 销毁和重入验证；离线结果不是物理或性能证据。
- 原生窗口事件不等于 OS 鼠标设备自动化，也不代替拖拽、主题、Scene 手柄等完整人工作者流程。
- Mono / IL2CPP、Low stripping、反射状态与泛型事件必须用对应 Player 验证；图形或音频条件不能用 nographics 冒充。
- 旧主 Editor 和固定工作区入口只适用于原身份，不直接调用其历史 PID、owner、授权或冻结路径。新专项必须明确自己的宿主、输入、产物和保护边界。

## 历史证据

T3-A、BM26 和后续 GUI / IL2CPP 的原始失败、修复与结果保留在本文件 Git 历史及对应任务 / 交接记录。此页不再复制机器路径、Hash、PID 和旧测试数量；它们不构成当前通过、工作区保护白名单或新的执行授权。
