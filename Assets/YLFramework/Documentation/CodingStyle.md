# YLFramework 代码规范

适用于 YL 自有及已融合代码。准确、清晰优先；上游库和外部契约保持原规则。分层与依赖方向见 [架构](Architecture.md)。

## 命名

先保护外部契约，再判断实际 Unity 序列化资格，最后按访问级别命名；不能仅凭 public 判定序列化。

| 对象 | 规则 |
|---|---|
| 类型、方法、属性、事件、枚举及成员 | PascalCase；接口 `IResourceSource`，泛型参数 `TState/TKey` |
| const（含私有、局部常量） | PascalCase，优先于其他字段/局部变量规则 |
| 参数、普通局部变量 | camelCase |
| 实际 Unity 序列化字段 | camelCase，优先于访问级别规则 |
| private 非 Unity 序列化字段（非 const） | `_camelCase`，static/readonly 同规则 |
| 其他非 Unity 序列化数据成员 | PascalCase |

- 缩写用 `Id/Http/Json/Xml/Url/Api/Guid/Mvid/Fsm`；保留 UI、IO、官方产品名和外部契约。
- 文件名对应主类型，partial 用 `Type.Concern.cs`；集合用复数，歧义数值注明单位，如 `timeoutSeconds`。
- 异步入口用 `Async`；事件表达已发生的事，如 `Completed`；Unity 消息/覆写钩子可用 `OnXxx`，处理器可用 `HandleXxx`。
- 名称表达领域含义，不堆类型后缀或含糊缩写；Manager/Helper 等自然名称无需机械替换。

## bool

- 状态、持有、能力用 `Is/Has/Can`；策略、支持、要求可用 `Should/Supports/Requires`。按成员规则写成 `_isReady`、`isReady` 等。
- `loop/fade/bindCore/includeInactive` 是行为选项，不机械加 Is；`Contains/Matches/Exists/TryGetValue` 不重复加判断前缀。
- 普通 bool 直接判断；保留 bool? 的 null 语义、原真值和默认值，不为正向命名反转行为。

## 排版与组织

- C# 9、UTF-8 无 BOM、LF、四空格、单 EOF 换行、无行尾空白；block namespace、Allman 大括号。
- 控制块展开，一行一语句、一字段一声明；多步 setter、状态 lambda 和异常收尾展开并正确缩进。简单属性/转发可用表达式体。
- 120 列软上限，续行缩进四空格；不为行宽改字符串或 URL。System using 在前，保留必要别名与条件编译。
- 成员按职责组织，独立步骤明确才提取方法，不为压缩行数或少量重复增加抽象。
- 注释以中文解释原因、约束和所有权；同步 XML，保留版权，不逐行复述代码。

## 安全改名

- 同步调用、命名实参、反射、`FindProperty`、UnityEvent、文档和生成模板；移动资产保留 `.meta`/GUID。
- Unity 字段按需使用精确 `FormerlySerializedAs`，类型迁移另评估；用真实旧资产验证值、引用与重导入。
- JSON/Excel/存档键、资源地址、枚举值、布局和签名输入不随改名漂移；Json.NET 显式映射，JsonUtility 协议字段必要时保留旧名。
- `keepWaiting` 等外部 override、Audio 公开 JSON 字段、`SerializedDictionary.datas/Key/Value` 是合法例外，不以旧名零命中验收。
- 不借改名改变 API 形状、访问级别、初始化/求值顺序、线程、所有权、取消、异常、重入或 Unity fake-null 语义。

## 格式工具

以下仅适用于源码仓库，默认产品包不携带 `Tools`。从仓库根使用 PowerShell 7.6，执行 `Tools/CodeStyle/Format-YL.ps1 -Mode Check`；Apply 必须以 `-Paths` 指定本次任务的仓库相对路径。

工具按受维护源码范围工作，排除第三方实现、生成代码和 HybridCLR 本机测试输入。Check 只查编码与排版，不重命名、改语义或执行 IDE 命名建议；实际范围与 SDK 前置以脚本及对应版本锁为准。

命名、结构、外部合同和可读性仍需语义审查。编译器诊断的独立构建与验证见 [Attributes](Modules/Attributes.md#重建与验证)，不把格式通过当作行为或 Analyzer 验收。
