using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.DataAssets
{
    /// <summary>按根名称/hash查询的显式入口，不扫描资产、不自动加载、不接管其他系统配置。</summary>
    public static class ScriptableObjectManager
    {
        private static readonly DataAssetRegistry<ScriptableObjectConfigs> _registry = new DataAssetRegistry<ScriptableObjectConfigs>(
            root =>
            {
                if (root)
                {
                    root.ClearCache();
                }
            });
        private static readonly HashSet<ScriptableObjectRegistration> _registrations = new HashSet<ScriptableObjectRegistration>();

        /// <summary>当前有效owner登记数（同一根的两个owner计两次），用于定位未归还责任。</summary>
        public static int RegistrationCount
        {
            get
            {
                PruneInvalid();
                return _registrations.Count;
            }
        }

        /// <summary>借用已加载根；返回值归调用owner Dispose。多个owner可登记同一根。</summary>
        /// <param name="config">由外部负责保活的共享配置根。</param>
        public static ScriptableObjectRegistration AddConfig(ScriptableObjectConfigs config) => Register(config, null, null);

        /// <summary>先校验根内 ID 和名称身份，再建立独立登记及可选的资源释放责任。</summary>
        private static ScriptableObjectRegistration Register(ScriptableObjectConfigs config, Func<bool> valid, Action release)
        {
            if (!config)
            {
                throw new ArgumentNullException(nameof(config));
            }

            PruneInvalid();
            config.TryGetIdRange(out _, out _);
            var key = config.name;
            var entry = _registry.Register(key, StringToHash(key), config);
            var owner = new ScriptableObjectRegistration(config, key, entry, valid, release);
            try
            {
                DataAssetLifetime.Track(owner);
                _registrations.Add(owner);
            }
            catch
            {
                entry.Dispose();
                throw;
            }
            return owner;
        }

        /// <summary>同步取得根Lease并登记；登记失败自动归还Lease。</summary>
        /// <param name="resource">负责取得根资产的资源系统。</param>
        /// <param name="address">根资产在资源目录中的地址。</param>
        /// <returns>拥有本次租约的登记，由调用方释放。</returns>
        public static ScriptableObjectRegistration Load(ResourceSystem resource, ResourceAddress address)
        {
            if (resource == null)
            {
                throw new ArgumentNullException(nameof(resource));
            }

            var generation = DataAssetLifetime.Generation;
            var lease = resource.Acquire<ScriptableObjectConfigs>(address);
            try
            {
                if (generation != DataAssetLifetime.Generation)
                {
                    throw new OperationCanceledException("DataAssets closed during the synchronous load.");
                }

                return Register(lease.Asset, () => lease.IsValid, lease.Dispose);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        /// <summary>通过资源请求取得根，成功后登记；调用方持有返回句柄至借读结束。</summary>
        /// <param name="resource">负责取得根资产的资源系统。</param>
        /// <param name="address">根资产在资源目录中的地址。</param>
        /// <returns>拥有本消费者请求、成功租约及关联登记的句柄。</returns>
        public static ScriptableObjectAsset<ScriptableObjectConfigs> LoadAsync(ResourceSystem resource, ResourceAddress address)
        {
            ScriptableObjectRegistration registration = null;
            var owner = new ScriptableObjectAsset<ScriptableObjectConfigs>();
            owner.Start(resource, address,
                root => registration = Register(root, () => owner.IsValid, owner.Dispose),
                () => registration?.Dispose());
            return owner;
        }

        /// <summary>按登记时的根名称借读配置，清除失效登记后查询；未找到时为空。</summary>
        /// <param name="name">按 Ordinal 比较的资产对象名，不是显示名称。</param>
        public static ScriptableObjectConfigs GetConfig(string name)
        {
            PruneInvalid();
            var root = _registry.Get(name);
            return root ? root : null;
        }

        /// <summary>按已登记名称的哈希借读配置，未找到时为空；登记阶段会拒绝哈希冲突。</summary>
        /// <param name="nameHash">由 StringToHash 计算的根名称哈希。</param>
        public static ScriptableObjectConfigs GetConfig(int nameHash)
        {
            PruneInvalid();
            var root = _registry.Get(nameHash);
            return root ? root : null;
        }

        /// <summary>将非空根名称转换为登记使用的 Animator 字符串哈希。</summary>
        /// <param name="value">根的资产对象名。</param>
        public static int StringToHash(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("A root name is required.", nameof(value));
            }

            return Animator.StringToHash(value);
        }

        /// <summary>按根引用释放全部登记及各自资源责任，不直接销毁传入对象。</summary>
        public static void RemoveConfig(ScriptableObjectConfigs config)
        {
            foreach (var owner in new List<ScriptableObjectRegistration>(_registrations))
            {
                if (ReferenceEquals(owner.Root, config))
                {
                    owner.Dispose();
                }
            }
        }

        /// <summary>释放登记名称精确匹配的全部登记，使用登记时的名称而非根当前名称。</summary>
        public static void RemoveConfig(string name)
        {
            foreach (var owner in new List<ScriptableObjectRegistration>(_registrations))
            {
                if (StringComparer.Ordinal.Equals(owner.Name, name))
                {
                    owner.Dispose();
                }
            }
        }

        /// <summary>按名称哈希定位有效根，再释放该根的全部登记。</summary>
        public static void RemoveConfig(int nameHash)
        {
            PruneInvalid();
            var root = _registry.Get(nameHash);
            if (root)
            {
                RemoveConfig(root);
            }
        }

        /// <summary>先收集再释放失效登记，避免释放回调修改正在遍历的集合。</summary>
        private static void PruneInvalid()
        {
            List<ScriptableObjectRegistration> invalid = null;
            foreach (var owner in _registrations)
            {
                if (!owner.IsValid)
                {
                    (invalid ??= new List<ScriptableObjectRegistration>()).Add(owner);
                }
            }

            if (invalid != null)
            {
                foreach (var owner in invalid)
                {
                    owner.Dispose();
                }
            }
        }

        /// <summary>关闭本域全部配置加载/登记（包含Singleton配置），不关闭ResourceSystem。</summary>
        public static void Clear() => DataAssetLifetime.Clear();

        /// <summary>从管理器的登记集合移除已结束责任的句柄，不重复执行释放。</summary>
        internal static void Forget(ScriptableObjectRegistration owner) => _registrations.Remove(owner);
    }

    /// <summary>配置根的一份独立登记；只释放自身登记及绑定租约，配置对象本身供借读。</summary>
    public sealed class ScriptableObjectRegistration : IDisposable
    {
        private IDisposable _entry;
        private Func<bool> _valid;
        private Action _release;
        /// <summary>本登记引用的配置根，释放时清空。</summary>
        internal ScriptableObjectConfigs Root
        {
            get; private set;
        }
        /// <summary>登记时固定的根名称，不随资产对象改名自动变化。</summary>
        internal string Name
        {
            get;
        }
        /// <summary>本登记的释放责任是否已经结束。</summary>
        public bool IsDisposed => _entry == null;
        /// <summary>登记未结束、根仍存活且可选租约校验通过时为真。</summary>
        public bool IsValid => !IsDisposed && Root && (_valid == null || _valid());
        /// <summary>借读有效登记的共享根；登记或底层租约失效时抛出异常。</summary>
        public ScriptableObjectConfigs Config => IsValid ? Root : throw new InvalidOperationException("The DataAssets registration is no longer valid.");

        /// <summary>绑定根身份、底层登记和可选租约校验及释放回调。</summary>
        internal ScriptableObjectRegistration(ScriptableObjectConfigs root, string name, IDisposable entry, Func<bool> valid, Action release)
        {
            Root = root;
            Name = name;
            this._entry = entry;
            this._valid = valid;
            this._release = release;
        }

        /// <summary>先结束自身可见状态，再移除底层登记并执行资源释放回调；可重复调用。</summary>
        public void Dispose()
        {
            if (_entry == null)
            {
                return;
            }

            var owned = _entry;
            var onRelease = _release;
            _entry = null;
            _valid = null;
            _release = null;
            ScriptableObjectManager.Forget(this);
            DataAssetLifetime.Untrack(this);
            Root = null;
            try
            {
                owned.Dispose();
            }
            finally
            {
                onRelease?.Invoke();
            }
        }
    }
}
