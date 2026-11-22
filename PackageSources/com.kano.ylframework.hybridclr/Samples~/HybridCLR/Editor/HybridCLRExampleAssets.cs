using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;
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
        public static HybridCLRBuildProfile Create(string assetDirectory, string persistentBaselineDirectory,
            AddressableAssetSettings settings, AddressableAssetGroup codeGroup, AddressableAssetGroup sampleGroup)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Create sample assets in a settled Editor.");
            }
            if (!settings || !codeGroup || !sampleGroup || codeGroup.Settings != settings || sampleGroup.Settings != settings ||
                codeGroup.ReadOnly || sampleGroup.ReadOnly)
            {
                throw new ArgumentException("Explicitly select existing writable official code/sample groups belonging to the same Settings.");
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
            var profile = UnityEngine.ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            profile.schemaVersion = 2;
            profile.addressableSettings = settings;
            profile.codeGroup = codeGroup;
            profile.projectId = "hybridclr-example-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            profile.target = BuildTarget.StandaloneWindows64;
            profile.appContentCompatibilityId = "hybridclr-aa-base-v2";
            profile.development = EditorUserBuildSettings.development;
            profile.generatedAssetDirectory = assetDirectory + "/Generated";
            profile.baselineDirectory = persistentBaselineDirectory;
            AssetDatabase.CreateAsset(profile, assetDirectory + "/Profile.asset");
            Undo.RecordObjects(new UnityEngine.Object[] { settings, sampleGroup }, "Register HybridCLR Sample Entries");
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(textPath), sampleGroup).SetAddress(profile.projectId + "/sample/message.json");
            settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(prefabPath), sampleGroup).SetAddress(profile.projectId + "/sample/hot-view");
            AssetDatabase.SaveAssetIfDirty(sampleGroup);
            AssetDatabase.SaveAssetIfDirty(settings);
            AssetDatabase.SaveAssetIfDirty(profile);
            return profile;
        }
    }
}
