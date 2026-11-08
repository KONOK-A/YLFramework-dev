# 扩展工具（Extensions）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

集中存放常用 Unity 与 System 类型的扩展方法与小型静态工具，减少样板代码；Resource 等上层消费者按需使用。Foundation 的事件/单例适配直接使用 Unity 原生 API，不为复用这些便利方法增加底层封装。

## 位置

- 运行时：[`Runtime/Utilities/Extensions/`](../../Runtime/Utilities/Extensions/)，统一使用 `YLFramework.Extensions`，属于 `YLFramework.Runtime`。
- `Unity/`：GameObject、Component、Transform、Camera、Animator、物理、场景、颜色与 UI 等扩展。
- `System/`：字符串、IO、集合、托管对象、反射与 Type 等扩展。
- `Math/`：`MathExtension`、`VectorExtension`。
- `Utilities/`：`RandomUtility`。
- 字节与进度显示：[`Runtime/Utilities/StringTools.cs`](../../Runtime/Utilities/StringTools.cs)，namespace `YLFramework.Utilities`，归 Runtime。
- 共用成员判断：[`Runtime/Foundation/Utilities/LayerMaskUtility.cs`](../../Runtime/Foundation/Utilities/LayerMaskUtility.cs)，namespace `YLFramework.Foundation`，供物理 Listener 与 Runtime 包装扩展使用。

业务 asmdef 引用 `YLFramework.Runtime`，按需添加对应 using；无需 Root、Module 或 Manager。

## 主要功能

- Unity：显示隐藏、父子层级、位置/旋转/缩放链式设置、组件获取与自动添加、销毁、位置同步、相机截图、颜色与 UI alpha/fill。
- System：字符串解析与清洗、路径/目录/文件操作、集合遍历与字典合并、托管引用判空、反射与 Attribute 查询。
- 数学：随机取项、角度/弧度换算、三角函数、方向与距离换算。

## 用法

```csharp
using UnityEngine;
using YLFramework.Extensions;

LayerMask mask = 0;
mask.Set(0, true); // 使用层号，不要求额外的层名配置。
int count = "12".ToInt(defaultValue: 0);
```

Unity 对象操作仍遵循原生主线程与生命周期规则；扩展方法不自动接入 Module，也不为调用方管理返回的对象或贴图。

## 关键约定

### Unity 对象判空

- 模块内对 UnityEngine.Object 的判空统一使用 Unity 的 `== null` 语义：已被 Destroy 的对象视为空。
- `DestroySelfGracefully` / `DestroySelfWithDelayGracefully` / `DestroyGameObjGracefully` 会跳过 null 与已销毁对象；非 Gracefully 版本不做判空。
- `SystemObjectExtension.IsNull / IsNotNull` 是**托管引用判空**，不能识别 Unity fake-null；判断 Unity 对象请直接写 `obj == null`。
- `ObjectExtension.IsDestroy` 对 null / 已销毁 Unity 对象返回 true；对存活 GameObject / Component 返回 false，与托管引用判空不合并。
- `ObjectExtension.InstantiateWithParent(Component parent, ...)` 把已销毁的 parent 视为 null（挂到场景根）；`Transform` 重载本身接受 null。

### 区域格式

- `ToInt / ToLong / ToFloat / ToDateTime` 使用当前 Culture 解析，失败返回默认值；机器数据调用方需自行固定 Culture。
- `HtmlColorExtensions.HtmlStringToColor` 基于 `ColorUtility`，与 Culture 无关。
- `StringTools.FormatByte(long)` 使用当前 Culture 的人类显示、1024 进制及 b/kb/mb/gb/tb 后缀；TB 以上继续以 tb 显示，最大 long 不返回空文本。小于 1024 的值按有符号 b 显示，不作为机器解析格式。

### 文件与路径

- 路径拼接、文件名、扩展名、目录名都委托 `System.IO.Path`，遵循当前运行平台的路径规则，不跨平台自动转换路径文本。
- `CreateDirIfNotExists` 幂等；`DeleteDirIfExists` 不存在时为空操作；`EmptyDirIfExists` 会清空已有目录，目录不存在时会直接创建。
- IO 异常向上抛出，不吞掉。

### 数学与向量

- `float.Lerp(a, b)` 的 `self` 是插值系数 t，会被 clamp 到 0~1。
- 零向量 `normalized` 返回零向量；`ToAngle / ToRadius` 对零向量返回 0。
- `Abs(int)` 的返回类型为 `float`，需要整型结果时不要假定它返回 int。

### 随机与层掩码

- `RandomUtility` 使用 `UnityEngine.Random` 全局随机状态；`Percent(p)` 中 p≤0 恒为 false、p≥1 恒为 true；`Choose` 空数组抛异常。
- `Percent` 对分数百分比向上离散；`RandomExtensions.Random` 先用 `Mathf.RoundToInt` 离散到百分比，再用 1..100 的整数判定。两者不是同一算法，端点是否推进全局 RNG 状态也不同。
- Foundation `LayerMaskUtility.Contains` 与 `IsInLayerMask` 共用严格 0..31 成员判断；非法层号及未定义层名返回 false。null 或已销毁 GameObject/Component 的包装返回 false。
- `LayerMaskExtensions.Set` / `PhysicsSetting` / `Physics2DSetting` 非法层号抛 `ArgumentOutOfRangeException`，掩码不变；未定义层名的 Set 同样拒绝。Physics 方法只读取当前矩阵，将已命名且可碰撞的层加入返回掩码，不修改全局碰撞设置。
- `GameObjectExtension.Layer` 仅改自身；`SetLayer` 改自身及默认 `GetComponentsInChildren<Transform>()` 收集的活跃子孙，不改 inactive 子孙。两者层名入口仍沿用未定义层名写入 -1 的 Unity 行为，不与严格掩码 Set 混同。

### 射线、旋转与类型工具

- Ray 的 `MinimumDistance`、两个 `GetClosestPoints` 与 `Distance(Vector3)` 保留**无限直线**用途；最近点参数可为负。距离非负、同向/反向平行均可求一组最近点，零方向视为原点。`DistanceToRay(Vector3)` 才限制射线半轴，投影在原点后方时计算到原点的距离，不能用前述方法承诺半轴最近点。
- `QuaternionExtensions.InverseLerp` 是最短角距离比，不判断 current 是否在 from/to 同一插值弧上，也不 clamp 到 0..1。有限非零四元数先归一化，q/-q 等价；同端点返回 0，角差使用双精度 atan2 差长/和长计算，不用 `Quaternion.Angle` 的小角度死区。零或非有限四元数抛 `ArgumentException`，无法表示为有限 float 的比例抛 `InvalidOperationException`。
- `ParabolicMotionVelocity` 是无阻力、无运动约束、恒定重力的连续解，不是物理运动控制器或原生离散积分的精确落点保证。3D 尊重 `useGravity` 及完整 `Physics.gravity`；2D 尊重 `Physics2D.gravity * gravityScale` 的方向，忽略目标 Z。指定 Y 初速度后选最晚的正到达时间，通常为下落段；零重力有正时间时可作直线求解，零初速度向低处自由落体仍可求解。
- 抛物线不可达、没有正到达时间、零竖直重力且零竖直初速度导致时间无法确定，或所需速度超出有限 float 时抛 `InvalidOperationException`；非有限输入抛 `ArgumentException`，null/已销毁刚体抛 `ArgumentNullException`。`ParabolicMotion` 只在计算成功后写速度，拒绝路径保留已有运动状态；不改重力、drag、constraints 或对象生命周期。
- `GetDelegateDisplayName` 保持 `Name(ArgName,...)` 风格；FullName 不可用时回退到 Name，开放/部分构造泛型也列出泛型参数，移除泛型元数但保留嵌套类型名。null Type 抛 `ArgumentNullException`，不是委托签名反射平台。

### 集合与相机

- `IEnumerable.ForEach` 先物化快照，数组输入直接复用原数组；字典 `ForEach` 是实时枚举，遵循字典自身的迭代修改限制和异常，不为统一语义添加快照分配。反向 List 与带索引遍历保持各自用途。
- `CaptureCamera` 同步借用相机，恢复原 `targetTexture` 与 `RenderTexture.active` 并销毁自身临时 RenderTexture；返回 Texture2D 由调用方销毁。rect 须落在当前屏幕像素范围。

## 主要消费者

- Resource：`InstantiateWithParent`、`GetOrAddComponent`。
- Foundation 的 Events / Singleton / Pool 直接使用 Unity 原生 API，不依赖本扩展；上层和通用客户端可按需使用便利方法。
- Editor（Resource 工具）：使用 `GetFolderPath` 等路径便利入口。目录创建/删除、类名清洗和字符串替换仍为通用工具，不要求特定领域消费者。

## 已知限制

- `Split(this string, char)` 扩展在 .NET Standard 2.1 下会被 BCL 实例方法遮蔽，可用 `SystemStringExtension.Split(s, ',')` 静态调用；null 输入抛 `NullReferenceException`。
- `Merge` 不支持重复 Key：键同值不同的键值对会抛 `ArgumentException`。
- `ReflectionCallPrivateMethod` 支持当前类型的 private/protected 与继承的 protected 方法；不查找公共方法与基类 private 方法，不支持泛型方法定义；方法不存在返回 null，重载歧义抛 `AmbiguousMatchException`。
- `Layer(this GameObject/Component, string)` 传入未定义层名时会尝试写入 -1，应在业务侧确认层名有效。
- `CameraExtension.CaptureCamera` 的 rect 以屏幕像素为单位，返回的 Texture2D 由调用方持有并销毁。
- `GetRandomItem` / `Choose` 在空集合上抛异常，不做静默默认值。
