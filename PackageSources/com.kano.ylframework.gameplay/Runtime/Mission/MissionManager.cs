using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Pooling;
using YLFramework.Singletons;
using YLFramework.Foundation;
using YLFramework.Resource;

namespace YLFramework.Mission
{
    /// <summary>保留多配表、参数约束、任务状态与奖励配置；不拥有存档权威。</summary>
    /// <remarks>登记时复制任务定义和参数配置，运行值不回写源资产；奖励仅供业务读取，不在此发放。</remarks>
    public static class MissionManager
    {
        /// <summary>一张配表的源资产、运行实例、参数引用关系及可选资源租约。</summary>
        private sealed class RuntimeConfig
        {
            /// <summary>登记时的配表名称。</summary>
            internal string Name;
            /// <summary>只作为定义来源保留的配表资产。</summary>
            internal MissionsConfig Source;
            /// <summary>由 LoadConfig 取得、随配表释放的资源租约。</summary>
            internal IDisposable Lease;
            /// <summary>以任务 ID 索引的运行实例。</summary>
            internal readonly Dictionary<int, MissionBase> Missions = new();
            /// <summary>以名称哈希索引的独立运行参数。</summary>
            internal readonly Dictionary<int, MissionParameter> Parameters = new();
            /// <summary>参数到引用任务的关系，用于写入约束和完成后重置判定。</summary>
            internal readonly Dictionary<int, List<MissionBase>> References = new();
        }
        private static readonly Dictionary<int, RuntimeConfig> _configs = new();
        private static readonly Dictionary<int, string> _keys = new();
        private static readonly Dictionary<Type, ObjectPool<MissionBase>> _pools = new();
        private static bool _isStopping;
        /// <summary>当前已登记的任务配表数量。</summary>
        public static int ConfigCount => _configs.Count;
        /// <summary>登记执行会话重置与应用退出时的统一清理入口。</summary>
        static MissionManager()
        {
            SingletonSessionState.RegisterSessionReset(Shutdown);
            Application.quitting += Shutdown;
        }

        /// <summary>确定性的Ordinal FNV-1a；不是旧Animator hash/存档ID。注册时显式拒绝碰撞。</summary>
        public static int StringToHash(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            unchecked
            {
                var hash = 2166136261u;
                foreach (var value in text)
                {
                    hash ^= value;
                    hash *= 16777619u;
                }
                return (int)hash;
            }
        }
        /// <summary>记录非空名称与哈希的对应关系，拒绝不同名称占用同一哈希。</summary>
        private static int RememberKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("任务配表/参数名称不能为空。", nameof(text));
            }

            var key = StringToHash(text);
            if (_keys.TryGetValue(key, out var existing) && !string.Equals(existing, text, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"任务键碰撞: '{existing}' / '{text}', hash={key}。请更名，不能混用状态。");
            }

            _keys[key] = text;
            return key;
        }
        /// <summary>按名称查找运行配表，同时核对原始名称以避免哈希误匹配。</summary>
        private static RuntimeConfig GetRuntime(string name)
        {
            if (name == null || !_configs.TryGetValue(StringToHash(name), out var state) || !string.Equals(state.Name, name, StringComparison.Ordinal))
            {
                return null;
            }

            return state;
        }
        /// <summary>按已登记的配表名称哈希查找运行配表。</summary>
        private static RuntimeConfig GetRuntime(int hash) => _configs.TryGetValue(hash, out var state) ? state : null;

        /// <summary>校验并登记源配表，为参数和任务配置创建独立运行副本，并从类型池取得任务实例。</summary>
        /// <remarks>不接管调用方的资源租约；失败会尝试释放已创建的运行内容，不保证撤销构造器等业务副作用。</remarks>
        public static void AddConfig(MissionsConfig source)
        {
            if (!source)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (_isStopping)
            {
                throw new InvalidOperationException("任务会话正在退出。");
            }

            var key = RememberKey(source.name);
            if (_configs.ContainsKey(key))
            {
                throw new InvalidOperationException($"任务配表重复: {source.name}");
            }

            var state = new RuntimeConfig { Name = source.name, Source = source };
            _configs.Add(key, state);
            try
            {
                foreach (var parameter in source.parameters)
                {
                    if (parameter == null)
                    {
                        throw new InvalidOperationException($"任务参数为空: {source.name}");
                    }

                    var id = RememberKey(parameter.name);
                    if (state.Parameters.ContainsKey(id))
                    {
                        throw new InvalidOperationException($"任务参数名称重复: {source.name}/{parameter.name}");
                    }

                    state.Parameters.Add(id, parameter.CloneConfiguration());
                    state.References.Add(id, new List<MissionBase>());
                }
                foreach (var data in source.missions)
                {
                    if (!data)
                    {
                        throw new InvalidOperationException($"任务条目为空: {source.name}");
                    }

                    if (state.Missions.ContainsKey(data.id))
                    {
                        throw new InvalidOperationException($"任务ID重复: {source.name}/{data.id}");
                    }

                    foreach (var condition in data.conditions)
                    {
                        if (condition == null || !state.Parameters.TryGetValue(StringToHash(condition.paramName ?? string.Empty), out var parameter) || !string.Equals(parameter.name, condition.paramName, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException($"任务条件参数不存在: 配表={source.name}, ID={data.id}, 参数={condition?.paramName}");
                        }
                    }

                    var type = string.IsNullOrWhiteSpace(data.missionTypeClassFullName) ? null : AssemblyTool.GetType(data.missionTypeClassFullName);
                    if (type == null || type.IsAbstract || !typeof(MissionBase).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        throw new InvalidOperationException($"任务类型无效: 配表={source.name}, ID={data.id}, class={data.missionTypeClassFullName}");
                    }

                    if (!_pools.TryGetValue(type, out var pool))
                    {
                        pool = new ObjectPool<MissionBase>(() => (MissionBase)Activator.CreateInstance(type), reset: mission => mission.Reset());
                        _pools.Add(type, pool);
                    }
                    var mission = pool.Allocate();
                    try
                    {
                        mission.MissionConfig = UnityEngine.Object.Instantiate(data);
                        mission.MissionConfig.parent = source;
                        mission.ConfigNameHash = key;
                        state.Missions.Add(data.id, mission);
                        mission.Init();
                        foreach (var condition in mission.MissionConfig.conditions)
                        {
                            var references = state.References[condition.ParamNameHash];
                            if (!references.Contains(mission))
                            {
                                references.Add(mission);
                            }
                        }
                    }
                    catch
                    {
                        if (!state.Missions.ContainsKey(data.id))
                        {
                            pool.Recycle(mission);
                        }

                        throw;
                    }
                }
            }
            catch
            {
                _configs.Remove(key);
                try
                {
                    Release(state);
                }
                finally
                {
                    RebuildKeys();
                }
                throw;
            }
        }

        /// <summary>取得并登记任务配表，成功后由管理器持有资源租约，失败时释放本次租约。</summary>
        public static void LoadConfig(ResourceSystem resources, ResourceAddress address)
        {
            var lease = resources.Acquire<MissionsConfig>(address);
            try
            {
                AddConfig(lease.Asset);
                GetRuntime(lease.Asset.name).Lease = lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        /// <summary>按登记名称取得源配表资产；未登记时为 null，不返回运行副本。</summary>
        public static MissionsConfig GetConfig(string name) => GetRuntime(name)?.Source;
        /// <summary>按名称哈希取得源配表资产；未登记时为 null。</summary>
        public static MissionsConfig GetConfig(int hash) => GetRuntime(hash)?.Source;
        /// <summary>移除指定名称的配表，清理运行实例、参数订阅与管理器持有的租约。</summary>
        public static void RemoveConfig(string name)
        {
            var state = GetRuntime(name);
            if (state != null)
            {
                RemoveConfig(StringToHash(state.Name));
            }
        }
        /// <summary>按名称哈希先取消配表登记，再释放其运行内容并重建名称索引。</summary>
        public static void RemoveConfig(int hash)
        {
            if (_configs.TryGetValue(hash, out var state))
            {
                _configs.Remove(hash);
                try
                {
                    Release(state);
                }
                finally
                {
                    RebuildKeys();
                }
            }
        }

        /// <summary>依据仍登记的配表和参数重建名称索引，移除已释放配置占用的键。</summary>
        private static void RebuildKeys()
        {
            _keys.Clear();
            foreach (var state in _configs.Values)
            {
                RememberKey(state.Name);
                foreach (var parameter in state.Parameters.Values)
                {
                    RememberKey(parameter.name);
                }
            }
        }

        /// <summary>解除监听并归还任务实例，销毁任务配置副本，清理参数与配表租约。</summary>
        private static void Release(RuntimeConfig state)
        {
            foreach (var mission in state.Missions.Values)
            {
                var data = mission.MissionConfig;
                mission.RemoveListener();
                if (data)
                {
                    foreach (var condition in data.conditions)
                    {
                        condition.Dispose();
                    }
                }

                _pools[mission.GetType()].Recycle(mission);
                mission.MissionConfig = null;
                mission.UserData = null;
                if (data)
                {
                    UnityEngine.Object.Destroy(data);
                }
            }
            foreach (var parameter in state.Parameters.Values)
            {
                parameter.Reset();
            }

            state.Missions.Clear();
            state.Parameters.Clear();
            state.References.Clear();
            state.Lease?.Dispose();
            state.Lease = null;
        }

        /// <summary>按配表名称和任务 ID 取得运行实例；未找到时为 null，移除配表后不可继续持有使用。</summary>
        public static MissionBase GetMission(string config, int id) => GetMission(GetRuntime(config), id);
        /// <summary>按配表名称哈希和任务 ID 取得运行实例；未找到时为 null。</summary>
        public static MissionBase GetMission(int config, int id) => GetMission(GetRuntime(config), id);
        /// <summary>从运行配表中查找任务，不存在时返回 null。</summary>
        private static MissionBase GetMission(RuntimeConfig state, int id) => state != null && state.Missions.TryGetValue(id, out var mission) ? mission : null;
        /// <summary>按配表名称复制任务引用列表；列表独立，内部任务仍是管理器持有的实例。</summary>
        public static List<MissionBase> GetAllMissions(string config) => GetRuntime(config) is RuntimeConfig state ? new List<MissionBase>(state.Missions.Values) : new List<MissionBase>();
        /// <summary>按配表名称哈希复制任务引用列表；未登记时返回空列表。</summary>
        public static List<MissionBase> GetAllMissions(int config) => GetRuntime(config) is RuntimeConfig state ? new List<MissionBase>(state.Missions.Values) : new List<MissionBase>();
        /// <summary>清空调用方列表，再按配表名称填入任务引用；不复制任务实例。</summary>
        public static void GetAllMissionsNonAlloc(string config, List<MissionBase> results)
        {
            results.Clear();
            if (GetRuntime(config) is RuntimeConfig state)
            {
                results.AddRange(state.Missions.Values);
            }
        }
        /// <summary>清空调用方列表，再按配表名称哈希填入任务引用。</summary>
        public static void GetAllMissionsNonAlloc(int config, List<MissionBase> results)
        {
            results.Clear();
            if (GetRuntime(config) is RuntimeConfig state)
            {
                results.AddRange(state.Missions.Values);
            }
        }
        /// <summary>按配表名称复制运行参数引用列表；直接改写其中参数会绕过管理器的写入约束。</summary>
        public static List<MissionParameter> GetAllParameters(string config) => GetRuntime(config) is RuntimeConfig state ? new List<MissionParameter>(state.Parameters.Values) : new List<MissionParameter>();
        /// <summary>按配表名称哈希复制运行参数引用列表；未登记时返回空列表。</summary>
        public static List<MissionParameter> GetAllParameters(int config) => GetRuntime(config) is RuntimeConfig state ? new List<MissionParameter>(state.Parameters.Values) : new List<MissionParameter>();
        /// <summary>清空调用方列表，再按配表名称填入运行参数引用。</summary>
        public static void GetAllParametersNonAlloc(string config, List<MissionParameter> results)
        {
            results.Clear();
            if (GetRuntime(config) is RuntimeConfig state)
            {
                results.AddRange(state.Parameters.Values);
            }
        }
        /// <summary>清空调用方列表，再按配表名称哈希填入运行参数引用。</summary>
        public static void GetAllParametersNonAlloc(int config, List<MissionParameter> results)
        {
            results.Clear();
            if (GetRuntime(config) is RuntimeConfig state)
            {
                results.AddRange(state.Parameters.Values);
            }
        }
        /// <summary>复制同配表中引用指定参数的任务列表，供完成后的参数重置检查使用。</summary>
        internal static List<MissionBase> GetParamRefMission(int config, int parameter) => GetRuntime(config) is RuntimeConfig state && state.References.TryGetValue(parameter, out var list) ? new List<MissionBase>(list) : new List<MissionBase>();
        /// <summary>按配表和参数的名称哈希取得运行参数。</summary>
        internal static MissionParameter GetMissionParam(int config, int parameter) => GetRuntime(config) is RuntimeConfig state && state.Parameters.TryGetValue(parameter, out var value) ? value : null;
        /// <summary>按原始名称核对并查找运行参数，缺少配表或参数时返回 null。</summary>
        private static MissionParameter GetMissionParam(string config, string parameter)
        {
            var state = GetRuntime(config);
            if (state == null || parameter == null || !state.Parameters.TryGetValue(StringToHash(parameter), out var value) || !string.Equals(value.name, parameter, StringComparison.Ordinal))
            {
                return null;
            }

            return value;
        }
        /// <summary>校验参数存在及类型，应用执行中和增大值限制后同步写入运行值。</summary>
        private static void Set(MissionParameter parameter, RuntimeConfig state, MissionParamType type, float value)
        {
            if (parameter == null || state == null)
            {
                throw new InvalidOperationException("任务配表/参数尚未登记或已经移除。");
            }

            if (parameter.type != type)
            {
                throw new InvalidOperationException($"任务参数类型不匹配: {state.Name}/{parameter.name}, actual={parameter.type}, requested={type}");
            }

            var references = state.References[StringToHash(parameter.name)];
            if (parameter.onlySetInProgress && !references.Exists(mission => mission.State == MissionState.InProgress))
            {
                return;
            }

            if (!parameter.onlySetLarger || value > parameter.Value)
            {
                parameter.Value = value;
            }
        }
        /// <summary>按名称将布尔参数写为 0 或 1；缺项或类型不符时抛错，写入仍受参数配置限制。</summary>
        public static void SetBool(string config, string parameter, bool value) => Set(GetMissionParam(config, parameter), GetRuntime(config), MissionParamType.Bool, value ? 1 : 0);
        /// <summary>按名称写入整数参数，应用参数配置的写入限制并同步更新条件。</summary>
        public static void SetInt(string config, string parameter, int value) => Set(GetMissionParam(config, parameter), GetRuntime(config), MissionParamType.Int, value);
        /// <summary>按名称写入浮点参数，应用参数配置的写入限制并同步更新条件。</summary>
        public static void SetFloat(string config, string parameter, float value) => Set(GetMissionParam(config, parameter), GetRuntime(config), MissionParamType.Float, value);
        /// <summary>按名称哈希写入布尔参数，遵循与名称入口相同的校验和写入限制。</summary>
        public static void SetBool(int config, int parameter, bool value) => Set(GetMissionParam(config, parameter), GetRuntime(config), MissionParamType.Bool, value ? 1 : 0);
        /// <summary>按名称哈希写入整数参数，遵循参数配置的写入限制。</summary>
        public static void SetInt(int config, int parameter, int value) => Set(GetMissionParam(config, parameter), GetRuntime(config), MissionParamType.Int, value);
        /// <summary>按名称哈希写入浮点参数，遵循参数配置的写入限制。</summary>
        public static void SetFloat(int config, int parameter, float value) => Set(GetMissionParam(config, parameter), GetRuntime(config), MissionParamType.Float, value);
        /// <summary>仅在参数存在且类型匹配时读取值，否则返回 0。</summary>
        private static float Read(MissionParameter parameter, MissionParamType type) => parameter != null && parameter.type == type ? parameter.Value : 0;
        /// <summary>按名称读取布尔参数，仅值恰为 1 时返回 true；缺项或类型不符时为 false。</summary>
        public static bool GetBool(string config, string parameter) => Read(GetMissionParam(config, parameter), MissionParamType.Bool) == 1;
        /// <summary>按名称读取整数参数，使用 Math.Round 后转换；缺项或类型不符时为 0。</summary>
        public static int GetInt(string config, string parameter) => (int)Math.Round(Read(GetMissionParam(config, parameter), MissionParamType.Int));
        /// <summary>按名称读取浮点参数；缺项或类型不符时为 0。</summary>
        public static float GetFloat(string config, string parameter) => Read(GetMissionParam(config, parameter), MissionParamType.Float);
        /// <summary>按名称哈希读取布尔参数，仅值恰为 1 时返回 true。</summary>
        public static bool GetBool(int config, int parameter) => Read(GetMissionParam(config, parameter), MissionParamType.Bool) == 1;
        /// <summary>按名称哈希读取整数参数，使用 Math.Round 后转换；缺项或类型不符时为 0。</summary>
        public static int GetInt(int config, int parameter) => (int)Math.Round(Read(GetMissionParam(config, parameter), MissionParamType.Int));
        /// <summary>按名称哈希读取浮点参数；缺项或类型不符时为 0。</summary>
        public static float GetFloat(int config, int parameter) => Read(GetMissionParam(config, parameter), MissionParamType.Float);
        /// <summary>按名称读取整数参数后加上增量，再通过受配置约束的写入入口保存。</summary>
        public static void AddInt(string config, string parameter, int value) => SetInt(config, parameter, GetInt(config, parameter) + value);
        /// <summary>按名称读取浮点参数后加上增量，再通过受配置约束的写入入口保存。</summary>
        public static void AddFloat(string config, string parameter, float value) => SetFloat(config, parameter, GetFloat(config, parameter) + value);
        /// <summary>按名称哈希累加整数参数，写回时仍受执行中和增大值限制。</summary>
        public static void AddInt(int config, int parameter, int value) => SetInt(config, parameter, GetInt(config, parameter) + value);
        /// <summary>按名称哈希累加浮点参数，写回时仍受执行中和增大值限制。</summary>
        public static void AddFloat(int config, int parameter, float value) => SetFloat(config, parameter, GetFloat(config, parameter) + value);

        /// <summary>重置全部已登记配表的运行状态，不移除配表或释放其资源租约。</summary>
        public static void Reset()
        {
            foreach (var key in new List<int>(_configs.Keys))
            {
                Reset(key);
            }
        }
        /// <summary>按名称重置任务状态、运行参数及全部订阅，随后重新绑定条件。</summary>
        public static void Reset(string name)
        {
            if (GetRuntime(name) is RuntimeConfig state)
            {
                Reset(StringToHash(state.Name));
            }
        }
        /// <summary>按名称哈希重置任务和参数并重绑条件；业务订阅和任务自定义数据不会保留。</summary>
        public static void Reset(int hash)
        {
            var state = GetRuntime(hash);
            if (state == null)
            {
                return;
            }

            foreach (var mission in state.Missions.Values)
            {
                mission.Reset();
            }

            foreach (var parameter in state.Parameters.Values)
            {
                parameter.Reset();
            }

            foreach (var mission in state.Missions.Values)
            {
                mission.Init();
            }
        }
        /// <summary>移除所有任务配表并释放类型池和名称索引，作为执行会话与应用退出的清理入口。</summary>
        public static void Shutdown()
        {
            if (_isStopping)
            {
                return;
            }

            _isStopping = true;
            try
            {
                foreach (var key in new List<int>(_configs.Keys))
                {
                    RemoveConfig(key);
                }

                foreach (var pool in _pools.Values)
                {
                    pool.Dispose();
                }

                _pools.Clear();
                _keys.Clear();
            }
            finally
            {
                _isStopping = false;
            }
        }
    }
}
