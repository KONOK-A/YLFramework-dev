using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>旧版迁移输入，原字段完整保留；不执行、不自动转换或修改存量资产。</summary>
    public sealed class ResourceBuildRecipe : ScriptableObject
    {
        public ResourceProject project;
        public BuildTarget target = BuildTarget.StandaloneWindows64;
        public string appContentCompatibilityId = "app-v1";
        public ResourceBuildAction action;
        public bool forceRebuild;
        public bool generateReferences;
        public string referenceDirectory = ResourceReferenceOutput.DirectoryPath;
    }

    /// <summary>仅供旧资产反序列化，不用于新版官方构建决策。</summary>
    public enum ResourceBuildAction
    {
        BuildOnly,
        BuildAndPublish
    }
}
