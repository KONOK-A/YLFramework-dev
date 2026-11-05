using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Pooling;
using YLFramework.Singletons;
using YLFramework.Events;
using YLFramework.Foundation;
using YLFramework.Resource;
using YLFramework.Update;

namespace YLFramework.Buff
{
    /// <summary>保留Single/Additive、时长与效果查询。宿主拥有Buff集合，退出显式CloseHost。</summary>
    public static class BuffManager
    {
        private static readonly List<BuffsConfig> _configs = new();
        private static readonly Dictionary<BuffsConfig, IDisposable> _leases = new();
        private static readonly List<IAddableBuff> _hosts = new();
        private static readonly Dictionary<IAddableBuff, HashSet<int>> _effects = new();
        private static readonly HashSet<IAddableBuff> _closing = new();
        private static readonly HashSet<(IAddableBuff, int)> _adding = new();
        private static readonly HashSet<BuffsConfig> _removingConfigs = new();
        private static readonly Dictionary<IAddableBuff, int> _addingDepth = new();
        private static readonly HashSet<IAddableBuff> _cancelledAdds = new();
        private static readonly Dictionary<Type, ObjectPool<BuffBase>> _pools = new();
        private static readonly List<(BuffBase buff, ObjectPool<BuffBase> pool)> _pending = new();
        private static IUnregister _update;
        private static IUnregister _fixed;
        private static IUnregister _late;
        private static int _depth;
        private static bool _isStopping;
        /// <summary>当前已登记的 Buff 配表数量。</summary>
        public static int ConfigCount => _configs.Count;
        /// <summary>当前参与管理的 Buff 宿主数量。</summary>
        public static int HostCount => _hosts.Count;
        /// <summary>当前保有效果 ID 索引的宿主数量，不是效果总数。</summary>
        public static int EffectOwnerCount => _effects.Count;

        /// <summary>注册执行会话重置与应用退出时的 Buff 清理入口。</summary>
        static BuffManager()
        {
            SingletonSessionState.RegisterSessionReset(Shutdown);
            Application.quitting += Shutdown;
        }

        /// <summary>校验并登记配表，不创建 Buff；直接登记的资产由调用方保持有效。</summary>
        /// <param name="config">定义、效果引用及 Buff 类型均有效，且 ID 不与其他已登记项重复的配表。</param>
        public static void AddConfig(BuffsConfig config)
        {
            if (!config)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (_isStopping || _removingConfigs.Contains(config))
            {
                throw new InvalidOperationException("Buff配表正在关闭。");
            }

            if (_configs.Contains(config))
            {
                return;
            }

            var ids = new HashSet<int>();
            foreach (var data in config.buffs)
            {
                if (!data)
                {
                    throw new InvalidOperationException($"Buff配表存在空项: {config.name}");
                }

                if (!ids.Add(data.id) || GetBuffData(data.id) != null)
                {
                    throw new InvalidOperationException($"Buff ID重复: {data.id}, 配表={config.name}");
                }

                ResolveType(data.buffClassFullName, data.id, config.name);
                for (var i = 0; i < data.effects.Count; i++)
                {
                    if (!data.effects[i])
                    {
                        throw new InvalidOperationException($"Buff效果缺失: ID={data.id}, 配表={config.name}, effect[{i}]");
                    }
                }
            }
            _configs.Add(config);
        }

        /// <summary>同步取得并登记配表，持有其资源租约直到移除配表或关闭管理器。</summary>
        /// <param name="resources">提供配表资产的资源系统。</param>
        /// <param name="address">Buff 配表的正式资源地址。</param>
        public static void LoadConfig(ResourceSystem resources, ResourceAddress address)
        {
            var lease = resources.Acquire<BuffsConfig>(address);
            try
            {
                AddConfig(lease.Asset);
                if (_leases.ContainsKey(lease.Asset))
                {
                    lease.Dispose();
                }
                else
                {
                    _leases.Add(lease.Asset, lease);
                }
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        /// <summary>按资产名称查找已登记配表，未找到时返回 null。</summary>
        public static BuffsConfig GetConfig(string name) => _configs.Find(config => config && config.name == name);
        /// <summary>撤销配表并移除依赖它的运行实例，最后释放本管理器持有的租约。</summary>
        /// <remarks>逐宿主收集移除异常后汇总抛出；不恢复已经移除的 Buff 或回调产生的业务变化。</remarks>
        public static void RemoveConfig(BuffsConfig config)
        {
            if (!config || !_configs.Contains(config) && !_leases.ContainsKey(config) || !_removingConfigs.Add(config))
            {
                return;
            }

            _configs.Remove(config);
            var errors = new List<Exception>();
            _depth++;
            try
            {
                foreach (var host in _hosts.ToArray())
                {
                    try
                    {
                        RemoveMatching(host, buff => buff.BuffData && buff.BuffData.parent == config);
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }
            }
            finally
            {
                try
                {
                    if (--_depth == 0)
                    {
                        FlushReturns();
                    }
                }
                finally
                {
                    if (_leases.TryGetValue(config, out var lease))
                    {
                        _leases.Remove(config);
                        try
                        {
                            lease.Dispose();
                        }
                        catch (Exception error)
                        {
                            errors.Add(error);
                        }
                    }
                    _removingConfigs.Remove(config);
                }
            }
            if (errors.Count != 0)
            {
                throw new AggregateException(errors);
            }
        }
        /// <summary>从已登记配表中查找共享定义，未命中时返回 null。</summary>
        private static BuffConfigData GetBuffData(int id)
        {
            foreach (var config in _configs)
            {
                foreach (var data in config.buffs)
                {
                    if (data && data.id == id)
                    {
                        return data;
                    }
                }
            }

            return null;
        }
        /// <summary>解析可构造的 BuffBase 具体类型，并在失败信息中附带定义来源。</summary>
        private static Type ResolveType(string name, int id, string config)
        {
            var type = AssemblyTool.GetType(name);
            if (type == null || type.IsAbstract || !typeof(BuffBase).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new InvalidOperationException($"Buff类型无效: ID={id}, 配表={config}, class={name}");
            }

            return type;
        }

        /// <summary>使用已登记定义的默认持续时间添加 Buff；本重载先查定义，再进入宿主检查。</summary>
        /// <param name="host">持有已初始化 Buff 列表的宿主。</param>
        /// <param name="id">已登记的 Buff ID。</param>
        /// <param name="userData">传给本次实例的业务数据，管理器不负责释放。</param>
        /// <returns>完成添加且回调后仍有效的实例；许可拒绝、宿主无效或添加中被移除时返回 null。</returns>
        public static BuffBase AddBuff(IAddableBuff host, int id, object userData = null)
        {
            var data = GetBuffData(id);
            if (!data)
            {
                throw new InvalidOperationException($"Buff配置不存在: {id}");
            }

            return AddBuff(host, id, data.duration, userData);
        }
        /// <summary>按指定持续时间添加独立实例；同宿主同 ID 的嵌套添加会抛错。</summary>
        /// <param name="host">持有已初始化列表的宿主；null、已销毁或层级停用的组件宿主不添加。</param>
        /// <param name="id">已登记的 Buff ID。</param>
        /// <param name="duration">本次持续秒数；小于等于零时不按时间自动移除。</param>
        /// <param name="userData">传给本次实例的业务数据，管理器不负责释放。</param>
        /// <returns>回调后仍挂接的实例；被拒绝或中途失效时返回 null，异常继续向调用方传播。</returns>
        public static BuffBase AddBuff(IAddableBuff host, int id, float duration, object userData = null)
        {
            if (host == null)
            {
                return null;
            }

            if (host.Buffs == null)
            {
                throw new ArgumentException("请初始化IAddableBuff.Buffs。", nameof(host));
            }

            if (_isStopping || _closing.Contains(host) || _cancelledAdds.Contains(host))
            {
                throw new InvalidOperationException("Buff宿主正在退出。");
            }

            if (host is UnityEngine.Object obj && !obj || host is Component component && !component.gameObject.activeInHierarchy)
            {
                return null;
            }

            if (!_adding.Add((host, id)))
            {
                throw new InvalidOperationException($"Buff同宿主同ID重入添加: {id}");
            }

            BeginAdd(host);
            _depth++;
            BuffBase buff = null;
            ObjectPool<BuffBase> pool = null;
            try
            {
                if (!host.CanAdd(id) || _cancelledAdds.Contains(host))
                {
                    return null;
                }

                var definition = GetBuffData(id);
                if (!definition)
                {
                    throw new InvalidOperationException($"Buff配置不存在: {id}");
                }

                var owner = _configs.Find(config => config.buffs.Contains(definition));
                var type = ResolveType(definition.buffClassFullName, id, definition.parent ? definition.parent.name : "(unparented)");
                if (!_pools.TryGetValue(type, out pool))
                {
                    pool = new ObjectPool<BuffBase>(() => (BuffBase)Activator.CreateInstance(type), reset: value => value.ClearRuntime());
                    _pools.Add(type, pool);
                }
                buff = pool.Allocate();
                if (_isStopping || _cancelledAdds.Contains(host) || !_configs.Contains(owner))
                {
                    _pending.Add((buff, pool));
                    return null;
                }
                buff.BuffData = UnityEngine.Object.Instantiate(definition);
                buff.BuffData.parent = owner;
                buff.BuffData.effects = new List<EffectData>();
                foreach (var effect in definition.effects)
                {
                    buff.BuffData.effects.Add(UnityEngine.Object.Instantiate(effect));
                }

                buff.ResetData();
                buff.Owner = host;
                buff.Duration = duration;
                buff.UserData = userData;
                if (_isStopping || _cancelledAdds.Contains(host) || !_configs.Contains(owner))
                {
                    _pending.Add((buff, pool));
                    return null;
                }
                if (!buff.CanAdd())
                {
                    _pending.Add((buff, pool));
                    return null;
                }
                if (definition.mode == BuffMode.Single)
                {
                    RemoveBuff(host, id);
                }

                if (_isStopping || _closing.Contains(host) || _cancelledAdds.Contains(host) || !_configs.Contains(owner))
                {
                    _pending.Add((buff, pool));
                    return null;
                }
                host.Buffs.Add(buff);
                if (!_hosts.Contains(host))
                {
                    _hosts.Add(host);
                }

                UpdateEffects(host);
                EnsureUpdates();
                // 先提交列表与效果索引，回调才能查询当前状态；OnAdd 可移除自身，版本变化后不再通知宿主添加成功。
                var version = buff.Version;
                buff.OnAdd();
                if (version == buff.Version && host.Buffs.Contains(buff))
                {
                    host.OnAddBuff(buff);
                }

                return version == buff.Version && host.Buffs.Contains(buff) ? buff : null;
            }
            catch
            {
                if (buff != null && !_pending.Exists(entry => ReferenceEquals(entry.buff, buff)))
                {
                    host.Buffs.Remove(buff);
                    UpdateEffects(host);
                    _pending.Add((buff, pool));
                }
                throw;
            }
            finally
            {
                _adding.Remove((host, id));
                EndAdd(host);
                if (--_depth == 0)
                {
                    FlushReturns();
                }
            }
        }
        /// <summary>按 ID 快照依次使用各定义的默认时长添加；失败不回滚此前已添加的实例。</summary>
        /// <param name="host">接收 Buff 的宿主。</param>
        /// <param name="ids">要添加的 ID 列表，按原顺序处理并保留重复项。</param>
        /// <param name="userData">各次添加共用的业务数据引用。</param>
        public static void AddBuff(IAddableBuff host, IList<int> ids, object userData = null)
        {
            AddBatch(host, ids, id => AddBuff(host, id, userData));
        }
        /// <summary>按 ID 快照依次以统一时长添加；关闭宿主会停止本批余下的添加。</summary>
        /// <param name="host">接收 Buff 的宿主。</param>
        /// <param name="ids">要添加的 ID 列表，按原顺序处理并保留重复项。</param>
        /// <param name="duration">每个实例的持续秒数。</param>
        /// <param name="userData">各次添加共用的业务数据引用。</param>
        public static void AddBuff(IAddableBuff host, IList<int> ids, float duration, object userData = null)
        {
            AddBatch(host, ids, id => AddBuff(host, id, duration, userData));
        }

        /// <summary>记录宿主的嵌套添加层级，以便关闭宿主时取消尚未完成的添加。</summary>
        private static void BeginAdd(IAddableBuff host)
        {
            _addingDepth.TryGetValue(host, out var depth);
            _addingDepth[host] = depth + 1;
        }
        /// <summary>退出一层添加，最外层退出后清除该宿主的临时取消标记。</summary>
        private static void EndAdd(IAddableBuff host)
        {
            if (_addingDepth[host] == 1)
            {
                _addingDepth.Remove(host);
                _cancelledAdds.Remove(host);
            }
            else
            {
                _addingDepth[host]--;
            }
        }
        /// <summary>在统一的添加层级内遍历 ID 快照，并将对象归还延迟到整批退出。</summary>
        private static void AddBatch(IAddableBuff host, IList<int> ids, Action<int> add)
        {
            if (host == null)
            {
                return;
            }

            BeginAdd(host);
            _depth++;
            try
            {
                foreach (var id in new List<int>(ids))
                {
                    if (_cancelledAdds.Contains(host))
                    {
                        break;
                    }
                    add(id);
                }
            }
            finally
            {
                EndAdd(host);
                if (--_depth == 0)
                {
                    FlushReturns();
                }
            }
        }

        /// <summary>移除属于该宿主的指定实例并发送移除通知，无效或已移除的实例不重复处理。</summary>
        /// <param name="host">实例当前所属的宿主。</param>
        /// <param name="buff">要移除的运行实例，不是配表定义。</param>
        public static void RemoveBuff(IAddableBuff host, BuffBase buff)
        {
            if (host?.Buffs == null || buff == null || !ReferenceEquals(buff.Owner, host) || !host.Buffs.Remove(buff))
            {
                return;
            }

            // 先出列表、失效旧版本并刷新效果，再通知 Buff 与宿主；即使前者抛错，finally 仍调用宿主通知并安排归还。
            buff.Detach();
            UpdateEffects(host);
            _depth++;
            try
            {
                try
                {
                    buff.OnRemove();
                }
                finally
                {
                    host.OnRemoveBuff(buff);
                }
            }
            finally
            {
                _pending.Add((buff, _pools[buff.GetType()]));
                if (--_depth == 0)
                {
                    FlushReturns();
                }
            }
        }
        /// <summary>按给定实例引用集合移除匹配项；逐项收集异常后汇总抛出。</summary>
        public static void RemoveBuff(IAddableBuff host, IList<BuffBase> buffs)
        {
            var selected = new List<BuffBase>(buffs);
            RemoveMatching(host, buff => selected.Exists(value => ReferenceEquals(value, buff)));
        }
        /// <summary>移除宿主当前快照内所有同 ID 实例，而不是只减一层。</summary>
        public static void RemoveBuff(IAddableBuff host, int id)
        {
            RemoveMatching(host, buff => buff.BuffData && buff.BuffData.id == id);
        }
        /// <summary>移除运行类型与指定类型完全相同的实例，不包含派生类型。</summary>
        public static void RemoveBuff(IAddableBuff host, Type type)
        {
            RemoveMatching(host, buff => buff.GetType() == type);
        }
        /// <summary>移除可赋值为指定 Buff 类型的实例，包含派生类型。</summary>
        /// <typeparam name="T">要移除的 Buff 类型。</typeparam>
        /// <param name="host">待处理的宿主。</param>
        public static void RemoveBuff<T>(IAddableBuff host) where T : BuffBase
        {
            RemoveMatching(host, buff => buff is T);
        }

        /// <summary>按实例与版本快照移除匹配 Buff，跳过回调已移除或替换的项并汇总异常。</summary>
        private static void RemoveMatching(IAddableBuff host, Predicate<BuffBase> predicate)
        {
            if (host?.Buffs == null)
            {
                return;
            }

            var snapshot = new List<(BuffBase buff, int version)>();
            foreach (var buff in host.Buffs)
            {
                snapshot.Add((buff, buff.Version));
            }

            var errors = new List<Exception>();
            _depth++;
            try
            {
                foreach (var entry in snapshot)
                {
                    if (entry.version != entry.buff.Version || !ReferenceEquals(entry.buff.Owner, host) || !host.Buffs.Contains(entry.buff))
                    {
                        continue;
                    }

                    try
                    {
                        if (predicate(entry.buff))
                        {
                            RemoveBuff(host, entry.buff);
                        }
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }
            }
            finally
            {
                if (--_depth == 0)
                {
                    FlushReturns();
                }
            }
            if (errors.Count != 0)
            {
                throw new AggregateException(errors);
            }
        }
        /// <summary>移除宿主的全部 Buff、取消进行中的添加并撤销索引；宿主结束时由业务显式调用。</summary>
        public static void CloseHost(IAddableBuff host)
        {
            if (host == null || !_closing.Add(host))
            {
                return;
            }

            if (_addingDepth.ContainsKey(host))
            {
                _cancelledAdds.Add(host);
            }

            try
            {
                RemoveMatching(host, _ => true);
            }
            finally
            {
                _effects.Remove(host);
                _hosts.Remove(host);
                _closing.Remove(host);
                ReleaseIdleUpdates();
            }
        }

        /// <summary>查询宿主的效果 ID 索引是否包含指定效果；多个实例的相同效果只记存在性。</summary>
        public static bool ContainsEffect(IAddableBuff host, int id) => host != null && _effects.TryGetValue(host, out var effects) && effects.Contains(id);
        /// <summary>先清空结果列表，再收集指定 C# 类型的运行效果引用，不去重或复制。</summary>
        /// <typeparam name="T">效果配置类型，包含其派生类型。</typeparam>
        /// <param name="host">要查询的宿主。</param>
        /// <param name="results">接收运行效果的非 null 列表；引用只在所属 Buff 有效期间使用。</param>
        public static void GetEffects<T>(IAddableBuff host, List<T> results) where T : EffectData => QueryEffects(host, results, null, null);
        /// <summary>先清空结果列表，再收集同时匹配 C# 类型与效果 ID 的运行效果引用。</summary>
        /// <typeparam name="T">效果配置类型，包含其派生类型。</typeparam>
        /// <param name="host">要查询的宿主。</param>
        /// <param name="results">接收运行效果的非 null 列表，不去重或复制。</param>
        /// <param name="id">要匹配的效果 ID。</param>
        public static void GetEffects<T>(IAddableBuff host, List<T> results, int id) where T : EffectData => QueryEffects(host, results, id, null);
        /// <summary>先清空结果列表，再收集匹配 C# 类型与业务分类编号的运行效果引用。</summary>
        /// <typeparam name="T">效果配置类型，包含其派生类型。</typeparam>
        /// <param name="host">要查询的宿主。</param>
        /// <param name="results">接收运行效果的非 null 列表，不去重或复制。</param>
        /// <param name="type">EffectData.type 的业务分类编号，不是 C# 类型。</param>
        public static void GetEffectsByType<T>(IAddableBuff host, List<T> results, int type) where T : EffectData => QueryEffects(host, results, null, type);
        /// <summary>遍历宿主当前 Buff 的效果副本，按可选 ID 与分类过滤后写入结果列表。</summary>
        private static void QueryEffects<T>(IAddableBuff host, List<T> results, int? id, int? type) where T : EffectData
        {
            if (results == null)
            {
                throw new ArgumentNullException(nameof(results));
            }

            results.Clear();
            if (host?.Buffs == null)
            {
                return;
            }

            foreach (var buff in host.Buffs)
            {
                foreach (var effect in buff.BuffData.effects)
                {
                    if (effect is T value && (!id.HasValue || effect.id == id) && (!type.HasValue || effect.type == type))
                    {
                        results.Add(value);
                    }
                }
            }
        }
        /// <summary>按当前 Buff 列表重建效果 ID 集合，最后一个 Buff 移除后撤销宿主索引。</summary>
        private static void UpdateEffects(IAddableBuff host)
        {
            if (host.Buffs.Count == 0)
            {
                _effects.Remove(host);
                _hosts.Remove(host);
                return;
            }
            if (!_effects.TryGetValue(host, out var effects))
            {
                effects = new HashSet<int>();
                _effects.Add(host, effects);
            }
            effects.Clear();
            foreach (var buff in host.Buffs)
            {
                foreach (var effect in buff.BuffData.effects)
                {
                    effects.Add(effect.id);
                }
            }
        }
        /// <summary>首次有 Buff 时订阅三阶段更新，避免为每个实例分别注册。</summary>
        private static void EnsureUpdates()
        {
            if (_update != null)
            {
                return;
            }

            _update = UpdateManager.AddUpdateEvent(() => Dispatch(0));
            _fixed = UpdateManager.AddFixedUpdateEvent(() => Dispatch(1));
            _late = UpdateManager.AddLateUpdateEvent(() => Dispatch(2));
        }
        /// <summary>按宿主与实例版本快照分发更新，跳过停用宿主并在 Update 后移除到期实例。</summary>
        private static void Dispatch(int phase)
        {
            if (_isStopping)
            {
                return;
            }

            _depth++;
            try
            {
                foreach (var host in _hosts.ToArray())
                {
                    if (host is UnityEngine.Object obj && !obj)
                    {
                        CloseHost(host);
                        continue;
                    }
                    if (host is Component component && !component.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    var snapshot = new List<(BuffBase buff, int version)>();
                    foreach (var buff in host.Buffs)
                    {
                        snapshot.Add((buff, buff.Version));
                    }

                    foreach (var entry in snapshot)
                    {
                        var buff = entry.buff;
                        if (_isStopping || entry.version != buff.Version || !host.Buffs.Contains(buff))
                        {
                            continue;
                        }

                        if (phase == 0)
                        {
                            buff.Update();
                            if (entry.version == buff.Version && buff.Progress >= 1)
                            {
                                RemoveBuff(host, buff);
                            }
                        }
                        else if (phase == 1)
                        {
                            buff.FixedUpdate();
                        }
                        else
                        {
                            buff.OnLateUpdate();
                        }
                    }
                }
            }
            finally
            {
                if (--_depth == 0)
                {
                    FlushReturns();
                }
            }
        }
        /// <summary>最外层操作退出后归还待回收实例，避免回调尚未结束就复用同一对象。</summary>
        private static void FlushReturns()
        {
            foreach (var entry in _pending)
            {
                // Recycle 会清空 BuffData，因此先保留本次副本以销毁效果和配置，不销毁配表中的定义。
                var data = entry.buff.BuffData;
                entry.pool.Recycle(entry.buff);
                if (data)
                {
                    foreach (var effect in data.effects)
                    {
                        if (effect)
                        {
                            UnityEngine.Object.Destroy(effect);
                        }
                    }

                    UnityEngine.Object.Destroy(data);
                }
            }
            _pending.Clear();
            ReleaseIdleUpdates();
            if (_isStopping)
            {
                foreach (var pool in _pools.Values)
                {
                    pool.Dispose();
                }

                _pools.Clear();
                foreach (var lease in _leases.Values)
                {
                    lease.Dispose();
                }

                _leases.Clear();
                _configs.Clear();
                _effects.Clear();
                _isStopping = false;
            }
        }
        /// <summary>没有宿主时撤销三个更新订阅。</summary>
        private static void ReleaseIdleUpdates()
        {
            if (_hosts.Count != 0)
            {
                return;
            }

            _update?.Unregister();
            _fixed?.Unregister();
            _late?.Unregister();
            _update = _fixed = _late = null;
        }
        /// <summary>关闭全部宿主并释放对象池与所持配表租约；若在回调中调用，归还延迟到外层退出。</summary>
        /// <remarks>宿主移除异常会汇总抛出，已执行的生命周期与业务副作用不会回滚。</remarks>
        public static void Shutdown()
        {
            if (_isStopping)
            {
                return;
            }

            _isStopping = true;
            _depth++;
            var errors = new List<Exception>();
            try
            {
                foreach (var host in _hosts.ToArray())
                {
                    try
                    {
                        CloseHost(host);
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }
            }
            finally
            {
                if (--_depth == 0)
                {
                    FlushReturns();
                }
            }
            if (errors.Count != 0)
            {
                throw new AggregateException(errors);
            }
        }
    }
}
