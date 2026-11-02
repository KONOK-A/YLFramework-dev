using System;
using System.Collections.Generic;

namespace YLFramework.Core.Tests
{
    public sealed class YLCoreStartupProbeModule : SingletonModule<YLCoreStartupProbeModule>
    {
        public static int ConstructionCount;
        public static Action ConstructorAction;
        public int StartCount;
        public Action StartAction;

        public YLCoreStartupProbeModule()
        {
            ConstructionCount++;
            ConstructorAction?.Invoke();
        }

        public override void OnStart()
        {
            StartCount++;
            StartAction?.Invoke();
        }
    }

    public sealed class YLCoreLifecycleModule : YLFramework.Core.Module
    {
        public readonly List<string> Events = new List<string>();
        public bool SeedMvc;
        public int StartCount;
        public int CloseCount;
        public bool RegisteredAtStart;
        public bool RegisteredAtClose;
        public string ProjectNameAtStart;
        public YLCoreProbeModel StartedModel;
        public YLCoreProbeController StartedController;
        public Action CloseAction;

        public override void OnStart()
        {
            StartCount++;
            Events.Add("module.start");
            RegisteredAtStart = ReferenceEquals(YLFramework.Core.ModuleManager.GetModule(ModuleName), this);
            ProjectNameAtStart = ProjectName;
            if (!SeedMvc)
            {
                return;
            }

            StartedModel = new YLCoreProbeModel { Id = 101, Events = Events };
            AddModel(StartedModel);
            StartedController = LoadController<YLCoreProbeController>();
        }

        protected override void OnClose()
        {
            CloseCount++;
            Events.Add("module.close");
            RegisteredAtClose = ReferenceEquals(YLFramework.Core.ModuleManager.GetModule(ModuleName), this);
            CloseAction?.Invoke();
        }
    }

    public sealed class YLCoreConsumerModule : YLFramework.Core.Module
    {
        public override void OnStart()
        {
        }
    }

    public sealed class YLCoreBaseProbeModule : YLFramework.Core.Module
    {
        // Only the fixture may make its own base module closable during cleanup.
        public bool AllowClose;
        public override bool IsBaseModule => !AllowClose;
        public override void OnStart()
        {
        }
    }

    public class YLCoreProbeModel : YLFramework.Core.Model
    {
        public static int ConstructionCount;
        public static Action<YLCoreProbeModel> ConstructorAction;
        public YLCoreProbeModel()
        {
            ConstructionCount++;
            ConstructorAction?.Invoke(this);
        }

        public int InitCount;
        public int ExitCount;
        public bool RegisteredAtInit;
        public bool RegisteredAtExit;
        public List<string> Events;
        public Action<YLCoreProbeModel> InitAction;
        public Action<YLCoreProbeModel> ExitAction;

        public override void OnInit()
        {
            InitCount++;
            RegisteredAtInit = ReferenceEquals(Module.GetModel(GetType(), Id), this);
            if (Events != null)
            {
                Events.Add("model.init");
            }

            InitAction?.Invoke(this);
        }

        public override void OnExit()
        {
            ExitCount++;
            RegisteredAtExit = ReferenceEquals(Module.GetModel(GetType(), Id), this);
            if (Events != null)
            {
                Events.Add("model.exit");
            }

            ExitAction?.Invoke(this);
        }
    }

    public sealed class YLCoreDerivedProbeModel : YLCoreProbeModel
    {
    }

    public sealed class YLCoreConstantHashModel : YLCoreProbeModel
    {
        public override int GetHashCode() => 1;
    }

    public sealed class YLCoreProbeController : YLFramework.Core.Controller
    {
        public static int ConstructionCount;
        public static Action<YLCoreProbeController> ConstructorAction;
        public YLCoreProbeController()
        {
            ConstructionCount++;
            ConstructorAction?.Invoke(this);
        }

        public int InitCount;
        public int ExitCount;
        public YLFramework.Core.Module OwnerAtInit;
        public YLFramework.Core.Module OwnerAtExit;
        public Action<YLCoreProbeController> InitAction;
        public Action<YLCoreProbeController> ExitAction;

        public override void OnInit()
        {
            InitCount++;
            OwnerAtInit = Module;
            var lifecycle = Module as YLCoreLifecycleModule;
            if (lifecycle != null)
            {
                lifecycle.Events.Add("controller.init");
            }

            InitAction?.Invoke(this);
        }

        public override void OnExit()
        {
            ExitCount++;
            OwnerAtExit = Module;
            var lifecycle = Module as YLCoreLifecycleModule;
            if (lifecycle != null)
            {
                lifecycle.Events.Add("controller.exit");
            }

            ExitAction?.Invoke(this);
        }
    }
}
