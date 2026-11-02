using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Pooling;
using YLFramework.Singletons;
using YLFramework.Events;
using YLFramework.Foundation;
using YLFramework.Resource;
using YLFramework.Update;

namespace YLFramework.Skill
{
    /// <summary>保留技能装备/释放流程。CLR宿主退出显式CloseHost，整个会话由Shutdown归还。</summary>
    /// <remarks>源配表只提供定义；每次装备分配池化技能及配置副本，并在移除时统一清理。批量操作不保证原子回滚。</remarks>
    public static class SkillManager
    {
        private static readonly Dictionary<string, SkillsConfig> _configs = new(StringComparer.Ordinal);
        private static readonly Dictionary<SkillsConfig, IDisposable> _leases = new();
        private static readonly List<IEquippableSkill> _hosts = new();
        private static readonly HashSet<IEquippableSkill> _closing = new();
        private static readonly HashSet<SkillsConfig> _removingConfigs = new();
        private static readonly Dictionary<IEquippableSkill, int> _addingDepth = new();
        private static readonly HashSet<IEquippableSkill> _cancelledAdds = new();
        private static readonly Dictionary<Type, ObjectPool<SkillBase>> _pools = new();
        private static readonly List<(SkillBase skill, ObjectPool<SkillBase> pool)> _pending = new();
        private static IUnregister _update;
        private static IUnregister _fixed;
        private static IUnregister _late;
        // 管理器的遍历和增删流程嵌套时延后归还，防止同一派发快照中的旧实例过早复用。
        private static int _depth;
        private static bool _isStopping;
        /// <summary>已登记的技能源配表数量。</summary>
        public static int ConfigCount => _configs.Count;
        /// <summary>当前纳入更新派发的宿主数量，不包含尚未装备技能的宿主。</summary>
        public static int HostCount => _hosts.Count;

        /// <summary>登记执行会话重置和应用退出时的统一清理入口。</summary>
        static SkillManager()
        {
            SingletonSessionState.RegisterSessionReset(Shutdown);
            Application.quitting += Shutdown;
        }

        /// <summary>校验技能 ID 和运行类型后登记源配表；同一资产重复登记无操作，不同同名资产会抛错。</summary>
        /// <remarks>本入口不创建技能实例，也不接管调用方的资源租约。</remarks>
        public static void AddConfig(SkillsConfig config)
        {
            if (!config)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (_isStopping || _removingConfigs.Contains(config))
            {
                throw new InvalidOperationException("技能配表正在关闭。");
            }

            if (_configs.TryGetValue(config.name, out var existing))
            {
                if (existing == config)
                {
                    return;
                }

                throw new InvalidOperationException($"技能配表名称重复: {config.name}");
            }
            var ids = new HashSet<int>();
            foreach (var data in config.skills)
            {
                if (!data)
                {
                    throw new InvalidOperationException($"技能配表 {config.name} 存在空条目。");
                }

                if (!ids.Add(data.id) || GetSkillInfo(data.id) != null)
                {
                    throw new InvalidOperationException($"技能ID重复: {data.id}, 配表: {config.name}");
                }

                ResolveType(data.skillClassFullName, data.id, config.name);
            }
            _configs.Add(config.name, config);
        }

        /// <summary>Acquire的根Lease由Manager接管，直到RemoveConfig或Shutdown。</summary>
        /// <remarks>同一资产已有管理器租约时释放本次取得的租约；登记失败也释放本次租约。</remarks>
        public static void LoadConfig(ResourceSystem resources, ResourceAddress address)
        {
            var lease = resources.Acquire<SkillsConfig>(address);
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

        /// <summary>按登记时的资产名称取得源配表；未登记或名称为 null 时返回 null。</summary>
        public static SkillsConfig GetConfig(string name) => name != null && _configs.TryGetValue(name, out var config) ? config : null;
        /// <summary>先取消配表登记，再移除各宿主中归属该配表的技能，并释放管理器持有的资源租约。</summary>
        /// <remarks>逐宿主收集移除异常后继续清理，最后汇总抛出；不会恢复已移除的技能。</remarks>
        public static void RemoveConfig(SkillsConfig config)
        {
            if (!config)
            {
                return;
            }

            string key = null;
            foreach (var pair in _configs)
            {
                if (ReferenceEquals(pair.Value, config))
                {
                    key = pair.Key;
                    break;
                }
            }

            if (key == null && !_leases.ContainsKey(config) || !_removingConfigs.Add(config))
            {
                return;
            }

            if (key != null)
            {
                _configs.Remove(key);
            }

            var errors = new List<Exception>();
            _depth++;
            try
            {
                foreach (var host in _hosts.ToArray())
                {
                    try
                    {
                        ForEachOwnedSkill(host, (id, skill) =>
                        {
                            if (skill.SkillData && skill.SkillData.parent == config)
                            {
                                RemoveSkills(host, id);
                            }
                        });
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

        /// <summary>遍历已登记配表，取得指定 ID 的源技能定义。</summary>
        private static SkillConfigData GetSkillInfo(int id)
        {
            foreach (var config in _configs.Values)
            {
                foreach (var data in config.skills)
                {
                    if (data && data.id == id)
                    {
                        return data;
                    }
                }
            }

            return null;
        }

        /// <summary>解析技能类型并要求其为带公共无参构造器的非抽象 SkillBase 派生类。</summary>
        private static Type ResolveType(string name, int id, string config)
        {
            var type = AssemblyTool.GetType(name);
            if (type == null || type.IsAbstract || !typeof(SkillBase).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new InvalidOperationException($"技能类型无效: ID={id}, 配表={config}, class={name}");
            }

            return type;
        }

        /// <summary>为宿主装备技能，建立独立配置副本和运行状态，登记字典与更新后调用 OnAdd。</summary>
        /// <returns>仍在宿主字典中的本次实例；添加期间被关闭或移除时可返回 null。</returns>
        /// <remarks>宿主须预先初始化 Skills；失败会清理本次实例，但不撤销构造器或 OnAdd 的外部业务副作用。</remarks>
        public static SkillBase AddSkills(IEquippableSkill host, int skillId)
        {
            if (host == null || host.Skills == null)
            {
                throw new ArgumentException("请初始化IEquippableSkill.Skills。", nameof(host));
            }

            if (_isStopping || _closing.Contains(host) || _cancelledAdds.Contains(host))
            {
                throw new InvalidOperationException("技能宿主正在退出。");
            }

            if (host.Skills.ContainsKey(skillId))
            {
                throw new InvalidOperationException($"技能已装备: {skillId}");
            }

            var definition = GetSkillInfo(skillId);
            if (!definition)
            {
                throw new InvalidOperationException($"技能配置不存在: {skillId}");
            }

            SkillsConfig owner = null;
            foreach (var config in _configs.Values)
            {
                if (config.skills.Contains(definition))
                {
                    owner = config;
                    break;
                }
            }

            var type = ResolveType(definition.skillClassFullName, skillId, definition.parent ? definition.parent.name : "(unparented)");
            if (!_pools.TryGetValue(type, out var pool))
            {
                pool = new ObjectPool<SkillBase>(() => (SkillBase)Activator.CreateInstance(type), reset: skill => skill.ClearRuntime());
                _pools.Add(type, pool);
            }
            BeginAdd(host);
            SkillBase skill = null;
            _depth++;
            try
            {
                skill = pool.Allocate();
                if (_cancelledAdds.Contains(host) || !_configs.ContainsValue(owner))
                {
                    _pending.Add((skill, pool));
                    return null;
                }
                skill.SkillData = UnityEngine.Object.Instantiate(definition);
                skill.SkillData.parent = owner;
                skill.Releaser = host;
                skill.ResetData();
                if (_isStopping || _cancelledAdds.Contains(host) || !_configs.ContainsValue(owner))
                {
                    _pending.Add((skill, pool));
                    return null;
                }
                host.Skills.Add(skillId, skill);
                if (!_hosts.Contains(host))
                {
                    _hosts.Add(host);
                }

                EnsureUpdates();
                skill.OnAdd();
                return host.Skills.TryGetValue(skillId, out var current) && ReferenceEquals(current, skill) ? skill : null;
            }
            catch
            {
                if (skill != null)
                {
                    if (host.Skills.TryGetValue(skillId, out var current) && ReferenceEquals(current, skill))
                    {
                        host.Skills.Remove(skillId);
                    }

                    if (!_pending.Exists(entry => ReferenceEquals(entry.skill, skill)))
                    {
                        _pending.Add((skill, pool));
                    }
                }
                if (host.Skills.Count == 0)
                {
                    _hosts.Remove(host);
                }

                throw;
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

        /// <summary>按输入 ID 快照逐项装备；中途异常不会撤销已成功项，关闭宿主会停止剩余添加。</summary>
        public static void AddSkills(IEquippableSkill host, IList<int> skills)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            BeginAdd(host);
            _depth++;
            try
            {
                foreach (var id in new List<int>(skills))
                {
                    if (_cancelledAdds.Contains(host))
                    {
                        break;
                    }
                    AddSkills(host, id);
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
        /// <summary>按宿主实例快照移除选中 ID，跳过回调中已失效或被替换的实例，并汇总逐项异常。</summary>
        public static void RemoveSkills(IEquippableSkill host, IList<int> skills)
        {
            var selected = new HashSet<int>(skills);
            ForEachOwnedSkill(host, (id, _) =>
            {
                if (selected.Contains(id))
                {
                    RemoveSkills(host, id);
                }
            });
        }

        /// <summary>记录宿主的嵌套添加深度，让关闭流程能够取消当前添加链。</summary>
        private static void BeginAdd(IEquippableSkill host)
        {
            _addingDepth.TryGetValue(host, out var depth);
            _addingDepth[host] = depth + 1;
        }
        /// <summary>退出一层宿主添加流程，在最外层退出时清除该宿主的添加取消标记。</summary>
        private static void EndAdd(IEquippableSkill host)
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

        /// <summary>按 ID、对象和版本快照遍历仍归属宿主的技能，逐项收集异常并延后归还实例。</summary>
        private static void ForEachOwnedSkill(IEquippableSkill host, Action<int, SkillBase> action)
        {
            if (host?.Skills == null)
            {
                return;
            }

            var snapshot = new List<(int id, SkillBase skill, int version)>();
            foreach (var pair in host.Skills)
            {
                snapshot.Add((pair.Key, pair.Value, pair.Value.Version));
            }

            var errors = new List<Exception>();
            _depth++;
            try
            {
                foreach (var entry in snapshot)
                {
                    if (entry.version != entry.skill.Version || !ReferenceEquals(entry.skill.Releaser, host) ||
                        !host.Skills.TryGetValue(entry.id, out var current) || !ReferenceEquals(current, entry.skill))
                    {
                        continue;
                    }

                    try
                    {
                        action(entry.id, entry.skill);
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

        /// <summary>先从宿主字典摘除技能并标记移除，再尝试中断、调用 OnRemove，最后安排归还池。</summary>
        /// <remarks>即使中断抛错也尝试 OnRemove；移除后不应继续持有实例，因为它可能被其他装备请求复用。</remarks>
        public static void RemoveSkills(IEquippableSkill host, int id)
        {
            if (host == null || host.Skills == null || !host.Skills.TryGetValue(id, out var skill))
            {
                return;
            }

            host.Skills.Remove(id);
            if (host.Skills.Count == 0)
            {
                _hosts.Remove(host);
            }

            skill.BeginRemove();
            _depth++;
            try
            {
                try
                {
                    skill.Interrupt();
                }
                finally
                {
                    skill.OnRemove();
                }
            }
            finally
            {
                _pending.Add((skill, _pools[skill.GetType()]));
                if (--_depth == 0)
                {
                    FlushReturns();
                }
            }
        }

        /// <summary>关闭宿主当前技能，取消该宿主尚在进行的添加链，并在无宿主时解除更新订阅。</summary>
        /// <remarks>可供普通 CLR 宿主显式结束使用；不永久封禁宿主，关闭流程结束后仍可重新装备。</remarks>
        public static void CloseHost(IEquippableSkill host)
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
                ForEachOwnedSkill(host, (id, _) => RemoveSkills(host, id));
            }
            finally
            {
                _hosts.Remove(host);
                _closing.Remove(host);
                ReleaseIdleUpdates();
            }
        }

        /// <summary>中断快照中仍归属宿主的技能，不卸下技能；逐项异常在遍历后汇总抛出。</summary>
        public static void InterruptAllSkill(IEquippableSkill host)
        {
            ForEachOwnedSkill(host, (_, skill) => skill.Interrupt());
        }
        /// <summary>查询宿主字典中是否有释放中的技能；宿主或字典为空时为 false。</summary>
        public static bool IsAnySkillReleasing(IEquippableSkill host)
        {
            if (host?.Skills != null)
            {
                foreach (var skill in host.Skills.Values)
                {
                    if (skill.IsReleasing)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>首次装备时订阅帧更新、固定步和延迟更新的统一派发。</summary>
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

        /// <summary>按宿主和技能快照派发指定阶段，清理已销毁宿主，跳过未激活的 Component 宿主及失效实例。</summary>
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

                    var snapshot = new List<(SkillBase skill, int version)>();
                    foreach (var skill in host.Skills.Values)
                    {
                        snapshot.Add((skill, skill.Version));
                    }

                    foreach (var entry in snapshot)
                    {
                        if (_isStopping || !entry.skill.IsAttached || entry.version != entry.skill.Version)
                        {
                            continue;
                        }

                        if (phase == 0)
                        {
                            entry.skill.Update();
                        }
                        else if (entry.skill.IsReleasing && phase == 1)
                        {
                            entry.skill.FixedUpdate();
                        }
                        else if (entry.skill.IsReleasing)
                        {
                            entry.skill.OnLateUpdate();
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

        /// <summary>归还待回收实例并销毁配置副本；会话停止时进一步释放池、租约与配置登记。</summary>
        private static void FlushReturns()
        {
            foreach (var entry in _pending)
            {
                var data = entry.skill.SkillData;
                entry.pool.Recycle(entry.skill);
                if (data)
                {
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
                _isStopping = false;
            }
        }

        /// <summary>没有登记宿主时归还三阶段更新订阅。</summary>
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

        /// <summary>关闭全部已登记宿主并请求释放整个技能会话；嵌套调用中会延后到最外层退出时完成归还。</summary>
        /// <remarks>逐宿主收集关闭异常，清理后汇总抛出；不承诺撤销业务回调或恢复先前状态。</remarks>
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
