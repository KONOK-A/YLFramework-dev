using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Integrations.HybridCLR.Editor;

namespace YLFramework.Samples.HybridCLR.Editor
{
    /// <summary>仅供 Editor 显式创建隔离接入样例的作者工具，不在运行时或打开窗口时自动执行。</summary>
    public static class HybridCLRExampleAssets
    {
        /// <summary>只创建一个全新自有样例目录；不配置官方热更名单、切换后端或保存场景。</summary>
        /// <param name="assetDirectory">父文件夹已存在、目标尚不存在的 Assets 相对目录。</param>
        /// <param name="persistentBaselineDirectory">写入 Profile 的持久基线目录，后续构建另行检查边界和归属。</param>
        /// <returns>已保存并关联代码、配置和真实热更 Prefab 的样例 Profile。</returns>
        /// <remarks>调用前须已加载 Entry 样例程序集；官方热更登记及原生环境仍由接收工程显式准备。</remarks>
        public static HybridCLRBuildProfile Create(string assetDirectory, string persistentBaselineDirectory)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Create sample assets in a settled Editor.");
            }

            if (string.IsNullOrEmpty(assetDirectory) || !assetDirectory.StartsWith("Assets/", StringComparison.Ordinal) ||
                assetDirectory.Contains('\\') || assetDirectory.Contains("..") || Directory.Exists(assetDirectory) || File.Exists(assetDirectory))
            {
                throw new ArgumentException("Choose a new explicit Assets directory.", nameof(assetDirectory));
            }

            var parent = Path.GetDirectoryName(assetDirectory).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent))
            {
                throw new DirectoryNotFoundException(parent);
            }

            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetDirectory));
            AssetDatabase.CreateFolder(assetDirectory, "Generated");
            AssetDatabase.CreateFolder(assetDirectory + "/Generated", "hybridclr");
            AssetDatabase.CreateFolder(assetDirectory, "Data");
            var textPath = assetDirectory + "/Data/message.json";
            File.WriteAllText(textPath, "[{\"Id\":1,\"Text\":\"YL Resource configuration\"}]");
            AssetDatabase.ImportAsset(textPath, ImportAssetOptions.ForceSynchronousImport);
            var preview = EditorSceneManager.NewPreviewScene();
            GameObject source = null;
            var prefabPath = assetDirectory + "/Data/HotView.prefab";
            try
            {
                source = new GameObject("HotView") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(source, preview);
                source.hideFlags = HideFlags.None;
                var entry = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "YLFramework.HybridCLR.Sample.Entry");
                source.AddComponent(entry.GetType("YLFramework.Samples.HybridCLR.HotUpdate.ExampleBehaviour", true));
                if (!PrefabUtility.SaveAsPrefabAsset(source, prefabPath))
                {
                    throw new IOException("Could not save the owned hot-script prefab.");
                }
            }
            finally
            {
                if (source)
                {
                    UnityEngine.Object.DestroyImmediate(source);
                }

                EditorSceneManager.ClosePreviewScene(preview);
            }
            var project = UnityEngine.ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "hybridclr-example-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(assetDirectory + "/Generated") });
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(assetDirectory + "/Data") });
            project.groups.Add(new ResourceGroup
            {
                id = "code",
                selections = new List<ResourceSelection>
                { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(assetDirectory + "/Generated/hybridclr"), extensions = new[] { ".bytes", ".json" } } }
            });
            project.groups.Add(new ResourceGroup
            {
                id = "configuration",
                selections = new List<ResourceSelection>
                { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(textPath), address = "sample/message.json" } }
            });
            project.groups.Add(new ResourceGroup
            {
                id = "prefab",
                selections = new List<ResourceSelection>
                { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(prefabPath), address = "sample/hot-view" } }
            });
            AssetDatabase.CreateAsset(project, assetDirectory + "/Project.asset");
            var recipe = UnityEngine.ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.target = BuildTarget.StandaloneWindows64;
            recipe.action = ResourceBuildAction.BuildAndPublish;
            recipe.appContentCompatibilityId = "hybridclr-base-v1";
            AssetDatabase.CreateAsset(recipe, assetDirectory + "/Recipe.asset");
            var profile = UnityEngine.ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            profile.recipe = recipe;
            profile.development = EditorUserBuildSettings.development;
            profile.generatedAssetDirectory = assetDirectory + "/Generated";
            profile.baselineDirectory = persistentBaselineDirectory;
            AssetDatabase.CreateAsset(profile, assetDirectory + "/Profile.asset");
            AssetDatabase.SaveAssetIfDirty(project);
            AssetDatabase.SaveAssetIfDirty(recipe);
            AssetDatabase.SaveAssetIfDirty(profile);
            return profile;
        }
    }
}
