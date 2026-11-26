using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Editor.Resource
{
    /// <summary>官方内容构建的实际结果；不代表已上传、部署或完成 Player 验证。</summary>
    public sealed class ResourceEditorBuildReport
    {
        public AddressablesPlayerBuildResult Result
        {
            get; internal set;
        }
        public string SettingsGuid
        {
            get; internal set;
        }
        public string ProfileId
        {
            get; internal set;
        }
        public BuildTarget Target
        {
            get; internal set;
        }
        public string ContentStatePath
        {
            get; internal set;
        }
        public bool IsContentUpdate
        {
            get; internal set;
        }
        public string Summary => "Addressables " + (IsContentUpdate ? "Content Update" : "Full Build") +
            "\nTarget=" + Target + " / Profile=" + ProfileId + "\nOutput=" + Result.OutputPath +
            "\nContentState=" + ContentStatePath;
    }

    /// <summary>仅编排官方构建，不维护私有 Catalog、BundleMap、发布快照或运行时配置工厂。</summary>
    public static class ResourceEditorOperations
    {
        private static bool _isBuilding;

        /// <summary>只读门禁；不切平台、Profile、Builder，不保存用户场景或自动修复设置。</summary>
        public static void ValidateBuild(AddressableAssetSettings settings)
        {
            if (!settings || settings != AddressableAssetSettingsDefaultObject.GetSettings(false))
            {
                throw new InvalidOperationException("Select the existing official default Addressables settings; the build never switches settings implicitly.");
            }
            if (_isBuilding || BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating || EditorUtility.scriptCompilationFailed)
            {
                throw new InvalidOperationException("Addressables content builds require a settled Editor without another build.");
            }
            var errors = ResourceAddressablesAuthoring.Validate(settings);
            if (errors.Length != 0)
            {
                throw new InvalidOperationException(string.Join("\n", errors));
            }
            if (!EditorUtility.IsPersistent(settings) || EditorUtility.IsDirty(settings) ||
                string.IsNullOrEmpty(settings.profileSettings.GetProfileName(settings.activeProfileId)))
            {
                throw new InvalidOperationException("Explicitly save the official Addressables settings and select a valid Profile first.");
            }
            if (!(settings.ActivePlayerDataBuilder is BuildScriptPackedMode))
            {
                throw new InvalidOperationException("Select the official packed content builder, not a Play Mode simulation builder.");
            }
            foreach (var group in settings.groups.Where(group => group))
            {
                if (EditorUtility.IsDirty(group) || group.Schemas.Any(schema => schema && EditorUtility.IsDirty(schema)))
                {
                    throw new InvalidOperationException("Explicitly save the official group and schemas before building: " + group.Name);
                }
            }
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                if (SceneManager.GetSceneAt(index).isDirty)
                {
                    throw new InvalidOperationException("Save your scenes explicitly before an official content build.");
                }
            }
        }

        /// <summary>构建当前官方配置，并以拒绝覆盖的方式保存该 Player/平台对应的官方内容状态文件。</summary>
        public static ResourceEditorBuildReport BuildContent(AddressableAssetSettings settings, string contentStateArchivePath,
            CancellationToken cancellation = default)
        {
            var archive = RequireNewStatePath(contentStateArchivePath);
            cancellation.ThrowIfCancellationRequested();
            ValidateBuild(settings);
            if (IsLiveContentStatePath(settings, archive))
            {
                throw new ArgumentException("Archive the content state outside the live official build output.", nameof(contentStateArchivePath));
            }
            _isBuilding = true;
            try
            {
                var profile = settings.activeProfileId;
                var target = EditorUserBuildSettings.activeBuildTarget;
                AddressableAssetSettings.BuildPlayerContent(out var result);
                RequireResult(result);
                cancellation.ThrowIfCancellationRequested();
                if (settings.activeProfileId != profile || EditorUserBuildSettings.activeBuildTarget != target)
                {
                    throw new InvalidOperationException("The official build Profile or target changed during the content build.");
                }
                var state = result.ContentStateFilePath;
                if (string.IsNullOrEmpty(state) || !File.Exists(state) || new FileInfo(state).Length == 0)
                {
                    throw new InvalidDataException("The official build did not produce addressables_content_state.bin; no baseline was archived.");
                }
                ResourcePathValidation.RejectLinks(state);
                ResourcePathValidation.RejectLinks(archive);
                File.Copy(state, archive, false);
                if (!File.ReadAllBytes(state).SequenceEqual(File.ReadAllBytes(archive)))
                {
                    throw new IOException("The archived official content state does not match the build output.");
                }
                return Report(settings, result, target, profile, archive, false);
            }
            finally
            {
                _isBuilding = false;
            }
        }

        /// <summary>用明确的官方旧内容状态构建更新；限制冲突须先在官方工具中处理，不擅自搬组。</summary>
        public static ResourceEditorBuildReport BuildContentUpdate(AddressableAssetSettings settings, string previousContentStatePath,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateBuild(settings);
            if (!string.IsNullOrWhiteSpace(previousContentStatePath) && IsLiveContentStatePath(settings, previousContentStatePath))
            {
                throw new ArgumentException("Archive the Player content state outside the live official build output before building an update.", nameof(previousContentStatePath));
            }
            if (string.IsNullOrWhiteSpace(previousContentStatePath) || !File.Exists(previousContentStatePath))
            {
                throw new FileNotFoundException("Select the official content state archived with the target Player.", previousContentStatePath);
            }
            ResourcePathValidation.RejectLinks(previousContentStatePath);
            var path = Path.GetFullPath(previousContentStatePath);
            var bytes = File.ReadAllBytes(path);
            var state = ContentUpdateScript.LoadContentState(path);
            if (state == null || state.editorVersion != Application.unityVersion || string.IsNullOrEmpty(state.playerVersion) ||
                !settings.BuildRemoteCatalog || string.IsNullOrEmpty(state.remoteCatalogLoadPath) ||
                state.remoteCatalogLoadPath != settings.RemoteCatalogLoadPath.GetValue(settings))
            {
                throw new InvalidDataException("The official content state does not match this Editor and remote Catalog destination; build a new Player baseline instead.");
            }
            _isBuilding = true;
            try
            {
                var restricted = ContentUpdateScript.GatherModifiedEntries(settings, path);
                if (restricted == null || restricted.Count != 0)
                {
                    throw new InvalidOperationException("Resolve official content update restrictions before building: " +
                        (restricted == null ? "state could not be inspected" : string.Join(", ", restricted.Select(entry => entry.AssetPath))));
                }
                cancellation.ThrowIfCancellationRequested();
                var profile = settings.activeProfileId;
                var target = EditorUserBuildSettings.activeBuildTarget;
                var result = ContentUpdateScript.BuildContentUpdate(settings, path);
                RequireResult(result);
                if (!File.ReadAllBytes(path).SequenceEqual(bytes))
                {
                    throw new IOException("The frozen Player content state changed during the update build.");
                }
                if (settings.activeProfileId != profile || EditorUserBuildSettings.activeBuildTarget != target)
                {
                    throw new InvalidOperationException("The official build Profile or target changed during the content update.");
                }
                cancellation.ThrowIfCancellationRequested();
                return Report(settings, result, target, profile, path, true);
            }
            finally
            {
                _isBuilding = false;
            }
        }

        private static bool IsLiveContentStatePath(AddressableAssetSettings settings, string path)
        {
            var livePath = ContentUpdateScript.GetContentStateDataPath(false, settings);
            return !string.IsNullOrWhiteSpace(livePath) &&
                (!Uri.TryCreate(livePath, UriKind.Absolute, out var uri) || uri.IsFile) &&
                Path.GetFullPath(path).Equals(Path.GetFullPath(livePath), StringComparison.OrdinalIgnoreCase);
        }

        private static string RequireNewStatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Choose an explicit new local .bin content state archive.", nameof(path));
            }
            var full = Path.GetFullPath(path);
            ResourcePathValidation.RejectLinks(full);
            if (File.Exists(full) || Directory.Exists(full) || File.Exists(full + ".meta"))
            {
                throw new IOException("The content state archive already exists and will not be overwritten: " + full);
            }
            if (!Directory.Exists(Path.GetDirectoryName(full)))
            {
                throw new DirectoryNotFoundException("Create the explicit Player baseline parent directory first: " + full);
            }
            return full;
        }

        private static void RequireResult(AddressablesPlayerBuildResult result)
        {
            if (result == null || !string.IsNullOrEmpty(result.Error))
            {
                throw new InvalidOperationException("Official Addressables build failed: " + (result?.Error ?? "no build result"));
            }
            if (string.IsNullOrEmpty(result.OutputPath) || !File.Exists(result.OutputPath))
            {
                throw new InvalidDataException("Official Addressables build returned no runtime settings output.");
            }
        }

        private static ResourceEditorBuildReport Report(AddressableAssetSettings settings, AddressablesPlayerBuildResult result,
            BuildTarget target, string profile, string state, bool update) => new ResourceEditorBuildReport
            {
                Result = result,
                SettingsGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)),
                ProfileId = profile,
                Target = target,
                ContentStatePath = state,
                IsContentUpdate = update
            };
    }
}
