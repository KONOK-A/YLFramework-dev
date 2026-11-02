namespace YLFramework.Core.Tests
{
    public sealed class YLCoreDerivedHorizontalLayoutGroup : UnityEngine.UI.HorizontalLayoutGroup
    {
    }

    public sealed class YLCoreNativeLayoutReentryProbe : UnityEngine.EventSystems.UIBehaviour
    {
        public System.Action DimensionsChanged;
        public System.Action ParentChanged;
        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            DimensionsChanged?.Invoke();
        }
        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            ParentChanged?.Invoke();
        }
    }

    public sealed class YLCoreSmokePanel : YLFramework.Core.Panel
    {
        public static System.Action<YLCoreSmokePanel> LoadingProbe;
        public static System.Action<YLCoreSmokePanel, LoadViewArgs> ArgsProbe;
        public static System.Action<YLCoreSmokePanel, int> SortingProbe;
        public static System.Action<YLCoreSmokePanel> VisibleProbe;
        public static System.Action<YLCoreSmokePanel> InvisibleProbe;
        public static System.Action<YLCoreSmokePanel> EnabledProbe;
        public static System.Action<YLCoreSmokePanel, LoadViewArgs> UpdateProbe;
        public const string ProbeEvent = "YLCoreSmoke.PanelEvent";
        public static bool UseSharedHandler;
        public static int SharedEventCount;
        public int LoadedCount
        {
            get; private set;
        }
        public int ArgsCount
        {
            get; private set;
        }
        public int OpenCount
        {
            get; private set;
        }
        public int RemovedCount
        {
            get; private set;
        }
        public int ClosedCount
        {
            get; private set;
        }
        public int Token
        {
            get; private set;
        }
        public int EventCount
        {
            get; private set;
        }
        public System.Action RemoveAction;
        public System.Action ClosedAction;

        protected override void Awake()
        {
            base.Awake();
            RegisterEvent(ProbeEvent, UseSharedHandler ? (System.Action)SharedHandler : HandleEvent);
        }

        private static void SharedHandler() => SharedEventCount++;
        private void HandleEvent() => EventCount++;

        public override void OnLoaded(params object[] param)
        {
            LoadedCount++;
            LoadingProbe?.Invoke(this);
        }

        public override void OnLoaded(LoadViewArgs args)
        {
            ArgsCount++;
            Token = args.GetInt("token");
            ArgsProbe?.Invoke(this, args);
        }

        protected override void OnSortingOrderChange(int sortingOrder) => SortingProbe?.Invoke(this, sortingOrder);
        protected override void Visible() => VisibleProbe?.Invoke(this);
        protected override void Invisible() => InvisibleProbe?.Invoke(this);
        protected override void OnClosed()
        {
            ClosedCount++;
            ClosedAction?.Invoke();
        }
        protected override void OnViewEnabled() => EnabledProbe?.Invoke(this);
        protected override void OnOpen(LoadViewArgs args)
        {
            OpenCount++;
            Token = args.GetInt("token");
            UpdateProbe?.Invoke(this, args);
        }

        public override void OnRemove()
        {
            RemovedCount++;
            RemoveAction?.Invoke();
        }

        public static void ResetProbes()
        {
            LoadingProbe = null;
            ArgsProbe = null;
            SortingProbe = null;
            VisibleProbe = null;
            InvisibleProbe = null;
            EnabledProbe = null;
            UpdateProbe = null;
            UseSharedHandler = false;
            SharedEventCount = 0;
        }
    }

    public static class CorePanelTestRoots
    {
        public static UIRootContext Create(UnityEngine.GameObject host, int layerCount = 2, bool activate = true)
        {
            if (!host)
            {
                throw new System.ArgumentNullException(nameof(host));
            }

            if (host.activeSelf || !host.GetComponent<UnityEngine.Canvas>() ||
                !host.GetComponent<UnityEngine.UI.GraphicRaycaster>())
            {
                throw new System.ArgumentException("测试根必须 inactive 且已有 Canvas/GraphicRaycaster。", nameof(host));
            }

            if (layerCount < 1)
            {
                throw new System.ArgumentOutOfRangeException(nameof(layerCount));
            }

            if (host.GetComponent<UIRootContext>())
            {
                throw new System.ArgumentException("测试根不能已有 UIRootContext。", nameof(host));
            }

            var context = host.AddComponent<UIRootContext>();
            var layers = new UIRootContext.Layer[layerCount];
            for (int i = 0; i < layers.Length; i++)
            {
                var container = new UnityEngine.GameObject("Layer" + i, typeof(UnityEngine.RectTransform));
                container.transform.SetParent(host.transform, false);
                var rect = (UnityEngine.RectTransform)container.transform;
                rect.anchorMin = UnityEngine.Vector2.zero;
                rect.anchorMax = UnityEngine.Vector2.one;
                rect.sizeDelta = UnityEngine.Vector2.zero;
                var mask = new UnityEngine.GameObject("Mask", typeof(UnityEngine.RectTransform));
                mask.SetActive(false);
                mask.transform.SetParent(container.transform, false);
                var maskRect = (UnityEngine.RectTransform)mask.transform;
                maskRect.anchorMin = UnityEngine.Vector2.zero;
                maskRect.anchorMax = UnityEngine.Vector2.one;
                maskRect.sizeDelta = UnityEngine.Vector2.zero;
                var image = mask.AddComponent<UnityEngine.UI.Image>();
                image.color = new UnityEngine.Color(0, 0, 0, .5f);
                image.raycastTarget = false;
                layers[i] = new UIRootContext.Layer { root = rect, enableMask = true, maskImage = image };
            }
            typeof(UIRootContext).GetField("uiLayers", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).SetValue(context, layers);
            if (activate)
            {
                host.SetActive(true);
            }

            return context;
        }
    }
}
