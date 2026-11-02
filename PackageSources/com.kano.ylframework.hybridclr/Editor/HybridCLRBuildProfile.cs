using System;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Editor.Resource;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    /// <summary>显式构建编排配置；Project/Target/基座身份只读取 Resource Recipe，不覆盖官方程序集设置。</summary>
    [CreateAssetMenu(menuName = "YLFramework/HybridCLR/Build Profile", fileName = "HybridCLRBuildProfile")]
    public sealed class HybridCLRBuildProfile : ScriptableObject
    {
        /// <summary>资源构建及发布配方，提供项目、目标平台和内容兼容身份。</summary>
        [FormerlySerializedAs("Recipe")] public ResourceBuildRecipe recipe;
        /// <summary>须与当前 Editor 和 Player 选项完全匹配的 Development 标记。</summary>
        [FormerlySerializedAs("Development")] public bool development;
        /// <summary>本配置拥有的 Assets 相对生成根，只交付规范 .bytes/.json 代码资源。</summary>
        [FormerlySerializedAs("GeneratedAssetDirectory")] public string generatedAssetDirectory;
        /// <summary>持久冻结基线目录；新基座不得覆盖已有目录，也不得位于 Assets、Packages、Library 或 Temp 内。</summary>
        [FormerlySerializedAs("BaselineDirectory")] public string baselineDirectory;
        /// <summary>项目入口的精确程序集名，发布时包含其热更依赖闭包；空集合表示官方全量登记。</summary>
        [Tooltip("留空发布官方登记的全部程序集；指定入口时自动包含其热更依赖，不改变全局原生登记。")]
        [FormerlySerializedAs("HotUpdateAssemblies")] public string[] hotUpdateAssemblies = Array.Empty<string>();
        /// <summary>显式共享的直接基座拥有者；绑定后仅发布补丁，原生登记仍以拥有者的冻结基线为准。</summary>
        [Tooltip("可选：显式复用另一个配置拥有的原生基座。只允许一层绑定，基座目录与兼容身份必须一致。")]
        [FormerlySerializedAs("BaseProfile")] public HybridCLRBuildProfile baseProfile;
    }
}
