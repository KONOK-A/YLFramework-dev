using System;
using UnityEngine;
using UnityEngine.UI;
using YLFramework.Singletons;

namespace YLFramework.Actions
{
    // 屏幕过渡共用一块遮罩画布：优先收养场景中的现有实例，没有时从资源按 Prefab 创建；
    // 所有 ScreenTransition 动作都通过它读写 ColorImage，因此并行过渡会互相影响。
    internal class ScreenTransitionCanvas : MonoSingletonHost<ScreenTransitionCanvas>
    {
        internal const string ResourcePath = "ActionKit/ScreenTransitionCanvas";
        /// <summary>Prefab 中的全屏遮罩 Image，透明度由过渡动作直接写入。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("ColorImage")]
        public Image colorImage;

        // 关停期间禁止再创建画布；includeInactive 为 false，只收养激活中的现有实例。
        internal new static ScreenTransitionCanvas Instance
        {
            get
            {
                if (ActionKit.IsShuttingDown)
                {
                    throw new InvalidOperationException("ActionKit is shutting down.");
                }

                return GetInstance(true, CreateCanvas, false);
            }
        }

        // 资源加载失败或 prefab 缺少必需组件/引用时抛异常，并清理刚创建的临时对象，避免留下半成品宿主。
        private static GameObject CreateCanvas()
        {
            var prefab = Resources.Load<GameObject>(ResourcePath);
            if (!prefab)
            {
                throw new InvalidOperationException("ActionKit ScreenTransition resource is missing. Run YLFramework > Action > 创建屏幕过渡资源 in the Editor.");
            }
            var root = Instantiate(prefab);
            var canvas = root.GetComponent<ScreenTransitionCanvas>();
            if (!canvas || !canvas.colorImage)
            {
                if (canvas)
                {
                    canvas.ReleaseHost();
                }

                Destroy(root);
                throw new InvalidOperationException("ActionKit ScreenTransition resource must contain ScreenTransitionCanvas and its ColorImage reference.");
            }
            return root;
        }

        internal static void ReleaseOwnedInstance()
        {
            // 只销毁模块自行实例化的画布，保留场景中用户提供的对象。
            foreach (var canvas in FindObjectsByType<ScreenTransitionCanvas>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
            {
                canvas.ReleaseHost(true);
            }

            ResetHostReference();
        }
    }
}
