# 更新记录

## Unreleased

- 模块功能改动的验收要求补充真实 IL2CPP Player 构建和运行，不再仅凭 Editor/Mono/工具 CI 结项。新增独立 `ActionTimingIL2CPP` 门禁，复用 PlayerNative：Windows x64 Release、Medium 裁剪、双进程公共 API 与生命周期回归；探针隔离在产品编译图之外，不新增运行时 API 或可选 SDK 依赖。

- Action 新增 `StartHandle` / `StartHandleCurrentScene` / `StartHandleGlobal`，复用现有 ActionHandle 与执行租约，匿名安全控制不再要求名字；原 Start 和命名 API、有效输入的时钟/首步/重复语义保留。
- Action 必需委托、非有限时长、无效启动宿主和非法更新模式明确早失败，Repeat 小于 -1 不再无声挂起。原先用非法值永久阻塞的用法改用 Condition(() => false)；不改变有限零/负时长及空 Parallel 的既有时序。
- 内置组合拒绝自身、重复对象、已释放父/子和已调度根子项；配置失败或父 Append 拒绝时清理本次新建责任并保留原始异常。重复逻辑应分别构造子动作，非全局动作图所有权检查，无类型或序列化迁移。
- Fade 的非正时长路径不再先除零或写中间 NaN；正式资源已通过当前 Unity/D3D12/1920×1080 Game View 的背景、全屏遮罩与退场像素验收，运行时根缩放正常，保留资产/GUID，不据序列化零缩放改写布局。画面验收独立为显式人工用例，专用入口只保存 XML/截图、不退出 Editor；自动 Action 专项保持 batchmode。
- Timing 保留到期内核、精确 run/Scope/复用槽与组件 fail-fast；补组件非法 delay 后恢复、跨模块关闭隔离、匿名句柄与 TimerWait 的真实两 Play 回归。Foundation / Tools 新增 Timing 精选指南和完整组件示例，不新增统一调度、等待或生命周期系统。

- 修复 SerializedDictionary 多对象 Inspector 漏掉非首目标独有结构变化的问题：逐目标原生观察、明确附着/分离所有权，结构重建后绑定新字段，不丢弃或自动应用调用者待编辑数据。
- 补充规则文件在 Windows 共享读锁下的原子替换与失败保全回归，并为继承编辑的 IOException 保留阶段、HResult 和路径诊断；不以重试或降级覆盖掩盖尚未确诊的历史持锁者。

- Core 启动请求绑定原 manager/会话并显式持有、停止和释放真实生产迭代器；退出、会话重置及实际协程宿主 GameObject 失活/销毁时，内部失败回滚到 Closed 并结束协程/Task 等待，不再遗留 Pending 启动或资源请求消费者。
- 启动收束在资源归还/业务回滚之前防重入，保留首次异常；诊断格式化失败仍交付稳定请求终态。业务回调中失效延后至回调返回后回滚，组件 enabled=false 不误判停产；保持 Starting 拒绝 Close/Shutdown、Abort 不支持和 Running 模块显式关闭的合同。
- 在途启动于外部 MVC 事务内失效时，通过原 Module 的真实最外层事务出口同步续接，覆盖嵌套初始化失败回滚及 View/Panel 输入归还，避免事务保护拒绝关闭后却提前交付终态。

- Async 请求采用单次终态：重复 Complete/Abort 不再改写首次 Error/Progress 或重跑 hook；protected IsCompleted 改为只读，外部派生类须经 Complete 提交，领域结果仍需由生产者自行防重。
- CustomAsyncOperation 新增惰性缓存 AsTask，多个消费者等待同一请求；失败/取消仍读取请求结果。完成订阅自动断开，静默完成、回调/hook 异常或清空普通订阅均不会丢失 Task 完成信号；普通通知保持 fail-fast，必要 hook 继续收束，多处异常保留为 AggregateException。
- HTTP 移除原生 completed 与协程恢复的双驱动，不再创建或依赖协程宿主；保留 Pre/Post、独占 Handler 所有权、会话取消及释放后完成顺序，响应头改为独立且大小写不敏感的只读快照。
- HTTP 原生回归纳入 Core 的精确测试程序集选择，新增禁 Domain Reload 的真实两 Play 失效、Task 收束和事件隔离验证；不增加产品程序集或网络平台能力。

- 对象池公开合同收敛：ObjectPool/Safe/集合的MaxPoolSize统一为默认20、-1不限、0禁缓存、其他负数非法；0仍可借出并在归还时清理，预热不创建。旧0/其他负数不限调用需迁-1，内部Action已同步。
- SafeObjectPool封闭为sealed、私有无参构造及私有转发实现，保留工厂配置、IOfPool和原owner/会话责任；旧派生扩展改用工厂/IOfPool或组合显式ObjectPool，不保留兼容继承壳，默认FIFO与显式LIFO不变。

- 新增YL0011高置信度使用诊断：直接丢弃真实框架池Rent租约时警告，保留合法using/保存/返回/交接，不增加运行时包装或完整借用检查。
- Audio等待请求更新使用独立作用域快照，消除有界预热后的逐次临时数组；保留取消、重入新请求、Shutdown、错误记录与原资源责任，不改变公开查询数组。

- GameObject 池的健康借还不再反复全队列扫描；容量压力及精确计数保留存活修剪，初始化与清理同时失败时保留原始异常。
- 普通池与 Safe 池新增带代次的 `Rent(out T)` / `PoolLease<T>`，支持一次性 `Dispose` 正常回收和 `Discard` 永久丢弃；保留原始自管理入口，不替换领域句柄。
- 集合池新增作用域 `Rent` 和实际容量保留上限（List 4096、Dictionary 8192槽位），超大存储清元素后丢弃；Dictionary热借用也保证请求容量。静态会话切换隔离旧借用回流，公共Clear语义不变。
- Gameplay的Skill/Buff三阶段更新复用独立作用域快照，保留嵌套、异常、实例版本和最外层延迟归还；不改变公开查询集合或领域资源所有权。
- 静态会话重置在单项失败后仍尝试其余清理，聚合错误且保持执行门禁关闭；增加禁Domain Reload的真实双Play池缓存/晚归还/原owner隔离验证。
- 补齐Unity2022 LTS与Unity6的Mono/IL2CPP池与领域验收入口，反射业务构造仍遵循显式保留合同；新增脚本元数据采用2022实际生成的完整Importer默认字段并保留原GUID，避免跨版本重复改写。

框架正在开发中，尚未正式发布。当前功能与接入说明见[使用手册](Documentation~/README.md)。
