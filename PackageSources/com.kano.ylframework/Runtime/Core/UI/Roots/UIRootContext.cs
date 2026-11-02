using System;
using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Core
{
    /// <summary>实际 UI 根的身份、层映射和模态承载。不拥有业务、请求、Camera 或第二套缩放配置。</summary>
    [DisallowMultipleComponent]
    public sealed class UIRootContext : MonoBehaviour
    {
        /// <summary>根内一个显式层的容器和可选遮罩绑定，不自动创建缺失对象。</summary>
        [Serializable]
        public sealed class Layer
        {
            /// <summary>属于本根的明确层容器，不能自动回退到根 Transform。</summary>
            public Transform root;
            /// <summary>是否为该层模态提供遮罩；开启时必须有有效 maskImage。</summary>
            public bool enableMask;
            /// <summary>本层明确的遮罩，显隐与顺序由有效模态会话派生。</summary>
            public Image maskImage;
        }
        [SerializeField] private Canvas canvas;
        [SerializeField] private GraphicRaycaster graphicRaycaster;
        [SerializeField] private Layer[] uiLayers = Array.Empty<Layer>();

        /// <summary>原生根 Canvas；相机、渲染模式和 SortingLayer 直接配置在该组件。</summary>
        public Canvas Canvas => canvas ? canvas : GetComponent<Canvas>();
        internal bool IsValid => this && Canvas && (graphicRaycaster ? graphicRaycaster : GetComponent<GraphicRaycaster>()) &&
            gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        internal bool IsAvailable => IsValid && isActiveAndEnabled && Canvas.isActiveAndEnabled;
        internal bool HasValidStructure
        {
            get
            {
                if (!IsValid || uiLayers == null || uiLayers.Length == 0)
                {
                    return false;
                }

                foreach (var layer in uiLayers)
                {
                    if (layer == null || !layer.root || !layer.root.IsChildOf(transform) ||
                        layer.root.GetComponentInParent<UIRootContext>(true) != this ||
                        (layer.enableMask && (!layer.maskImage || !layer.maskImage.transform.IsChildOf(layer.root))))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
        /// <summary>检查根以及全部层、必需遮罩的存活和归属，不修复失效结构。</summary>
        internal void ValidateStructure()
        {
            ValidateRoot();
            if (!HasValidStructure)
            {
                throw new InvalidOperationException("UIRootContext 层容器或必需遮罩丢失、跨根，根结构已失效。");
            }
        }
        /// <summary>校验原生组件、场景及非空层映射，供只需根级前置条件的操作使用。</summary>
        internal void ValidateRoot()
        {
            if (!IsValid)
            {
                throw new InvalidOperationException("UIRootContext 必须有存活 Canvas/GraphicRaycaster 并属于已加载场景。");
            }

            if (uiLayers == null || uiLayers.Length == 0)
            {
                throw new InvalidOperationException("UIRootContext 需要显式 Layer 0；请执行资产迁移或配置层容器。");
            }
        }
        /// <summary>返回明确配置的层容器。缺层、已销毁层或跨根引用均报错，不补层也不回退。</summary>
        public Transform GetLayer(int index)
        {
            ValidateRoot();
            if (index < 0 || index >= uiLayers.Length || uiLayers[index] == null || !uiLayers[index].root ||
                !uiLayers[index].root.IsChildOf(transform) || uiLayers[index].root.GetComponentInParent<UIRootContext>(true) != this)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Panel Layer 缺失、已销毁或不属于该根。");
            }

            return uiLayers[index].root;
        }
        internal bool HasMask(int index) => index >= 0 && index < uiLayers.Length && uiLayers[index] != null &&
            uiLayers[index].enableMask && uiLayers[index].maskImage;

        /// <summary>运行时补取本对象组件引用并登记实际根，不接管其场景或对象寿命。</summary>
        private void OnEnable()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (!canvas)
            {
                canvas = GetComponent<Canvas>();
            }

            if (!graphicRaycaster)
            {
                graphicRaycaster = GetComponent<GraphicRaycaster>();
            }

            PanelManager.RegisterRoot(this);
        }
        /// <summary>停用时隐藏根内遮罩并重新协调面板；保留登记和会话以便恢复。</summary>
        private void OnDisable()
        {
            if (uiLayers != null)
            {
                foreach (var layer in uiLayers)
                {
                    if (layer != null && layer.maskImage)
                    {
                        layer.maskImage.gameObject.SetActive(false);
                    }
                }
            }

            PanelManager.RootChanged();
        }
        /// <summary>根销毁时撤销登记，由协调器处理失去根的面板会话。</summary>
        private void OnDestroy() => PanelManager.UnregisterRoot(this);

        /// <summary>按每层最高有效可见模态刷新遮罩，并把遮罩顺序放在该面板之前。</summary>
        internal void RefreshMasks()
        {
            if (uiLayers == null)
            {
                return;
            }

            for (int i = 0; i < uiLayers.Length; i++)
            {
                var layer = uiLayers[i];
                if (layer == null || !layer.maskImage)
                {
                    continue;
                }

                var modal = PanelManager.GetLayerModal(this, i);
                var session = modal ? modal.Session : null;
                bool show = IsAvailable && layer.enableMask && modal;
                layer.maskImage.gameObject.SetActive(show);
                layer.maskImage.raycastTarget = show;
                if (!show || !modal || !modal.IsLoaded || !ReferenceEquals(modal.Session, session))
                {
                    continue;
                }

                var maskCanvas = layer.maskImage.GetComponent<Canvas>();
                if (!maskCanvas)
                {
                    maskCanvas = layer.maskImage.gameObject.AddComponent<Canvas>();
                }

                if (!layer.maskImage.GetComponent<GraphicRaycaster>())
                {
                    layer.maskImage.gameObject.AddComponent<GraphicRaycaster>();
                }

                maskCanvas.overrideSorting = true;
                maskCanvas.sortingLayerID = Canvas.sortingLayerID;
                maskCanvas.sortingOrder = modal.SortingOrder - 1;
            }
        }
    }
}
