using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using UnityEngine;
using YLFramework.Pooling;
using YLFramework.Singletons;
using YLFramework.Foundation;
using YLFramework.Resource;

namespace YLFramework.Equipment
{
    /// <summary>主线程装备入口。创建结果由本域回收，直接 Equip 的外部对象只借用。</summary>
    public static class EquipmentManager
    {
        /// <summary>以引用身份区分宿主或装备，避免业务实现的值相等改变归属判定。</summary>
        private sealed class Identity<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly Identity<T> Instance = new();

            /// <summary>判断两个参数是否为同一对象引用。</summary>
            public bool Equals(T a, T b) => ReferenceEquals(a, b);

            /// <summary>取得不受业务重写影响的引用哈希。</summary>
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }

        /// <summary>记录一次配表登记及管理器持有的可选资源租约。</summary>
        private sealed class Config
        {
            internal EquipmentsConfig Root;
            internal string Name;
            internal IDisposable Lease;
            internal bool IsClosing;
        }

        /// <summary>关联装备实例、定义来源、挂接状态与归还责任，不把这些运行状态写入共享定义。</summary>
        private sealed class Record
        {
            internal IEquipment Value;
            internal Config Config;
            internal EquipmentConfigData Definition;
            internal EquipmentsConfig DefinitionRoot;
            internal ObjectPool<IEquipment> Pool;
            internal InstanceLease Lease;
            internal IEquippable Host;
            internal IEquippable PendingHost;
            internal List<IEquipment> List;
            internal bool IsOwned;
            internal bool IsLoaded;
            internal bool IsAttached;
            internal bool IsNotified;
            internal bool IsEquipping;
            internal bool IsUnequipping;
            internal bool IsEquipCancelled;
            internal bool IsReleaseRequested;
            internal bool IsReturning;
            internal bool ShouldDiscard;
            internal long Revision;
        }

        private static readonly Dictionary<string, Config> _configs = new(StringComparer.Ordinal);
        private static readonly Dictionary<IEquipment, Record> _records = new(Identity<IEquipment>.Instance);
        private static readonly Dictionary<Type, ObjectPool<IEquipment>> _pools = new();
        private static readonly HashSet<IEquippable> _closingHosts = new(Identity<IEquippable>.Instance);
        private static readonly ConditionalWeakTable<IEquipment, object> _returnedEquipment = new();
        private static readonly object _returnedMarker = new();
        private static readonly Queue<Record> _pendingReturns = new();
        private static readonly List<Config> _retiredConfigs = new();
        private static int _depth;
        private static bool _isFlushing;
        private static bool _isStopping;
        private static bool _isQuitting;
        private static bool _aggregateReturns;

        /// <summary>接入执行会话重置与应用退出，分别维护退出门禁后清理装备。</summary>
        static EquipmentManager()
        {
            SingletonSessionState.RegisterSessionReset(() =>
            {
                _isQuitting = false;
                Shutdown();
            });
            Application.quitting += () =>
            {
                _isQuitting = true;
                Shutdown();
            };
        }

        /// <summary>同时检查普通引用与 Unity 原生对象的存活状态。</summary>
        private static bool IsAlive(object value) => value != null && (!(value is UnityEngine.Object obj) || obj);

        /// <summary>拒绝关闭流程中或运行退出后的新登记、创建与装备请求。</summary>
        private static void Admit()
        {
            if (_isStopping || _isQuitting && Application.isPlaying)
            {
                throw new InvalidOperationException("Equipment system is closing.");
            }
        }

        /// <summary>执行单项清理并收集异常，使后续独立清理仍可继续。</summary>
        private static void Attempt(List<Exception> errors, Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        /// <summary>按调用要求汇总错误，单个非聚合错误保留原始异常堆栈。</summary>
        private static void ThrowErrors(List<Exception> errors, bool aggregate = false)
        {
            if (errors.Count == 0)
            {
                return;
            }

            if (aggregate || errors.Count > 1)
            {
                throw new AggregateException(errors);
            }

            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }

        /// <summary>追踪嵌套操作深度，在最外层退出时执行延迟归还并合并操作与清理错误。</summary>
        private static T Run<T>(Func<T> operation)
        {
            _depth++;
            var errors = new List<Exception>();
            T result = default;
            try
            {
                result = operation();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                if (--_depth == 0 && !_isFlushing)
                {
                    Attempt(errors, Flush);
                }
            }

            ThrowErrors(errors);
            return result;
        }

        /// <summary>使无返回值操作沿用统一的嵌套与归还边界。</summary>
        private static void Run(Action operation) => Run(() =>
        {
            operation();
            return 0;
        });

        /// <summary>确认记录仍是该实例当前登记的生命周期记录。</summary>
        private static bool IsCurrent(Record record) =>
            _records.TryGetValue(record.Value, out var current) && ReferenceEquals(current, record);

        /// <summary>按登记来源、定义根或条目引用识别装备是否依赖指定配表。</summary>
        private static bool DependsOn(Record record, Config config) =>
            ReferenceEquals(record.Config, config) ||
            ReferenceEquals(record.DefinitionRoot, config.Root) ||
            !ReferenceEquals(record.Definition, null) && config.Root &&
            config.Root.Equipments.Any(data => ReferenceEquals(data, record.Definition));

        /// <summary>记录外部借用实例当前的定义来源；自有实例保持创建时捕获的来源。</summary>
        private static void CaptureDefinition(Record record)
        {
            if (record.IsOwned)
            {
                return;
            }

            record.Definition = record.Value.EquipmentData;
            record.DefinitionRoot = record.Definition ? record.Definition.parent : null;
        }

        /// <summary>判断记录依赖的定义是否正随配表退出，防止回调中重新挂接。</summary>
        private static bool IsDefinitionClosing(Record record) =>
            record.Config != null && record.Config.IsClosing || _retiredConfigs.Any(config => DependsOn(record, config));

        /// <summary>校验后登记共享配表；直接传入的资产由调用方保活，不转移资产所有权。</summary>
        public static void AddConfig(EquipmentsConfig config) => Run(() => AddConfigCore(config));

        /// <summary>检查配表名称、全局 ID 与创建配置，全部通过后才发布登记。</summary>
        private static void AddConfigCore(EquipmentsConfig root)
        {
            Admit();
            if (!root)
            {
                throw new ArgumentNullException(nameof(root));
            }

            if (_retiredConfigs.Any(config => ReferenceEquals(config.Root, root)))
            {
                throw new InvalidOperationException("Equipment config is closing.");
            }

            if (_configs.Values.Any(config => ReferenceEquals(config.Root, root)))
            {
                return;
            }

            if (_configs.ContainsKey(root.name))
            {
                throw new InvalidOperationException($"Equipment config name already registered: {root.name}");
            }

            var ids = new HashSet<int>();
            foreach (var data in root.Equipments)
            {
                if (!data)
                {
                    throw new InvalidOperationException($"Equipment '{root.name}' contains a missing row.");
                }

                if (!ids.Add(data.id) || Find(data.id, out _) != null)
                {
                    throw new InvalidOperationException($"Equipment '{root.name}': duplicate ID {data.id}.");
                }

                Validate(data);
            }

            _configs.Add(root.name, new Config { Root = root, Name = root.name });
        }

        /// <summary>同步取得并登记装备配表，保留一份租约直到配表关闭且相关实例收尾。</summary>
        /// <param name="resources">已初始化的资源系统。</param>
        /// <param name="address">装备配表的正式资源地址。</param>
        public static void LoadConfig(ResourceSystem resources, ResourceAddress address) => Run(() =>
        {
            Admit();
            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            var lease = resources.Acquire<EquipmentsConfig>(address);
            try
            {
                AddConfigCore(lease.Asset);
                var config = _configs.Values.First(candidate => ReferenceEquals(candidate.Root, lease.Asset));
                if (config.Lease == null)
                {
                    config.Lease = lease;
                }
                else
                {
                    lease.Dispose();
                }
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        });

        /// <summary>按登记时的资产名称查找配表，未命中或资产已销毁时返回 null。</summary>
        public static EquipmentsConfig GetConfig(string name) =>
            name != null && _configs.TryGetValue(name, out var config) && config.Root ? config.Root : null;

        /// <summary>查询已登记配表中的共享装备定义，不创建实例；未找到时返回 null。</summary>
        public static EquipmentConfigData GetEquipmentInfo(int id) => Find(id, out _);

        /// <summary>从有效的已登记配表中查找 ID，并返回其登记记录。</summary>
        private static EquipmentConfigData Find(int id, out Config owner)
        {
            foreach (var config in _configs.Values)
            {
                if (!config.Root)
                {
                    continue;
                }

                var data = config.Root.GetEquipmentConfigData(id);
                if (!data)
                {
                    continue;
                }

                owner = config;
                return data;
            }

            owner = null;
            return null;
        }

        /// <summary>按登记名称撤销配表并收尾依赖装备，名称未命中时不处理。</summary>
        public static void RemoveConfig(string name) => RemoveConfig(GetConfig(name));

        /// <summary>撤销配表并卸下依赖实例，自有实例归还、外部实例仅解除挂接。</summary>
        /// <remarks>清理错误汇总抛出，已执行的通知和业务变化不回滚。</remarks>
        public static void RemoveConfig(EquipmentsConfig root) => Run(() =>
        {
            var config = _configs.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Root, root));
            if (config == null)
            {
                return;
            }

            _aggregateReturns = true;
            config.IsClosing = true;
            _configs.Remove(config.Name);
            _retiredConfigs.Add(config);
            var errors = new List<Exception>();
            foreach (var record in _records.Values.ToArray())
            {
                Attempt(errors, () =>
                {
                    if (IsCurrent(record) && DependsOn(record, config))
                    {
                        record.IsEquipCancelled = true;
                        UnequipCore(record, true);
                    }
                });
            }

            ThrowErrors(errors, true);
        });

        /// <summary>检查 Asset 地址格式或 Script 构造条件，不预先加载 Prefab 检查组件。</summary>
        private static Type Validate(EquipmentConfigData data)
        {
            if (data.equipmentType == EquipmentType.Asset)
            {
                _ = new ResourceAddress(data.projectName, data.assetName);
                return null;
            }

            var type = AssemblyTool.GetType(data.equipmentClassFullName);
            if (data.equipmentType != EquipmentType.Script || type == null || !type.IsClass || type.IsAbstract ||
                type.ContainsGenericParameters || !typeof(IEquipment).IsAssignableFrom(type) ||
                typeof(UnityEngine.Object).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new InvalidOperationException($"Invalid equipment type: ID={data.id}, mode={data.equipmentType}, class={data.equipmentClassFullName}");
            }

            return type;
        }

        /// <summary>取得未挂接的自有装备并调用 OnLoaded；不使用时须归还，不能继续使用已归还引用。</summary>
        /// <param name="equipmentId">已登记的装备定义 ID；Asset 模式要求当前 ResourceSystem 已初始化。</param>
        /// <returns>引用共享定义的运行实例；创建、组件检查或生命周期失败时抛出异常。</returns>
        public static IEquipment CreateEquipment(int equipmentId) => Run(() =>
        {
            Admit();
            var data = Find(equipmentId, out var config);
            if (!data)
            {
                throw new InvalidOperationException($"Equipment definition not found: {equipmentId}");
            }

            var type = Validate(data);
            var record = new Record
            {
                Config = config,
                IsOwned = true,
                Definition = data,
                DefinitionRoot = config.Root
            };
            try
            {
                if (data.equipmentType == EquipmentType.Asset)
                {
                    var resources = ResourceSystem.Current ??
                        throw new InvalidOperationException("Initialize ResourceSystem before creating Asset equipment.");
                    record.Lease = resources.Instantiate(new ResourceAddress(data.projectName, data.assetName));
                    var components = record.Lease.GameObject.GetComponents<MonoBehaviour>().OfType<IEquipment>().ToArray();
                    if (components.Length != 1)
                    {
                        throw new InvalidOperationException($"Equipment {equipmentId} requires exactly one IEquipment component on its Prefab root.");
                    }

                    record.Value = components[0];
                }
                else
                {
                    if (!_pools.TryGetValue(type, out var pool))
                    {
                        pool = new ObjectPool<IEquipment>(() => (IEquipment)Activator.CreateInstance(type));
                        _pools.Add(type, pool);
                    }

                    record.Pool = pool;
                    record.Value = pool.Allocate();
                }

                _returnedEquipment.Remove(record.Value);
                _records.Add(record.Value, record);
                if (config.IsClosing || _isStopping)
                {
                    throw new InvalidOperationException("Equipment creation was cancelled by domain closure.");
                }

                record.Value.Equipper = null;
                record.Value.EquipmentData = data;
                // 在进入 OnLoaded 前记为已加载，使初始化回调失败时仍能尝试 OnUnloaded 清理。
                record.IsLoaded = true;
                record.Value.OnLoaded();
                if (config.IsClosing || record.IsReleaseRequested || _isStopping || !IsAlive(record.Value))
                {
                    throw new InvalidOperationException("Equipment creation was cancelled during OnLoaded.");
                }

                return record.Value;
            }
            catch (Exception error)
            {
                var errors = new List<Exception> { error };
                if (record.Value != null)
                {
                    record.ShouldDiscard = true;
                    Attempt(errors, () => UnequipCore(record, true));
                }
                else if (record.Lease != null)
                {
                    Attempt(errors, record.Lease.Discard);
                }

                ThrowErrors(errors);
                throw;
            }
        });

        /// <summary>在宿主许可后挂接装备；外部传入且非本管理器创建的实例只借用，不触发加载或卸载钩子。</summary>
        /// <param name="equipper">有效且持有稳定、已初始化装备列表的宿主。</param>
        /// <param name="equipment">有效且未归还的装备实例；同一引用不能同时属于两个宿主。</param>
        /// <returns>回调后仍有效的挂接结果；无效输入、过渡重入或回调异常会抛错，而非返回状态。</returns>
        public static EquipState Equip(IEquippable equipper, IEquipment equipment) => Run(() =>
        {
            Admit();
            if (!IsAlive(equipper))
            {
                throw new ArgumentException("A live equipment host is required.", nameof(equipper));
            }

            if (!IsAlive(equipment))
            {
                throw new ArgumentException("A live equipment instance is required.", nameof(equipment));
            }

            if (_closingHosts.Contains(equipper))
            {
                throw new InvalidOperationException("Equipment host is closing.");
            }

            var list = equipper.Equipments ?? throw new ArgumentException("Initialize IEquippable.Equipments.", nameof(equipper));
            _records.TryGetValue(equipment, out var record);
            if (record != null && (record.IsEquipping || record.IsUnequipping || record.IsReleaseRequested))
            {
                throw new InvalidOperationException("Equipment is inside a lifecycle transition.");
            }

            if (record != null && record.IsAttached || equipment.Equipper != null ||
                list.Any(value => ReferenceEquals(value, equipment)))
            {
                return EquipState.AlreadyEquipped;
            }

            if (_returnedEquipment.TryGetValue(equipment, out _))
            {
                throw new InvalidOperationException("Returned equipment references cannot be equipped.");
            }

            var isExternal = record == null;
            if (isExternal)
            {
                record = new Record { Value = equipment };
            }

            CaptureDefinition(record);
            if (IsDefinitionClosing(record))
            {
                throw new InvalidOperationException("Equipment definition is closing.");
            }

            if (isExternal)
            {
                _records.Add(equipment, record);
            }

            record.IsEquipCancelled = false;
            record.IsEquipping = true;
            record.PendingHost = equipper;
            try
            {
                var canEquip = equipper.CanEquip(equipment);
                if (IsAlive(equipment))
                {
                    CaptureDefinition(record);
                }

                if (!canEquip || record.IsEquipCancelled || record.IsReleaseRequested || _isStopping ||
                    !IsAlive(equipper) || !IsAlive(equipment) || IsDefinitionClosing(record))
                {
                    return EquipState.CannotEquip;
                }

                if (!ReferenceEquals(list, equipper.Equipments))
                {
                    throw new InvalidOperationException("The equipment host replaced its list during Equip.");
                }

                record.Host = equipper;
                record.List = list;
                record.IsAttached = true;
                record.Revision++;
                list.Add(equipment);
                equipment.Equipper = equipper;
                if (!record.IsAttached || record.IsEquipCancelled)
                {
                    return EquipState.CannotEquip;
                }

                // 先标记通知已开始，OnEquip 抛错或重入卸下时也能配对尝试 OnUnequip，而非假定业务效果已回滚。
                record.IsNotified = true;
                equipment.OnEquip();
                return record.IsAttached && !record.IsReleaseRequested && !record.IsEquipCancelled
                    ? EquipState.Success
                    : EquipState.CannotEquip;
            }
            catch (Exception error)
            {
                record.ShouldDiscard = true;
                var errors = new List<Exception> { error };
                Attempt(errors, () => UnequipCore(record, false));
                ThrowErrors(errors);
                throw;
            }
            finally
            {
                record.IsEquipping = false;
                record.PendingHost = null;
                ForgetBorrowed(record);
            }
        });

        /// <summary>创建自有装备并尝试挂接；非成功结果会归还该实例，返回 null。</summary>
        /// <param name="equipper">接收装备的宿主。</param>
        /// <param name="equipmentId">已登记的装备定义 ID。</param>
        /// <returns>成功挂接的实例；许可拒绝或回调取消时返回 null，异常在清理后继续抛出。</returns>
        public static IEquipment CreateAndEquip(IEquippable equipper, int equipmentId) => Run(() =>
        {
            var equipment = CreateEquipment(equipmentId);
            try
            {
                if (Equip(equipper, equipment) == EquipState.Success)
                {
                    return equipment;
                }
            }
            catch (Exception error)
            {
                var errors = new List<Exception> { error };
                Attempt(errors, () => DestroyEquipment(equipment));
                ThrowErrors(errors);
            }

            DestroyEquipment(equipment);
            return null;
        });

        /// <summary>卸下已登记实例，并按选项请求归还；未登记或 null 的实例不处理。</summary>
        /// <param name="equipment">要卸下的运行实例。</param>
        /// <param name="destroy">是否归还自有实例；false 保留它以便再次装备，外部借用实例始终只卸下。</param>
        public static void Unequip(IEquipment equipment, bool destroy = true) => Run(() =>
        {
            if (equipment != null && _records.TryGetValue(equipment, out var record))
            {
                UnequipCore(record, destroy);
            }
        });

        /// <summary>解除当前挂接、配对装备通知，并根据所有权安排延迟归还。</summary>
        private static void UnequipCore(Record record, bool destroy)
        {
            if (!IsCurrent(record))
            {
                return;
            }

            var errors = new List<Exception>();
            if (record.IsAttached)
            {
                // 先撤销内部挂接和列表引用以阻止重复卸下；OnUnequip 仍可读旧 Equipper，随后才清除该属性。
                record.IsAttached = false;
                record.List?.RemoveAll(value => ReferenceEquals(value, record.Value));
                var wasNotified = record.IsNotified;
                record.IsNotified = false;
                record.IsUnequipping = true;
                if (record.IsEquipping)
                {
                    record.IsEquipCancelled = true;
                }

                try
                {
                    if (wasNotified && IsAlive(record.Value))
                    {
                        Attempt(errors, record.Value.OnUnequip);
                    }
                }
                finally
                {
                    if (IsAlive(record.Value))
                    {
                        Attempt(errors, () => record.Value.Equipper = null);
                    }

                    record.Host = null;
                    record.List = null;
                    record.IsUnequipping = false;
                }
            }

            if (errors.Count != 0)
            {
                record.ShouldDiscard = true;
            }

            if (destroy && record.IsOwned && !record.IsReleaseRequested)
            {
                record.IsReleaseRequested = true;
                _pendingReturns.Enqueue(record);
            }

            ForgetBorrowed(record);
            ThrowErrors(errors);
        }

        /// <summary>外部借用实例不再挂接且不处于通知中时，撤销管理记录而不归还或销毁对象。</summary>
        private static void ForgetBorrowed(Record record)
        {
            if (!record.IsOwned && !record.IsAttached && !record.IsEquipping && !record.IsUnequipping && IsCurrent(record))
            {
                _records.Remove(record.Value);
            }
        }

        /// <summary>卸下并归还本管理器创建的实例；已归还引用不重复处理，有效外部对象会被拒绝。</summary>
        public static void DestroyEquipment(IEquipment equipment) => Run(() =>
        {
            if (equipment == null)
            {
                return;
            }

            if (_records.TryGetValue(equipment, out var record) && record.IsOwned)
            {
                UnequipCore(record, true);
                return;
            }

            if (_returnedEquipment.TryGetValue(equipment, out _) || !IsAlive(equipment))
            {
                return;
            }

            throw new InvalidOperationException("Cannot destroy equipment owned by an external caller.");
        });

        /// <summary>按挂接版本快照卸下宿主的已登记装备，继续处理其余项后汇总异常。</summary>
        /// <param name="equipper">待处理的宿主，null 时不处理。</param>
        /// <param name="destroy">是否一并归还自有实例；外部借用实例只卸下。</param>
        public static void UnequipAll(IEquippable equipper, bool destroy = true) => Run(() =>
        {
            if (equipper == null)
            {
                return;
            }

            _aggregateReturns = true;
            var errors = new List<Exception>();
            var records = _records.Values
                .Where(record => ReferenceEquals(record.Host, equipper))
                .Select(record => (Record: record, record.Revision))
                .ToArray();
            foreach (var snapshot in records)
            {
                if (IsCurrent(snapshot.Record) && snapshot.Record.Revision == snapshot.Revision &&
                    ReferenceEquals(snapshot.Record.Host, equipper))
                {
                    Attempt(errors, () => UnequipCore(snapshot.Record, destroy));
                }
            }

            if (IsAlive(equipper))
            {
                equipper.Equipments?.RemoveAll(value => !IsAlive(value));
            }

            ThrowErrors(errors, true);
        });

        /// <summary>取消宿主正在进行的装备请求并卸下当前装备，在本轮延迟收尾结束前拒绝再次向它装备。</summary>
        /// <remarks>自有且已挂接的实例会安排归还，外部实例不转移销毁责任；本方法不会销毁宿主。</remarks>
        public static void CloseHost(IEquippable equipper) => Run(() =>
        {
            if (equipper == null || !_closingHosts.Add(equipper))
            {
                return;
            }

            _aggregateReturns = true;
            var errors = new List<Exception>();
            foreach (var snapshot in _records.Values
                .Where(record =>
                    ReferenceEquals(record.Host, equipper) || ReferenceEquals(record.PendingHost, equipper))
                .Select(record => (Record: record, record.Revision))
                .ToArray())
            {
                var record = snapshot.Record;
                if (!IsCurrent(record) || record.Revision != snapshot.Revision ||
                    !(ReferenceEquals(record.Host, equipper) || ReferenceEquals(record.PendingHost, equipper)))
                {
                    continue;
                }

                record.IsEquipCancelled = true;
                if (record.IsAttached || record.IsUnequipping)
                {
                    Attempt(errors, () => UnequipCore(record, true));
                }
            }

            if (IsAlive(equipper))
            {
                equipper.Equipments?.RemoveAll(value => !IsAlive(value));
            }

            ThrowErrors(errors, true);
        });

        /// <summary>撤销全部配表并收尾装备、对象池及所持租约；逐项清理后汇总错误，不回滚业务副作用。</summary>
        public static void Shutdown() => Run(() =>
        {
            if (_isStopping)
            {
                return;
            }

            _isStopping = true;
            _aggregateReturns = true;
            foreach (var config in _configs.Values)
            {
                config.IsClosing = true;
                _retiredConfigs.Add(config);
            }

            _configs.Clear();
            var errors = new List<Exception>();
            foreach (var record in _records.Values.ToArray())
            {
                record.IsEquipCancelled = true;
                Attempt(errors, () => UnequipCore(record, true));
            }

            foreach (var pool in _pools.Values)
            {
                Attempt(errors, pool.Dispose);
            }

            _pools.Clear();
            ThrowErrors(errors, true);
        });

        /// <summary>所有外层回调退出后才真正归还，防止旧回调继续访问已被下一次创建复用的实例。</summary>
        private static void Flush()
        {
            _isFlushing = true;
            _depth++;
            var errors = new List<Exception>();
            var aggregate = false;
            try
            {
                while (_pendingReturns.Count != 0 || _retiredConfigs.Count != 0)
                {
                    while (_pendingReturns.Count != 0)
                    {
                        var record = _pendingReturns.Dequeue();
                        if (record.IsReturning)
                        {
                            continue;
                        }

                        record.IsReturning = true;
                        var cleanup = new List<Exception>();
                        if (record.IsLoaded && IsAlive(record.Value))
                        {
                            Attempt(cleanup, record.Value.OnUnloaded);
                        }

                        if (IsAlive(record.Value))
                        {
                            Attempt(cleanup, () => record.Value.Equipper = null);
                            Attempt(cleanup, () => record.Value.EquipmentData = null);
                        }

                        _records.Remove(record.Value);
                        _returnedEquipment.Remove(record.Value);
                        _returnedEquipment.Add(record.Value, _returnedMarker);
                        var shouldDiscard = record.ShouldDiscard || cleanup.Count != 0;
                        if (record.Lease != null)
                        {
                            Attempt(cleanup, () =>
                            {
                                if (shouldDiscard)
                                {
                                    record.Lease.Discard();
                                }
                                else
                                {
                                    record.Lease.Return();
                                }
                            });
                        }

                        if (record.Pool != null)
                        {
                            Attempt(cleanup, () =>
                            {
                                if (shouldDiscard)
                                {
                                    record.Pool.Discard(record.Value);
                                }
                                else
                                {
                                    record.Pool.Recycle(record.Value);
                                }
                            });
                        }

                        errors.AddRange(cleanup);
                    }

                    if (_retiredConfigs.Count != 0)
                    {
                        var config = _retiredConfigs[0];
                        try
                        {
                            if (config.Lease != null)
                            {
                                Attempt(errors, config.Lease.Dispose);
                            }
                        }
                        finally
                        {
                            config.Lease = null;
                            _retiredConfigs.Remove(config);
                        }
                    }
                }
            }
            finally
            {
                aggregate = _aggregateReturns;
                _aggregateReturns = false;
                _depth--;
                _isFlushing = false;
                _isStopping = false;
                _closingHosts.Clear();
            }

            ThrowErrors(errors, aggregate);
        }
    }
}
