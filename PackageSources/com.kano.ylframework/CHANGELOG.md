# 更新记录

## Unreleased

- GameObject 池的健康借还不再反复全队列扫描；容量压力及精确计数保留存活修剪，初始化与清理同时失败时保留原始异常。
- 普通池与 Safe 池新增带代次的 `Rent(out T)` / `PoolLease<T>`，支持一次性 `Dispose` 正常回收和 `Discard` 永久丢弃；保留原始自管理入口，不替换领域句柄。
- 集合池新增作用域 `Rent` 和实际容量保留上限（List 4096、Dictionary 8192槽位），超大存储清元素后丢弃；Dictionary热借用也保证请求容量。静态会话切换隔离旧借用回流，公共Clear语义不变。
- Gameplay的Skill/Buff三阶段更新复用独立作用域快照，保留嵌套、异常、实例版本和最外层延迟归还；不改变公开查询集合或领域资源所有权。
- 静态会话重置在单项失败后仍尝试其余清理，聚合错误且保持执行门禁关闭；增加禁Domain Reload的真实双Play池缓存/晚归还/原owner隔离验证。
- 补齐Unity2022 LTS与Unity6的Mono/IL2CPP池与领域验收入口，反射业务构造仍遵循显式保留合同；新增脚本元数据采用2022实际生成的完整Importer默认字段并保留原GUID，避免跨版本重复改写。

框架正在开发中，尚未正式发布。当前功能与接入说明见[使用手册](Documentation~/README.md)。
