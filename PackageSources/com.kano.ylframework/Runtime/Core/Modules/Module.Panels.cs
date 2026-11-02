using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Core
{
    public abstract partial class Module
    {
        internal PanelRuleSnapshot PanelRules = PanelRuleSnapshot.Empty;

        /// <summary>之后接受的 Panel 请求的默认根。null 清除绑定；已销毁的绑定不会回退到宿主默认根。</summary>
        public UIRootContext PanelRoot
        {
            get; set;
        }

        /// <summary>完整复制并校验已解析规则后原子替换。枚举或校验失败保留旧快照；null/空集合清除可选规则。</summary>
        public void BindPanelRules(IEnumerable<PanelRule> rules) => PanelRules = PanelRuleSnapshot.Capture(rules);

        /// <summary>仅以 typeof(T).Name 作为地址打开 Panel。无需窗口目录，但 Core provider 的资源目录必须精确登记该地址或显式别名；不猜完整路径或文件名。</summary>
        public T LoadPanel<T>(LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default) where T : Panel
            => LoadPanel<T>(typeof(T).Name, loadViewArgs, root, overrides);
        /// <summary>按完整资源地址打开或有效重开 Panel，参数由本次请求接管并归还。</summary>
        public T LoadPanel<T>(string assetName, LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default) where T : Panel
            => PanelManager.Load<T>(this, assetName, null, loadViewArgs, root, overrides);
        /// <summary>直接 Prefab 打开，不依赖资源 provider；与解析到同一 Prefab 的命名请求共享单实例范围。</summary>
        public T LoadPanel<T>(GameObject prefab, LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default) where T : Panel
            => PanelManager.Load<T>(this, null, prefab, loadViewArgs, root, overrides);
        /// <summary>非泛型完整地址入口，返回实际根 Panel。</summary>
        public Panel LoadPanel(string assetName, LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default)
            => LoadPanel<Panel>(assetName, loadViewArgs, root, overrides);
        /// <summary>非泛型直接 Prefab 入口，返回实际根 Panel。</summary>
        public Panel LoadPanel(GameObject prefab, LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default)
            => LoadPanel<Panel>(prefab, loadViewArgs, root, overrides);

        /// <summary>仅以 typeof(T).Name 为资源地址异步打开，须由 provider 精确登记；接受后先 Pending，最早下一帧执行数据回调，不扫描或猜路径。</summary>
        public LoadPanelRequest<T> LoadPanelAsync<T>(LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default) where T : Panel
            => LoadPanelAsync<T>(typeof(T).Name, loadViewArgs, root, overrides);
        /// <summary>按完整地址异步打开；每次调用有独立终态、参数与提交前 Abort 权限。</summary>
        public LoadPanelRequest<T> LoadPanelAsync<T>(string assetName, LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default) where T : Panel
            => PanelManager.Start<T>(this, assetName, null, loadViewArgs, root, overrides);
        /// <summary>直接 Prefab 异步打开，缓存/已有单实例也遵守下一帧协议。</summary>
        public LoadPanelRequest<T> LoadPanelAsync<T>(GameObject prefab, LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default) where T : Panel
            => PanelManager.Start<T>(this, null, prefab, loadViewArgs, root, overrides);
        /// <summary>非泛型命名异步入口。</summary>
        public LoadPanelRequest<Panel> LoadPanelAsync(string assetName, LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default)
            => LoadPanelAsync<Panel>(assetName, loadViewArgs, root, overrides);
        /// <summary>非泛型直接 Prefab 异步入口。</summary>
        public LoadPanelRequest<Panel> LoadPanelAsync(GameObject prefab, LoadViewArgs loadViewArgs = null, UIRootContext root = null, PanelOverrides overrides = default)
            => LoadPanelAsync<Panel>(prefab, loadViewArgs, root, overrides);
    }
}
