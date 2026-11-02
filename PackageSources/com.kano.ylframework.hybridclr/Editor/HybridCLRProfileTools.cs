using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    /// <summary>当前配置与 Player 脚本图的只读检查快照，不是编译或原生运行证明。</summary>
    internal sealed class HybridCLRProfileInspection
    {
        /// <summary>阻止配置检查通过的错误消息。</summary>
        public readonly List<string> Errors = new List<string>();
        /// <summary>保存状态、环境与检查范围的提示。</summary>
        public readonly List<string> Warnings = new List<string>();
        /// <summary>当前解析出的项目入口及热更依赖名称。</summary>
        public string[] Assemblies = Array.Empty<string>();
        /// <summary>未显式选作入口而由引用闭包自动包含的名称。</summary>
        public string[] Dependencies = Array.Empty<string>();
        /// <summary>本次快照是否未发现配置错误，警告不影响此值。</summary>
        public bool HasSucceeded => Errors.Count == 0;
    }

    /// <summary>显式创建配置、关联规范资源映射及预览依赖，不改写官方 HybridCLR 设置。</summary>
    internal static class HybridCLRProfileTools
    {
        // 在全新自有位置创建并保存 Project、Recipe 和 Profile，失败时只清理本次创建内容。
        internal static HybridCLRBuildProfile Create(string assetPath)
        {
            RequireEditMode();
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                assetPath.Contains('\\') || assetPath.Split('/').Any(p => p.Length == 0 || p == "." || p == "..") ||
                Path.GetExtension(assetPath) != ".asset" || !AssetDatabase.IsValidFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/')) ||
                assetPath.StartsWith("Assets/StreamingAssets/", StringComparison.Ordinal))
            {
                throw new ArgumentException("请在 Assets 的真实文件夹内创建配置。", nameof(assetPath));
            }

            var stem = assetPath.Substring(0, assetPath.Length - ".asset".Length);
            var projectPath = stem + "-Project.asset";
            var recipePath = stem + "-Recipe.asset";
            var generated = stem + "-Generated";
            foreach (var path in new[] { assetPath, projectPath, recipePath, generated })
            {
                HybridCLRBuildBaseline.RejectLinks(path);
                if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta"))
                {
                    throw new IOException("目标已存在，拒绝覆盖：" + path);
                }
            }
            var created = new List<string>();
            try
            {
                var project = ResourceEditorOperations.CreateProject(projectPath);
                created.Add(projectPath);
                project.displayName = Path.GetFileNameWithoutExtension(assetPath);
                var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
                recipe.project = project;
                recipe.target = BuildTarget.StandaloneWindows64;
                recipe.action = ResourceBuildAction.BuildAndPublish;
                recipe.appContentCompatibilityId = "base-v1";
                AssetDatabase.CreateAsset(recipe, recipePath);
                created.Add(recipePath);
                var profile = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
                profile.recipe = recipe;
                profile.development = EditorUserBuildSettings.development;
                profile.generatedAssetDirectory = generated;
                AssetDatabase.CreateAsset(profile, assetPath);
                created.Add(assetPath);
                profile.baselineDirectory = Path.GetFullPath("Build/HybridCLR/" + AssetDatabase.AssetPathToGUID(assetPath) + "/base-v1");
                MapGeneratedResources(profile);
                EditorUtility.SetDirty(project);
                EditorUtility.SetDirty(recipe);
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssetIfDirty(project);
                AssetDatabase.SaveAssetIfDirty(recipe);
                AssetDatabase.SaveAssetIfDirty(profile);
                return profile;
            }
            catch
            {
                foreach (var path in created.AsEnumerable().Reverse())
                {
                    AssetDatabase.DeleteAsset(path);
                }

                if (AssetDatabase.IsValidFolder(generated))
                {
                    AssetDatabase.DeleteAsset(generated);
                }

                throw;
            }
        }

        // 幂等关联规范生成目录与代码选择，冲突先拒绝，项目修改通过 SerializedObject 支持 Undo。
        internal static void MapGeneratedResources(HybridCLRBuildProfile profile)
        {
            RequireEditMode();
            HybridCLRBuildPipeline.ValidateInputPaths(profile);
            if (!profile.recipe || !profile.recipe.project)
            {
                throw new InvalidDataException("请先选择资源配方及项目。");
            }

            var project = profile.recipe.project;
            var root = profile.generatedAssetDirectory.TrimEnd('/');
            ValidateCodeMappings(profile);
            foreach (var source in project.sourceDirectories)
            {
                var path = AssetDatabase.GUIDToAssetPath(source.guid);
                if (path == root && !string.IsNullOrEmpty(source.addressPrefix) || path != root &&
                    (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) || root.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("已有来源目录与代码地址映射冲突，请先在资源项目中明确调整：" + path);
                }
            }
            EnsureFolder(root);
            EnsureFolder(root + "/hybridclr");
            EnsureFolder(root + "/hybridclr/assemblies");
            EnsureFolder(root + "/hybridclr/metadata");
            var rootGuid = AssetDatabase.AssetPathToGUID(root);
            var codeGuid = AssetDatabase.AssetPathToGUID(root + "/hybridclr");
            var sourceExists = project.sourceDirectories.Any(s => s.guid == rootGuid);
            var selection = ReadCodeSelection(project, codeGuid);
            if (sourceExists && selection != null)
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("关联 HybridCLR 生成资源");
            var serialized = new SerializedObject(project);
            serialized.Update();
            if (!sourceExists)
            {
                var sources = serialized.FindProperty("sourceDirectories");
                var source = sources.GetArrayElementAtIndex(sources.arraySize++);
                source.FindPropertyRelative("guid").stringValue = rootGuid;
                source.FindPropertyRelative("addressPrefix").stringValue = "";
            }
            if (selection == null)
            {
                var id = "hybridclr-code";
                var suffix = 2;
                while (project.groups.Any(g => string.Equals(g.id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    id = "hybridclr-code-" + suffix++;
                }

                var groups = serialized.FindProperty("groups");
                var group = groups.GetArrayElementAtIndex(groups.arraySize++);
                group.FindPropertyRelative("id").stringValue = id;
                group.FindPropertyRelative("packing").enumValueIndex = (int)ResourcePacking.Together;
                var selections = group.FindPropertyRelative("selections");
                selections.arraySize = 1;
                var item = selections.GetArrayElementAtIndex(0);
                item.FindPropertyRelative("guid").stringValue = codeGuid;
                item.FindPropertyRelative("address").stringValue = "";
                item.FindPropertyRelative("aliases").arraySize = 0;
                var extensions = item.FindPropertyRelative("extensions");
                extensions.arraySize = 2;
                extensions.GetArrayElementAtIndex(0).stringValue = ".bytes";
                extensions.GetArrayElementAtIndex(1).stringValue = ".json";
            }
            serialized.ApplyModifiedProperties();
        }

        // 用当前 Player 脚本图及外部 DLL 预览配置，不执行编译、原生生成或安装。
        internal static HybridCLRProfileInspection Inspect(HybridCLRBuildProfile profile)
        {
            var result = new HybridCLRProfileInspection();
            if (!profile)
            {
                result.Errors.Add("先新建或选择一个 HybridCLR 配置。");
                return result;
            }
            // 累积独立检查的错误，避免首个失败遮住其他可诊断输入。
            void Check(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    result.Errors.Add(error.Message);
                }
            }
            Check(() =>
            {
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(profile)))
                {
                    throw new InvalidDataException("配置必须保存为 Assets 中的资产。");
                }

                HybridCLRBuildPipeline.ValidateInputPaths(profile);
                HybridCLRBuildPipeline.ResolveBaseProfile(profile);
                if (profile.recipe && profile.recipe.project)
                {
                    ValidateCodeMappings(profile);
                }
            });
            if (!profile.recipe || !profile.recipe.project)
            {
                result.Errors.Add("选择资源配方，并在配方中指定资源项目。");
                return result;
            }
            Check(() =>
            {
                _ = new ResourceAddress(profile.recipe.project.projectId, "hybridclr/manifest.json");
                _ = new ResourceAddress(profile.recipe.appContentCompatibilityId, "compatibility");
                if (profile.recipe.target != BuildTarget.StandaloneWindows64 || profile.recipe.action != ResourceBuildAction.BuildAndPublish || profile.recipe.generateReferences)
                {
                    throw new InvalidDataException("配方应使用 Windows64、构建并发布，且不生成 C# 地址引用。");
                }

                var root = profile.generatedAssetDirectory.TrimEnd('/');
                var code = root + "/hybridclr";
                if (!AssetDatabase.IsValidFolder(root) || !AssetDatabase.IsValidFolder(code))
                {
                    throw new InvalidDataException("代码来源目录已不存在，历史 GUID 不能作为有效文件夹，请显式关联生成资源。");
                }

                var rootGuid = AssetDatabase.AssetPathToGUID(root);
                var codeGuid = AssetDatabase.AssetPathToGUID(code);
                if (string.IsNullOrEmpty(rootGuid) || string.IsNullOrEmpty(codeGuid) ||
                    !profile.recipe.project.sourceDirectories.Any(s => s.guid == rootGuid && string.IsNullOrEmpty(s.addressPrefix)) ||
                    ReadCodeSelection(profile.recipe.project, codeGuid) == null)
                {
                    throw new InvalidDataException("代码来源目录与资源选择未关联，使用“关联生成资源”后保存资源项目。");
                }
            });
            Check(() =>
            {
                var names = HybridCLRBuildPipeline.ConfiguredNames();
                var graph = CompilationPipeline.GetAssemblies(AssembliesType.Player).ToDictionary(a => a.name, StringComparer.Ordinal);
                var external = HybridCLRBuildPipeline.ExternalCandidates(names).ToDictionary(a => a.Name, StringComparer.Ordinal);
                var records = new List<HybridCLRAssemblyRecord>();
                foreach (var name in names)
                {
                    var scripted = graph.TryGetValue(name, out var assembly);
                    if (scripted && external.ContainsKey(name))
                    {
                        throw new InvalidDataException("程序集同时来自脚本和外部 DLL：" + name);
                    }

                    if (scripted)
                    {
                        records.Add(new HybridCLRAssemblyRecord
                        {
                            Name = name,
                            References = assembly.assemblyReferences.Select(a => a.name)
                        .Concat(assembly.compiledAssemblyReferences.Select(Path.GetFileNameWithoutExtension)).Distinct(StringComparer.Ordinal).ToArray()
                        });
                    }
                    else if (external.TryGetValue(name, out var dll))
                    {
                        records.Add(dll);
                    }
                    else
                    {
                        throw new InvalidDataException("未找到已登记程序集的 Player 脚本或外部 DLL 来源：" + name);
                    }
                }
                var selected = HybridCLRBuildPipeline.SelectAssemblies(profile, new HybridCLRAssemblySet { HotUpdate = records, Aot = Array.Empty<HybridCLRAssemblyRecord>() });
                result.Assemblies = selected.HotUpdate.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                result.Dependencies = profile.hotUpdateAssemblies == null || profile.hotUpdateAssemblies.Length == 0 ? Array.Empty<string>() :
                    result.Assemblies.Except(profile.hotUpdateAssemblies, StringComparer.Ordinal).ToArray();
            });
            if (EditorUtility.IsDirty(profile) || EditorUtility.IsDirty(profile.recipe) || EditorUtility.IsDirty(profile.recipe.project))
            {
                result.Warnings.Add("配置存在未保存修改，正式构建前需显式保存。");
            }

            if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone) != ScriptingImplementation.IL2CPP ||
                PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Standalone) != ApiCompatibilityLevel.NET_4_6)
            {
                result.Warnings.Add("当前 Editor 可编辑和预检配置；原生构建需在接收工程显式配置 IL2CPP / .NET Framework。");
            }

            result.Warnings.Add("依赖预览依据当前 Player 脚本图与外部 DLL；正式发布会重新编译并按实际程序集身份复核。");
            return result;
        }

        // 要求代码目录选择唯一且只含标准扩展名，不静默覆盖用户地址或别名。
        private static ResourceSelection ReadCodeSelection(ResourceProject project, string guid)
        {
            var matches = project.groups.SelectMany(g => g.selections).Where(s => s.guid == guid).ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidDataException("代码目录选择必须唯一，请显式调整重复选择，未覆盖用户配置。");
            }

            var selection = matches.SingleOrDefault();
            if (selection != null && (!string.IsNullOrEmpty(selection.address) || selection.aliases != null && selection.aliases.Length != 0 ||
                selection.extensions == null || !selection.extensions.OrderBy(e => e, StringComparer.Ordinal).SequenceEqual(new[] { ".bytes", ".json" })))
            {
                throw new InvalidDataException("代码目录选择必须使用标准 .bytes/.json 且不含地址覆盖或文件夹别名，请显式调整，未覆盖用户配置。");
            }

            return selection;
        }

        // 拒绝组 ID 碰撞及其他来源对规范 hybridclr 地址的占用。
        private static void ValidateCodeMappings(HybridCLRBuildProfile profile)
        {
            var project = profile.recipe.project;
            if (project.groups.Select(g => g.id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != project.groups.Count)
            {
                throw new InvalidDataException("资源组 ID 存在大小写重复，请先在资源项目中明确调整。");
            }

            var expected = profile.generatedAssetDirectory.TrimEnd('/');
            foreach (var selection in project.groups.SelectMany(g => g.selections))
            {
                var path = AssetDatabase.GUIDToAssetPath(selection.guid);
                if (string.IsNullOrEmpty(path) || path.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(expected + "/", StringComparison.OrdinalIgnoreCase) || selection.extensions != null && selection.extensions.Length != 0 &&
                    !selection.extensions.Any(e => string.Equals(e, ".bytes", StringComparison.OrdinalIgnoreCase) || string.Equals(e, ".json", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (var source in project.sourceDirectories)
                {
                    var sourcePath = AssetDatabase.GUIDToAssetPath(source.guid);
                    if (string.IsNullOrEmpty(sourcePath) || path != sourcePath && !path.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relative = path == sourcePath ? "" : path.Substring(sourcePath.Length + 1);
                    var address = !string.IsNullOrEmpty(selection.address) ? selection.address :
                        string.IsNullOrEmpty(source.addressPrefix) ? relative : source.addressPrefix.TrimEnd('/') + (relative.Length == 0 ? "" : "/" + relative);
                    var code = address.Equals("hybridclr", StringComparison.OrdinalIgnoreCase) ||
                        address.Equals(HybridCLRManifest.ManifestPath, StringComparison.OrdinalIgnoreCase) ||
                        address.Equals("hybridclr/assemblies", StringComparison.OrdinalIgnoreCase) || address.StartsWith("hybridclr/assemblies/", StringComparison.OrdinalIgnoreCase) ||
                        address.Equals("hybridclr/metadata", StringComparison.OrdinalIgnoreCase) || address.StartsWith("hybridclr/metadata/", StringComparison.OrdinalIgnoreCase) ||
                        address.Length == 0 && AssetDatabase.IsValidFolder(path + "/hybridclr");
                    if (code)
                    {
                        throw new InvalidDataException("已有 HybridCLR 代码映射来自其他目录，请先明确移除或调整旧选择，未修改用户数据：" + path);
                    }
                }
            }
        }

        // 递归补齐 Assets 文件夹，已有有效文件夹保持原 GUID。
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent))
            {
                throw new InvalidDataException("Invalid Assets folder: " + path);
            }

            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path))))
            {
                throw new IOException("Cannot create Assets folder: " + path);
            }
        }

        // 仅允许在非 Play、编译或 Player 构建期间显式编辑配置。
        private static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || BuildPipeline.isBuildingPlayer)
            {
                throw new InvalidOperationException("请在非 Play、编译或构建的 Editor 中显式编辑配置。");
            }
        }
    }
}
