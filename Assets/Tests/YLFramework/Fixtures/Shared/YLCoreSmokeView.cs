using UnityEngine;

namespace YLFramework.Core.Tests
{
    public sealed class YLCoreSmokeView : YLFramework.Core.View
    {
        public const string ProbeEvent = "YLCoreSmoke.View.Probe";
        public static System.Action<YLCoreSmokeView> LoadingProbe;
        public static System.Action<YLCoreSmokeView, LoadViewArgs> ArgsProbe;
        public static System.Action<YLCoreSmokeView> EnabledProbe;
        public static System.Action<YLCoreSmokeView> DisabledProbe;
        public static System.Action SharedEventHandler;
        public static bool DuplicateProbeHandler;
        public static string SignatureConflictEvent;
        public int AwakeCount
        {
            get; private set;
        }
        public int LoadedCount
        {
            get; private set;
        }
        public int ArgsCount
        {
            get; private set;
        }
        public int RemovedCount
        {
            get; private set;
        }
        public int UnboundCount
        {
            get; private set;
        }
        public int ClosedCount
        {
            get; private set;
        }
        public int EnabledCount
        {
            get; private set;
        }
        public int DisabledCount
        {
            get; private set;
        }
        public int EventCount
        {
            get; private set;
        }
        public int Token
        {
            get; private set;
        }
        public object Parameter
        {
            get; private set;
        }
        public Model BoundModel
        {
            get => Model;
            set
            {
                if (value == null)
                {
                    UnbindModel();
                }
                else
                {
                    BindModel(value);
                }
            }
        }
        public System.Action RemoveAction;
        public System.Action<Model> UnboundAction;
        public System.Action LoadedAction;
        public System.Action AfterClose;
        public System.Action EventAction;

        protected override void OnClosed()
        {
            ClosedCount++;
            AfterClose?.Invoke();
        }

        protected override void OnModelUnbound(Model previousModel)
        {
            UnboundCount++;
            UnboundAction?.Invoke(previousModel);
        }

        private void Awake()
        {
            AwakeCount++;
            var handler = SharedEventHandler ?? (System.Action)HandleEvent;
            if (DuplicateProbeHandler)
            {
                handler += handler;
            }

            RegisterEvent(ProbeEvent, handler);
            if (SignatureConflictEvent != null)
            {
                RegisterEvent<int>(SignatureConflictEvent, _ => EventCount++);
            }
        }

        private void HandleEvent()
        {
            EventCount++;
            EventAction?.Invoke();
        }

        protected override void OnViewEnabled()
        {
            EnabledCount++;
            EnabledProbe?.Invoke(this);
        }

        protected override void OnViewDisabled()
        {
            DisabledCount++;
            DisabledProbe?.Invoke(this);
        }

        public override void OnLoaded(params object[] param)
        {
            LoadedCount++;
            Parameter = param.Length > 0 ? param[0] : null;
            LoadedAction?.Invoke();
            LoadingProbe?.Invoke(this);
        }

        public override void OnLoaded(LoadViewArgs args)
        {
            ArgsCount++;
            Token = args.GetInt("token");
            ArgsProbe?.Invoke(this, args);
        }

        public override void OnRemove()
        {
            RemovedCount++;
            RemoveAction?.Invoke();
        }
    }
}
