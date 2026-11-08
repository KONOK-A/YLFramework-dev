# 配置式状态机使用手册

> [包入口](../README.md)

本包用 `RuntimeFsmController` 保存节点、参数和过渡，由场景中的 `FsmController` 为每个运行层创建独立实例并驱动状态脚本。它不要求 Root、Module 或输入包，也不自动转换其他状态机的资产。

## 前置与程序集

安装主包与 `com.kano.ylframework.state-machine`。本地 UPM 与 Git 自有依赖来源见[安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，该链接指向源码仓库当前文档。

| 用途 | namespace | asmdef 引用 |
| --- | --- | --- |
| 配置、宿主、状态脚本 | `YLFramework.StateMachine` | `YLFramework.StateMachine` |
| 图与脚本作者工具 | `YLFramework.Editor.StateMachine` | `YLFramework.StateMachine.Editor` |

Runtime 依赖主包 `YLFramework.Runtime`；Editor 依赖本包 Runtime 与主包 `YLFramework.Editor`。在自有 asmdef 下生成的状态脚本也必须引用本包 Runtime，生成器不会替使用工程修改 asmdef。使用 `Assembly-CSharp` 时采用 Runtime 的正常自动引用。

需要纯代码、由调用方自行推进的状态机时，使用主包的[轻量 FSM](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/FSM.md)，两者不共享运行状态和结束责任。

## 创建第一套配置

1. 在 Project 中选择自己的资产目录，通过 `Assets/Create/YLFramework/Asset FSM/FSMController` 创建配置。此入口会补齐内置节点，不应以空 ScriptableObject 代替正式创建流程。
2. 打开 `Window/YLFramework/Asset FSM/StateMachineWindow`，选择配置，添加普通状态并指定默认状态；按需要增加子状态机、参数和过渡。
3. 通过 `Assets/Create/YLFramework/Asset FSM/FSMState` 创建状态脚本，或在选中状态的脚本列表中使用 `Add Script` / `Create And Add` 绑定。
4. 在自己的 GameObject 上添加 `FsmController`，在运行层中填入名称与配置；需要共享数据时，在它的 `Start` 前设置 `UserData`。
5. 进入 Play Mode，控制器在 `Start` 初始化，之后转发 `Update`、`LateUpdate`、`FixedUpdate`。先观察状态进入，再接入实际业务。

以下状态脚本可放在使用工程的源码目录，并绑定到默认状态。它是普通 C# 类，不挂到 GameObject，也不能在构造器中依赖尚未注入的 Controller。

```csharp
using UnityEngine;
using YLFramework.StateMachine;

public sealed class IdleState : FsmState
{
    public override void OnEnter()
    {
        Debug.Log("Entered IdleState", Controller);
    }
}
```

状态类型必须是具体的 `FsmState` 派生类并有 public 无参构造。使用 IL2CPP 时，使用工程须定向保留实际通过反射创建的类型；类型名存在于资产中不等于裁剪器会保留它。

## 参数与过渡

| 操作 | 当前 API |
| --- | --- |
| 布尔参数 | `SetBool` / `GetBool` |
| 浮点参数 | `SetFloat` / `GetFloat` |
| 整数参数 | `SetInt` / `GetInt` |
| 一次触发 | `SetTrigger` / `GetTrigger` / `ResetTrigger` |
| 参数存在性 | `ContainsParameter(name, ParameterType)` |
| 名称哈希 | `FsmController.StringToHash` |
| 当前状态 | `GetCurrentStateInfo(layerName, subStateName)` 或索引重载 |

参数入口提供名称与哈希重载，写入按名称和参数类型向各层广播。同名但不同类型的参数不应当作一个值；整数参数底层仍以浮点数存储，不应作为任意精度整数容器。

条件组内是 AND，组间是 OR。过渡按配置顺序求值，主动扫描采用首个满足项；参数变化也可能同步触发切换，因此 Set 后不能假设仍处于原状态。空条件链只有在 `autoSwitch=true` 时才自动切换。AnyState、Trigger 与层级进入/退出也应通过图中的显式连接配置。

Trigger 已置位时再次触发会排队；需要撤销时使用 `ResetTrigger`，不要直接改共享配置中的参数列表。

## 运行层与状态回调

`AddRuntimeFsmController(source, layerName)` 可以在 `Start` 前登记源配置，也可以在运行时添加命名层；移除使用 `RemoveRuntimeFsmController` 的名称或索引重载。不要直接增删 `RuntimeFsmControllerLayers` 返回的内部列表来替代这些方法。

- 每个运行层克隆配置、状态与过渡引用，运行数据不写回源资产。`UserData` 在状态节点创建时注入，之后修改控制器属性不会自动重绑已经创建的状态脚本。
- `OnCreate` 在脚本首次进入流程中调用，早于该脚本的 `OnEnter`；同一节点实例至多一次。每次进入调用 `OnEnter`，退出对已经创建的脚本调用 `OnExit`。
- 活动节点收到 `OnUpdate`、`OnFixedUpdate`、`OnLateUpdate`。脚本是普通对象，这些方法由控制器转发，不是 Unity 自动发送的组件消息。
- 默认 `resetOnDisable=true`，禁用时关闭活动状态、清空 Trigger 和过渡记录、恢复参数默认值，保留可重启克隆且不重置脚本创建标记；重新启用后在逻辑更新阶段重启，不重复已执行的 OnCreate。关闭此选项则不能依赖禁用自动重置状态。
- 移除层或销毁控制器时 Dispose 其自有克隆，不销毁借用的源资产。退出异常会先尝试其余层清理，再传播首个错误。
- 回调中增删层、禁用、销毁或重入会使旧流程失效，不能让旧调用栈继续推进已移除实例。循环切换有每帧状态与链路预算，诊断不依赖 scaled Time。

`Initialized` 表示初始层处理流程结束，不保证每一层都成功启动；无效配置与脚本构造问题会记录诊断。`RuntimeFsmController` 属性复用同一个列表，运行时返回活动克隆，否则返回源配置，不是可以长期保存的快照。

## 作者工具与资产所有权

图编辑器支持节点与过渡创建、拖拽、参数和条件组编辑、Delete / Undo / Redo。文本输入中的 Delete 不删除图节点；内置保护节点与运行时数据不能当作普通作者数据删除。

脚本绑定使用明确的 GUID 和类名。`Create And Add` 不覆盖已有文件，会等待实际导入、编译和程序集重载后再绑定。编译失败时先处理真实错误，不从类名猜 GUID，也不以一次延迟回调代替成功导入。程序化运行配置可以使用正式的 `className` 构造能力，不要求伪造 Editor GUID。

内置状态由标记与内置名称共同决定，不能仅用普通 `name` 推断。缺失类型、参数或父节点时，作者工具保留可修复数据，不自动删除全部配置。

配置资产、Prefab、生成的脚本及它们的 `.meta` 属于使用工程。编辑和重命名须保留真实资产身份，不手补 YAML 或覆盖未知输出。卸载本包前先处理使用工程的组件、asmdef 与 API 引用；卸载不会删除用户配置或生成代码。

## 排错与限制

| 现象 | 检查项 |
| --- | --- |
| 状态脚本编译失败 | 自有 asmdef 是否引用 `YLFramework.StateMachine`，是否误把普通状态类当组件 |
| 初始化后没有活动状态 | 配置是否通过 `ValidateConfiguration(out error)`，类型是否有 public 无参构造、默认状态与父层是否有效 |
| 没有自动过渡 | 参数类型、条件组与连接顺序，以及空条件链的 `autoSwitch` |
| Player 缺少状态类型 | IL2CPP 定向保留、精确类名、资产中实际脚本绑定 |
| 禁用后状态没有重置 | `resetOnDisable` 配置及使用工程自身的事件和外部资源关闭责任 |

配置验证、图窗口交互、序列化、IL2CPP 裁剪与实际业务运行是不同验证范围。复用同一资产不等于共享运行层，也不代表外部订阅和资源会自动关闭。
