using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.Editor.Core;

namespace YLFramework.Editor
{
    /// <summary>仓库测试专用的 UIAcceptance Windows64 IL2CPP 门，不随框架导出或自动运行。</summary>
    public static class UIPanelAcceptanceBuild
    {
        public const string ScenePath = "Assets/Tests/YLFramework/Acceptance/Core/UIAcceptance.unity";

        /// <summary>使用 Medium stripping 构建真实输入关口，结束后恢复原后端和裁剪设置；不保存用户场景。</summary>
        [MenuItem("YLFramework/Core/UI Panel/构建 Windows64 IL2CPP 验收")]
        public static void BuildWindows64IL2CPP()
        {
            if (Application.isPlaying || EditorApplication.isCompiling || PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                throw new InvalidOperationException("退出 Play/Prefab Stage 并等待编译完成后构建，不保存用户场景。");
            }

            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("存在未保存场景，验收构建不会替用户保存。");
                }
            }

            if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath))
            {
                throw new InvalidOperationException("缺少正式 UIAcceptance 场景。");
            }

            var basic = AssetDatabase.LoadAssetAtPath<GameObject>(UIPanelAssetMigration.BaseRootPath);
            if (!basic || !basic.GetComponent<global::YLFramework.Core.UIRootContext>() || !AssetDatabase.LoadAssetAtPath<GameObject>(UIPanelAssetMigration.TipsVariantPath))
            {
                throw new InvalidOperationException("先完成显式资产迁移及导入核验，再构建 IL2CPP 关口。");
            }

            var moduleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem", true);
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(moduleType.Assembly);
            Debug.Log("UI Panel 实际加载 Input System：" + (package == null ? moduleType.Assembly.FullName : package.name + "@" + package.version));
            var target = NamedBuildTarget.Standalone;
            var previous = PlayerSettings.GetScriptingBackend(target);
            var stripping = PlayerSettings.GetManagedStrippingLevel(target);
            try
            {
                PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Medium);
                var output = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Application.dataPath, "../Temp/UI.Panel.IL2CPP/UIAcceptance.exe")));
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(output)));
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    target = BuildTarget.StandaloneWindows64,
                    locationPathName = output,
                    options = BuildOptions.Development
                });
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new InvalidOperationException("IL2CPP 构建未成功：" + report.summary.result);
                }

                Debug.Log("仅完成构建，尚未运行或验收：" + output + "。运行默认 UI 场景并检查退出、原生捕获门标记及无相关异常。");
            }
            finally
            {
                PlayerSettings.SetManagedStrippingLevel(target, stripping);
                PlayerSettings.SetScriptingBackend(target, previous);
            }
        }
    }
}
