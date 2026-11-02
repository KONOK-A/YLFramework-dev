using System;
using UnityEngine;
using YLFramework.Core;
using YLFramework.Singletons;

namespace YLFramework
{
    /// <summary>框架的共享宿主，承载模块层级，并在需要 UI 时准备默认 UI 根。</summary>
    /// <remarks>
    /// 通过继承的 Instance 访问宿主；取得模块层级并不等于启动模块，也不会顺带创建 UI。
    /// 默认 UI 根要求场景中只有一个宿主，重复摆放宿主不会在此自动合并或销毁。
    /// </remarks>
    // ReSharper disable once InconsistentNaming
    [DefaultExecutionOrder(-20)]
    [MonoSingletonPath("YLFramework")]
    public class YLFrameworkRoot : MonoSingleton<YLFrameworkRoot>
    {
        /// <summary>保留 Unity 组件的构造入口；业务通过单例访问，不使用 new 创建宿主。</summary>
        private YLFrameworkRoot()
        {
        }

        // 可预先绑定默认 UI 根；未绑定时直到首次 UI 请求才从框架预制体创建。
        [SerializeField] private UIRootContext panelRoot;
        private bool _isPreparingPanelRoot;
        // 上次完整准备成功的执行会话身份；根对象存在并不等于本轮已准备成功。
        private object _preparedPanelRootSession;

        /// <summary>按需取得宿主唯一默认 UI 根。仅 Audio/Action/Module 层级访问不会创建 UI。</summary>
        /// <remarks>准备过程包含结构校验、根登记和 CanvasCreated 通知；通知成功后才缓存本次会话。</remarks>
        /// <exception cref="InvalidOperationException">宿主不唯一、准备过程重入、默认根已销毁或根结构不合法。</exception>
        internal UIRootContext GetPanelRoot()
        {
            // 调用方和 Module 都未指定根时，PanelManager 从这里准备或复用默认根。
            if (FindObjectsByType<YLFrameworkRoot>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length != 1)
            {
                throw new InvalidOperationException("存在多个 YLFrameworkRoot；默认 UI 根归属不明确，请保留唯一宿主。");
            }

            // CanvasCreated 回调可能再次请求默认根，因此准备完成前禁止重入。
            if (_isPreparingPanelRoot)
            {
                throw new InvalidOperationException("默认 UI 根创建事务不能重入。");
            }

            // ReferenceEquals 检查 C# 引用；!panelRoot 使用 Unity 的对象存活判断。
            // 两者合起来识别“包装引用仍在，但原生对象已销毁”的 fake-null，不能将它当作未绑定而悄悄换根。
            if (!ReferenceEquals(panelRoot, null) && !panelRoot)
            {
                throw new InvalidOperationException("宿主默认 UI 根已销毁，不能自动换根。");
            }

            // 执行会话令牌每轮运行初始化时换新，切场景不换；用它区分 Domain Reload 关闭时可能残留的旧状态。
            var execution = SingletonSessionState.ExecutionSession;
            if (panelRoot && ReferenceEquals(_preparedPanelRootSession, execution))
            {
                // 复用也要检查层容器和遮罩，避免外部修改后继续使用失效结构。
                panelRoot.ValidateStructure();
                return panelRoot;
            }

            _isPreparingPanelRoot = true;
            GameObject created = null;
            try
            {
                var context = panelRoot;
                if (!context)
                {
                    // 先取得预制体资产，下面 Instantiate 才创建由宿主承载的场景实例。
                    var prefab = Resources.Load<GameObject>("YLFramework/Core/Canvas");
                    if (!prefab || !prefab.GetComponent<UIRootContext>())
                    {
                        throw new InvalidOperationException("Core Canvas 尚未迁移为 UIRootContext 基础根，请在 Editor 显式执行 UI Panel 资产迁移。");
                    }

                    created = Instantiate(prefab, transform, false);
                    context = created.GetComponent<UIRootContext>();
                    panelRoot = context;
                }

                context.ValidateStructure();
                context.GetLayer(0);
                // 实例可能已在 OnEnable 中登记，但完整准备仍需通过下面的通知和复检。
                PanelManager.RegisterRoot(context);
                // 回调可能改变根结构或抛错，因此再次校验成功后才记录本轮成功身份。
                PanelManager.CanvasCreated?.Invoke(context.Canvas);
                context.ValidateStructure();
                _preparedPanelRootSession = execution;
                return context;
            }
            catch
            {
                // 只销毁本次新建根，不动预绑定根，也不回滚订阅者的业务动作。
                // Destroy 延后实际销毁，生命周期注销入口在 UIRootContext.OnDestroy，并非退出此处就已同步撤销登记。
                if (created)
                {
                    panelRoot = null;
                    Destroy(created);
                }

                throw;
            }
            finally
            {
                _isPreparingPanelRoot = false;
            }
        }

        /// <summary>取得或创建宿主下的模块层级节点，不创建模块实例或执行模块启动逻辑。</summary>
        /// <param name="moduleName">要查找的模块层级名称。</param>
        /// <returns>已有的匹配节点，或新建并挂到宿主下的节点。</returns>
        /// <remarks>这里仅组织 Transform 层级；模块的执行与生命周期仍由各自的宿主管理。</remarks>
        public Transform GetModuleRoot(string moduleName)
        {
            var module = transform.Find(moduleName);
            if (module != null)
            {
                return module;
            }

            module = new GameObject(moduleName).transform;
            // false 表示不保留原世界变换；新节点保持默认局部变换，按宿主的坐标系组织层级。
            module.SetParent(transform, false);
            return module;
        }

        /// <summary>在宿主唤醒时请求跨场景保留，避免切换场景时一并销毁共享模块层级。</summary>
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }
    }
}
