using System;
using System.Collections.Generic;

namespace YLFramework.Core.Tests
{
    public sealed class YLCoreSmokeModule : YLFramework.Core.Module
    {
        public const string FixtureRoot = "Assets/Tests/YLFramework/Fixtures/Core/YLCoreResourceModule";
        public const string Project = "yl-core-resource";
        public YLCoreSmokeModule() => ProjectName = Project;
        public const string Table = "YLCoreSmokeRows";
        public const string SceneName = "YLCoreSmokeScene";
        public const string HarnessName = "YLCoreSmokeHarness";
        public const string ScenePath = FixtureRoot + "/" + SceneName + ".unity";
        public const string HarnessPath = FixtureRoot + "/" + HarnessName + ".unity";
        public const string Json = "[{\"Id\":101,\"Marker\":\"yl-smoke\"}]";

        public const string EmptyTable = "YLCoreEmptyRows";
        public const string BlankTable = "YLCoreBlankRows";
        public const string InvalidTable = "YLCoreInvalidRows";

        public bool StartWithScene;
        public Action StartAction;
        public string TableName = Table;
        public string SceneOverride;
        public int StartCount
        {
            get; private set;
        }
        public bool RowReadyAtStart
        {
            get; private set;
        }
        public bool SceneReadyAtStart
        {
            get; private set;
        }

#pragma warning disable CS0618
        public override Dictionary<string, Type> PreloadConfigTables =>
            TableName == null ? new Dictionary<string, Type>() : new Dictionary<string, Type> { { TableName, typeof(YLCoreSmokeRow[]) } };
#pragma warning restore CS0618

        public override string DefaultStartUpScene => SceneOverride ?? (StartWithScene ? SceneName : string.Empty);

        public override void OnStart()
        {
            StartCount++;
            var row = GetModel<YLCoreSmokeRow>(101);
            RowReadyAtStart = row != null && row.Marker == "yl-smoke" && ReferenceEquals(row.Module, this);
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
            SceneReadyAtStart = scene.IsValid() && scene.isLoaded;
            StartAction?.Invoke();
        }
    }

    public sealed class YLCoreSmokeRow : YLFramework.Core.Model
    {
        public static Action<YLCoreSmokeRow> InitAction;
        public string Marker
        {
            get; set;
        }
        public int InitCount
        {
            get; private set;
        }
        public int ExitCount
        {
            get; private set;
        }
        public override void OnInit()
        {
            InitCount++;
            InitAction?.Invoke(this);
        }
        public override void OnExit() => ExitCount++;
    }

    public sealed class YLCoreResourceProjectModule : YLFramework.Core.Module
    {
        public bool AllowClose;
        public override bool IsBaseModule => !AllowClose;
        [Obsolete("已过时!")]
        public override Dictionary<string, Type> PreloadConfigTables =>
            new Dictionary<string, Type> { { YLCoreSmokeModule.Table, typeof(YLCoreSmokeRow[]) } };
        public override void OnStart()
        {
        }
    }
}
