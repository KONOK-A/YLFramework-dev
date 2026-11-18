using System;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using YLFramework.Editor.Resource;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    /// <summary>Addressables 代码交付与原生基座的显式编排输入，不复制官方 Entries/Labels/Profiles。</summary>
    [CreateAssetMenu(menuName = "YLFramework/HybridCLR/Build Profile", fileName = "HybridCLRBuildProfile")]
    public sealed class HybridCLRBuildProfile : ScriptableObject
    {
        /// <summary>旧资产的迁移输入；非空时拒绝构建，不自动改写旧配方。</summary>
        [HideInInspector]
        public ResourceBuildRecipe recipe;
        /// <summary>新版基座合同；旧配置须明确迁移并重新构建，不兼容旧冻结基座。</summary>
        public int schemaVersion = 2;
        public AddressableAssetSettings addressableSettings;
        public AddressableAssetGroup codeGroup;
        /// <summary>仅作为规范 address 的业务命名空间，不表示私有 Catalog。</summary>
        public string projectId;
        public BuildTarget target = BuildTarget.StandaloneWindows64;
        public string appContentCompatibilityId = "aa-base-v2";
        /// <summary>须与当前 Editor 和 Player 选项完全匹配的 Development 标记。</summary>
        public bool development;
        /// <summary>本配置拥有的 Assets 相对生成根，只交付规范 .bytes/.json 代码资源。</summary>
        public string generatedAssetDirectory;
        /// <summary>持久冻结基线目录；新基座不得覆盖已有目录，也不得位于 Assets、Packages、Library 或 Temp 内。</summary>
        public string baselineDirectory;
        /// <summary>项目入口的精确程序集名，发布时包含其热更依赖闭包；空集合表示官方全量登记。</summary>
        [Tooltip("留空发布官方登记的全部程序集；指定入口时自动包含其热更依赖，不改变全局原生登记。")]
        public string[] hotUpdateAssemblies = Array.Empty<string>();
        /// <summary>显式共享的直接基座拥有者；绑定后仅发布补丁，原生登记仍以拥有者的冻结基线为准。</summary>
        [Tooltip("可选：显式复用另一个配置拥有的原生基座。只允许一层绑定，基座目录与兼容身份必须一致。")]
        public HybridCLRBuildProfile baseProfile;
    }
}
