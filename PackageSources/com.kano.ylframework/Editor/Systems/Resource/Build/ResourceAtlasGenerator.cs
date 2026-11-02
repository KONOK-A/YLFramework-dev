using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace YLFramework.Editor.Resource
{
    /// <summary>只维护显式登记的自动 Atlas；最终选择、构建及 Runtime 所有权继续由既有链负责。</summary>
    public static class ResourceAtlasGenerator
    {
        private static bool _isGenerating;

        /// <summary>在明确选择的 Assets 根目录或现有子目录中新建并登记自有子目录；不会接管父目录或已有内容。</summary>
        public static ResourceAtlasConfiguration CreateConfiguration(ResourceProject project, string generationParentPath, string folderName)
        {
            ResourceEditorOperations.RequireEditableProject(project);
            ResourceBundleBuilder.ValidateEditor(EditorUserBuildSettings.activeBuildTarget);
            if (_isGenerating)
            {
                throw new InvalidOperationException("Automatic Atlas generation is already running.");
            }

            if (IsConfigured(project) || EditorUtility.IsDirty(project))
            {
                throw new InvalidOperationException("Save the Project and use its existing Atlas configuration, if registered.");
            }

            ResourceProjectPaths.Resolve(project);
            if (string.IsNullOrEmpty(generationParentPath))
            {
                throw new InvalidOperationException("请明确选择 Assets 根目录或现有子目录作为图集生成父目录；不能使用 StreamingAssets 或 Packages。");
            }

            var parentGuid = AssetDatabase.AssetPathToGUID(generationParentPath);
            var parent = ResourceAtlasPlan.ResolveGenerationParent(parentGuid);
            if (parent != generationParentPath)
            {
                throw new InvalidOperationException("图集生成父目录与其登记 GUID 不一致。");
            }

            if (string.IsNullOrEmpty(folderName) || folderName.Length > 64 || folderName.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
            {
                throw new InvalidOperationException("Atlas output folder must be a single explicit name (letters, digits, '_' or '-').");
            }

            var directory = parent + "/" + folderName;
            ResourceBuildOperation.ValidateAssetPath(directory);
            ResourceBuildOperation.RejectLinks(directory + ".meta");
            ResourceProjectPaths.ValidateProjects(project, directory);
            if (Directory.Exists(directory) || File.Exists(directory) || File.Exists(directory + ".meta"))
            {
                throw new InvalidOperationException("Refusing to adopt an existing Atlas output directory or meta: " + directory);
            }

            var projectPath = AssetDatabase.GetAssetPath(project);
            var projectGuid = AssetDatabase.AssetPathToGUID(projectPath);
            if (!HasExpectedGuid(projectPath, projectGuid))
            {
                throw new InvalidOperationException("Resource Project meta identity was externally modified; not adopted.");
            }

            var projectBytes = File.ReadAllBytes(projectPath);
            var projectJson = EditorJsonUtility.ToJson(project);
            var projectMetaHash = ResourceAtlasPlan.HashFile(projectPath + ".meta");
            var configuration = ScriptableObject.CreateInstance<ResourceAtlasConfiguration>();
            string boundProjectJson = null;
            string directoryGuid = null;
            string directoryMetaHash = null;
            string configurationGuid = null;
            string configurationMetaHash = null;
            byte[] configurationBytes = null;
            var createdDirectory = directory;
            var created = false;
            var createdConfiguration = false;
            _isGenerating = true;
            try
            {
                var guid = directoryGuid = AssetDatabase.CreateFolder(parent, folderName);
                if (string.IsNullOrEmpty(guid))
                {
                    throw new IOException("Cannot create Atlas directory: " + directory);
                }

                created = true;
                configuration.directoryGuid = guid;
                createdDirectory = AssetDatabase.GUIDToAssetPath(guid);
                if (createdDirectory != directory || !HasExpectedGuid(directory, guid))
                {
                    throw new InvalidOperationException("New Atlas directory identity was externally modified; not adopted.");
                }

                configuration.projectGuid = projectGuid;
                configuration.generationParentGuid = parentGuid;
                configuration.directoryMetaHash = directoryMetaHash = ResourceAtlasPlan.HashFile(directory + ".meta");
                var path = directory + "/AutomaticAtlases.asset";
                AssetDatabase.CreateAsset(configuration, path);
                createdConfiguration = true;
                configuration.configurationGuid = configurationGuid = AssetDatabase.AssetPathToGUID(path);
                if (!HasExpectedGuid(path, configuration.configurationGuid))
                {
                    throw new InvalidOperationException("New Atlas configuration meta was externally modified; not adopted.");
                }

                if (configuration.projectGuid != projectGuid || configuration.directoryGuid != guid ||
                    configuration.generationParentGuid != parentGuid || configuration.rules == null || configuration.rules.Count != 0 ||
                    configuration.outputs == null || configuration.outputs.Count != 0)
                {
                    throw new InvalidOperationException("New Atlas configuration ownership changed during creation; not adopted.");
                }

                configuration.configurationMetaHash = configurationMetaHash = ResourceAtlasPlan.HashFile(path + ".meta");
                RequireRegistrationInputs();
                EditorUtility.SetDirty(configuration);
                AssetDatabase.SaveAssetIfDirty(configuration);
                configurationBytes = File.ReadAllBytes(path);
                RequireRegistrationInputs();
                if (!HasExpectedGuid(directory, directoryGuid) || ResourceAtlasPlan.HashFile(directory + ".meta") != directoryMetaHash ||
                    !HasExpectedGuid(path, configurationGuid) || ResourceAtlasPlan.HashFile(path + ".meta") != configurationMetaHash)
                {
                    throw new InvalidOperationException("New Atlas ownership/meta changed during registration; not adopted.");
                }

                ResourceProjectPaths.ValidateProjects(project, directory);
                project.automaticAtlases = configuration;
                project.automaticAtlasConfigurationGuid = configurationGuid;
                boundProjectJson = EditorJsonUtility.ToJson(project);
                EditorUtility.SetDirty(project);
                AssetDatabase.SaveAssetIfDirty(project);
                if (EditorUtility.IsDirty(project) || EditorJsonUtility.ToJson(project) != boundProjectJson)
                {
                    throw new InvalidOperationException("Resource Project changed during Atlas registration; changed content was preserved.");
                }

                return configuration;
            }
            catch
            {
                // 只撤销尚未落盘且仍精确等于本次绑定的内存改动；绝不恢复旧字节覆盖回调保存的 Project。
                if (project && boundProjectJson != null && EditorJsonUtility.ToJson(project) == boundProjectJson &&
                    File.Exists(projectPath) && File.ReadAllBytes(projectPath).SequenceEqual(projectBytes) &&
                    File.Exists(projectPath + ".meta") &&
                    ResourceAtlasPlan.HashFile(projectPath + ".meta") == projectMetaHash)
                {
                    EditorJsonUtility.FromJsonOverwrite(projectJson, project);
                    EditorUtility.ClearDirty(project);
                }
                var referenced = project && (project.automaticAtlases == configuration || !string.IsNullOrEmpty(configurationGuid) &&
                    project.automaticAtlasConfigurationGuid == configurationGuid);
                if (!referenced && createdConfiguration && configurationBytes != null && File.Exists(directory + "/AutomaticAtlases.asset") &&
                    File.ReadAllBytes(directory + "/AutomaticAtlases.asset").SequenceEqual(configurationBytes) &&
                    AssetDatabase.AssetPathToGUID(directory + "/AutomaticAtlases.asset") == configurationGuid &&
                    HasExpectedGuid(directory + "/AutomaticAtlases.asset", configurationGuid) &&
                    ResourceAtlasPlan.HashFile(directory + "/AutomaticAtlases.asset.meta") == configurationMetaHash)
                {
                    AssetDatabase.DeleteAsset(directory + "/AutomaticAtlases.asset");
                }

                if (!referenced && created && Directory.Exists(createdDirectory) && AssetDatabase.AssetPathToGUID(createdDirectory) == directoryGuid &&
                    HasExpectedGuid(createdDirectory, directoryGuid) && ResourceAtlasPlan.HashFile(createdDirectory + ".meta") == directoryMetaHash &&
                    !Directory.EnumerateFileSystemEntries(createdDirectory).Any())
                {
                    AssetDatabase.DeleteAsset(createdDirectory);
                }

                if (!createdConfiguration && configuration)
                {
                    Object.DestroyImmediate(configuration);
                }

                throw;
            }
            finally
            {
                _isGenerating = false;
            }

            void RequireRegistrationInputs()
            {
                if (!project || EditorUtility.IsDirty(project) || AssetDatabase.GetAssetPath(project) != projectPath ||
                    AssetDatabase.AssetPathToGUID(projectPath) != projectGuid || EditorJsonUtility.ToJson(project) != projectJson ||
                    !File.ReadAllBytes(projectPath).SequenceEqual(projectBytes) || ResourceAtlasPlan.HashFile(projectPath + ".meta") != projectMetaHash ||
                    ResourceAtlasPlan.ResolveGenerationParent(parentGuid) != parent)
                {
                    throw new InvalidOperationException("Resource Project/output parent changed during Atlas registration; not overwritten.");
                }
            }
        }

        /// <summary>只读检查当前归属并展开规则，不导入来源、不生成或准备图集。</summary>
        public static ResourceAtlasPlan Preview(ResourceProject project) => new(project);

        /// <summary>
        /// 执行当前已保存规则并准备自有图集，返回实际计划；未配置时返回 null。
        /// 异常或取消时仅尝试恢复仍匹配最后受控写入点的资产对；未知改动保留，回滚失败另行报错，不保证无条件回滚。
        /// </summary>
        public static ResourceAtlasPlan Generate(ResourceProject project, CancellationToken cancellation = default)
        {
            ResourceBundleBuilder.ValidateEditor(EditorUserBuildSettings.activeBuildTarget);
            ResourceBundleBuilder.RejectLoadedBundles();
            if (_isGenerating)
            {
                throw new InvalidOperationException("Automatic Atlas generation is already running.");
            }

            if (!project)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (!IsConfigured(project))
            {
                return null;
            }

            var configuration = project.automaticAtlases;
            if (EditorUtility.IsDirty(project) || EditorUtility.IsDirty(configuration))
            {
                throw new InvalidOperationException("Save the Project and automatic Atlas rules before generation.");
            }

            if (EditorSettings.spritePackerMode == SpritePackerMode.Disabled)
            {
                throw new InvalidOperationException("Automatic Atlases require an enabled SpritePacker; global settings are not changed by this feature.");
            }

            cancellation.ThrowIfCancellationRequested();
            _isGenerating = true;
            try
            {
                var directory = ResourceAtlasPlan.ValidateOwnership(project);
                var configPath = AssetDatabase.GetAssetPath(configuration);
                var configBytes = File.ReadAllBytes(configPath);
                var savedConfiguration = EditorJsonUtility.ToJson(configuration);
                var configurationMetaHash = ResourceAtlasPlan.HashFile(configPath + ".meta");
                var projectPath = AssetDatabase.GetAssetPath(project);
                var projectGuid = AssetDatabase.AssetPathToGUID(projectPath);
                var projectMetaHash = ResourceAtlasPlan.HashFile(projectPath + ".meta");
                var savedProject = ResourceBundleBuilder.CanonicalDefinition(project);
                var directoryIdentity = ResourceProjectPaths.SemanticIdentity(project);
                long definitionBytes = 0;
                var projectHash = ResourceBundleBuilder.ProjectFileHash(projectPath, ref definitionBytes);
                void RequireDefinition()
                {
                    definitionBytes = 0;
                    if (!project || !configuration || project.automaticAtlases != configuration ||
                        AssetDatabase.GetAssetPath(project) != projectPath || AssetDatabase.AssetPathToGUID(projectPath) != projectGuid ||
                        AssetDatabase.GetAssetPath(configuration) != configPath || !IsConfigured(project) ||
                        ResourceProjectPaths.SemanticIdentity(project) != directoryIdentity || EditorUtility.IsDirty(project) ||
                        ResourceBundleBuilder.CanonicalDefinition(project) != savedProject ||
                        ResourceBundleBuilder.ProjectFileHash(projectPath, ref definitionBytes) != projectHash ||
                        ResourceAtlasPlan.HashFile(projectPath + ".meta") != projectMetaHash ||
                        ResourceAtlasPlan.HashFile(configPath + ".meta") != configurationMetaHash ||
                        EditorUtility.IsDirty(configuration) || EditorJsonUtility.ToJson(configuration) != savedConfiguration ||
                        !File.ReadAllBytes(configPath).SequenceEqual(configBytes))
                    {
                        throw new InvalidOperationException("Automatic Atlas Project/configuration/source directories changed during generation; not overwritten.");
                    }
                }
                if (configuration.rules == null || configuration.rules.Count > 256 || configuration.rules.Any(x => x == null))
                {
                    throw new InvalidOperationException("Invalid automatic Atlas rules.");
                }

                var directories = ResourceProjectPaths.Resolve(project, validateProjects: false);
                var candidates = configuration.rules.SelectMany(rule => ResourceAtlasPlan.SourceCandidates(rule, directories, directory)).Distinct(StringComparer.Ordinal).ToArray();
                if (candidates.Length > 16384)
                {
                    throw new InvalidOperationException("Automatic Atlas source limit exceeded.");
                }
                // 仅同步明确来源的正常 Unity 导入，不设置或保存其 Importer。否则原始 PNG 已变而导入缓存未变时可能打出旧像素。
                foreach (var path in candidates)
                {
                    cancellation.ThrowIfCancellationRequested();
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    RequireDefinition();
                }
                RequireDefinition();
                var plan = Preview(project);
                if (!plan.NeedsGeneration)
                {
                    return plan;
                }

                var changes = plan.Changes.Where(x => x.Kind == ResourceAtlasChangeKind.Create || x.Kind == ResourceAtlasChangeKind.Update).ToArray();
                var oldRecords = configuration.outputs;
                List<ResourceAtlasConfiguration.Output> writtenRecords = null;
                var writes = new AtlasWrites();
                try
                {
                    foreach (var change in changes)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        RequireDefinition();
                        RequireExpectedOutput(change, oldRecords.SingleOrDefault(x => x.key == change.Key));
                        writes.Begin(change.Path);
                        WriteAtlas(change, () =>
                        {
                            writes.Checkpoint(change.Path);
                            RequireDefinition();
                        });
                    }
                    cancellation.ThrowIfCancellationRequested();
                    var atlases = changes.Select(x => AssetDatabase.LoadAssetAtPath<SpriteAtlas>(x.Path)).ToArray();
                    SpriteAtlasUtility.PackAtlases(atlases, EditorUserBuildSettings.activeBuildTarget, false);
                    RequireDefinition();
                    foreach (var change in changes)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        writes.RequireCurrent(change.Path);
                        var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(change.Path);
                        if (!atlas || atlas.spriteCount != change.Sources.Sum(path => AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().Count()))
                        {
                            throw new InvalidOperationException("Automatic Atlas preparation failed: " + change.Path);
                        }
                    }
                    RequireDefinition();
                    var records = oldRecords.Where(record => changes.All(x => x.Key != record.key)).ToList();
                    records.AddRange(changes.Select(x => new ResourceAtlasConfiguration.Output
                    {
                        key = x.Key,
                        fileName = Path.GetFileName(x.Path),
                        guid = AssetDatabase.AssetPathToGUID(x.Path),
                        inputHash = x.InputHash,
                        assetHash = ResourceAtlasPlan.HashFile(x.Path),
                        metaHash = ResourceAtlasPlan.HashFile(x.Path + ".meta")
                    }));
                    writes.Begin(configPath);
                    configuration.outputs = writtenRecords = records.OrderBy(x => x.key, StringComparer.Ordinal).ToList();
                    var writtenConfiguration = EditorJsonUtility.ToJson(configuration);
                    EditorUtility.SetDirty(configuration);
                    AssetDatabase.SaveAssetIfDirty(configuration);
                    if (EditorUtility.IsDirty(configuration) || EditorJsonUtility.ToJson(configuration) != writtenConfiguration)
                    {
                        throw new InvalidOperationException("Automatic Atlas configuration changed during output registration; not overwritten.");
                    }

                    savedConfiguration = writtenConfiguration;
                    configBytes = File.ReadAllBytes(configPath);
                    writes.Checkpoint(configPath);
                    RequireDefinition();
                    cancellation.ThrowIfCancellationRequested();
                    var after = Preview(project);
                    RequireDefinition();
                    if (after.NeedsGeneration)
                    {
                        throw new InvalidOperationException("Automatic Atlas sources/rules changed during generation; outputs rolled back.");
                    }

                    foreach (var change in changes)
                    {
                        change.Guid = AssetDatabase.AssetPathToGUID(change.Path);
                    }

                    return plan;
                }
                catch (Exception error)
                {
                    try
                    {
                        writes.Restore();
                        if (configuration && ReferenceEquals(configuration.outputs, writtenRecords))
                        {
                            configuration.outputs = oldRecords;
                        }
                    }
                    catch (Exception rollback)
                    {
                        throw new AggregateException("Atlas generation failed and rollback could not complete; changed/unknown content was preserved. Restore registered outputs before building.", error, rollback);
                    }
                    throw;
                }
            }
            finally
            {
                _isGenerating = false;
            }
        }

        /// <summary>临写前再次验证新路径未被占用，或旧输出仍匹配登记身份与字节。</summary>
        private static void RequireExpectedOutput(ResourceAtlasChange change, ResourceAtlasConfiguration.Output record)
        {
            if (record == null)
            {
                ResourceBuildOperation.RejectLinks(change.Path);
                ResourceBuildOperation.RejectLinks(change.Path + ".meta");
                if (File.Exists(change.Path) || Directory.Exists(change.Path) || File.Exists(change.Path + ".meta"))
                {
                    throw new InvalidOperationException("Atlas Create path was occupied during generation; not adopted: " + change.Path);
                }
            }
            else if (!File.Exists(change.Path) || !File.Exists(change.Path + ".meta") || AssetDatabase.AssetPathToGUID(change.Path) != record.guid ||
                ResourceAtlasPlan.HashFile(change.Path) != record.assetHash || ResourceAtlasPlan.HashFile(change.Path + ".meta") != record.metaHash ||
                EditorUtility.IsDirty(AssetImporter.GetAtPath(change.Path)) || EditorUtility.IsDirty(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(change.Path)))
            {
                throw new InvalidOperationException("Atlas Update path changed during generation; not overwritten: " + change.Path);
            }
        }

        /// <summary>保存图集创作资产及其专属 Importer 设置，在受控写入点记录状态并核对身份。</summary>
        private static void WriteAtlas(ResourceAtlasChange change, Action checkpoint)
        {
            // 写 authoring 资产而不是只修改导入后的 SpriteAtlas；保存不删除或重建存续 .meta。
            var authoring = new SpriteAtlasAsset();
            try
            {
                authoring.Add(change.Sources.Select(AssetDatabase.LoadAssetAtPath<Texture2D>).Cast<Object>().ToArray());
                authoring.SetIncludeInBuild(false);
                SpriteAtlasAsset.Save(authoring, change.Path);
                checkpoint();
            }
            finally
            {
                Object.DestroyImmediate(authoring);
            }
            var authoringHash = ResourceAtlasPlan.HashFile(change.Path);
            AssetDatabase.ImportAsset(change.Path, ImportAssetOptions.ForceSynchronousImport);
            var guid = AssetDatabase.AssetPathToGUID(change.Path);
            if (ResourceAtlasPlan.HashFile(change.Path) != authoringHash || change.Guid != null && guid != change.Guid || !HasExpectedGuid(change.Path, guid))
            {
                throw new InvalidOperationException("Atlas authoring identity changed during import; not overwritten: " + change.Path);
            }

            checkpoint();
            if (AssetImporter.GetAtPath(change.Path) is not SpriteAtlasImporter importer)
            {
                throw new InvalidOperationException("Cannot import generated Atlas: " + change.Path);
            }

            var rule = change.Rule;
            importer.includeInBuild = false;
            var packing = importer.packingSettings;
            packing.enableRotation = rule.allowRotation;
            packing.enableTightPacking = rule.tightPacking;
            packing.padding = rule.padding;
            importer.packingSettings = packing;
            var texture = importer.textureSettings;
            texture.readable = rule.readable;
            texture.generateMipMaps = rule.mipMaps;
            texture.sRGB = rule.srgb;
            texture.filterMode = rule.filterMode;
            importer.textureSettings = texture;
            var platform = importer.GetPlatformSettings("DefaultTexturePlatform");
            platform.maxTextureSize = rule.maxTextureSize;
            platform.textureCompression = rule.compression;
            importer.SetPlatformSettings(platform);
            importer.SaveAndReimport();
            if (ResourceAtlasPlan.HashFile(change.Path) != authoringHash || AssetDatabase.AssetPathToGUID(change.Path) != guid || !HasExpectedGuid(change.Path, guid))
            {
                throw new InvalidOperationException("Generated Atlas changed during importer save: " + change.Path);
            }

            checkpoint();
        }

        /// <summary>直接核对 meta 中唯一 GUID 行，避免只信任 AssetDatabase 的缓存身份。</summary>
        private static bool HasExpectedGuid(string path, string guid)
        {
            ResourceBuildOperation.RejectLinks(path + ".meta");
            if (string.IsNullOrEmpty(guid) || !File.Exists(path + ".meta"))
            {
                return false;
            }

            var identities = File.ReadLines(path + ".meta").Where(line => line.StartsWith("guid:", StringComparison.Ordinal)).ToArray();
            return identities.Length == 1 && identities[0] == "guid: " + guid;
        }

        /// <summary>只记录实际开始写入的资产对；回滚前再次核对最后一个受控写入点，不覆盖后来的未知内容。</summary>
        private sealed class AtlasWrites
        {
            private readonly Dictionary<string, byte[]> _originals = new(StringComparer.Ordinal);
            private readonly Dictionary<string, byte[]> _expected = new(StringComparer.Ordinal);
            private readonly List<string> _paths = new();

            /// <summary>登记开始写入的资产及 meta 原字节，并建立初始检查点。</summary>
            internal void Begin(string path)
            {
                _paths.Add(path);
                foreach (var file in new[] { path, path + ".meta" })
                {
                    _originals.Add(file, Read(file));
                }

                Checkpoint(path);
            }

            /// <summary>记录本次受控写入后资产及 meta 的预期字节。</summary>
            internal void Checkpoint(string path)
            {
                _expected[path] = Read(path);
                _expected[path + ".meta"] = Read(path + ".meta");
            }

            /// <summary>要求资产对仍等于最后检查点，拒绝覆盖后来出现的未知内容。</summary>
            internal void RequireCurrent(string path)
            {
                foreach (var file in new[] { path, path + ".meta" })
                {
                    var actual = Read(file);
                    if (_expected[file] == null ? actual != null : actual == null || !actual.SequenceEqual(_expected[file]))
                    {
                        throw new InvalidOperationException("Atlas write state changed; unknown content preserved: " + file);
                    }
                }
            }

            /// <summary>逆序恢复通过检查点复验的资产对，重导入成功恢复项并汇总不能安全恢复的错误。</summary>
            internal void Restore()
            {
                var restored = new List<string>();
                var errors = new List<Exception>();
                foreach (var path in _paths.AsEnumerable().Reverse())
                {
                    try
                    {
                        RequireCurrent(path);
                        foreach (var file in new[] { path, path + ".meta" })
                        {
                            if (_originals[file] == null)
                            {
                                File.Delete(file);
                            }
                            else
                            {
                                File.WriteAllBytes(file, _originals[file]);
                            }
                        }

                        restored.Add(path);
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }

                foreach (var path in restored.Where(File.Exists))
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                if (errors.Count != 0)
                {
                    throw new AggregateException("Some Atlas writes could not be safely rolled back.", errors);
                }
            }

            /// <summary>拒绝链接或目录占位，读取现有文件字节，缺失时返回 null。</summary>
            private static byte[] Read(string path)
            {
                ResourceBuildOperation.RejectLinks(path);
                if (Directory.Exists(path))
                {
                    throw new InvalidOperationException("An Atlas file was replaced by a directory: " + path);
                }

                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
        }

        /// <summary>显式删除单个失效输出，先复验完整归属及当前引用；不会自动删除仍被使用或未知的资产。</summary>
        public static void DeleteObsolete(ResourceProject project, string outputKey)
        {
            ResourceBundleBuilder.ValidateEditor(EditorUserBuildSettings.activeBuildTarget);
            ResourceBundleBuilder.RejectLoadedBundles();
            if (_isGenerating)
            {
                throw new InvalidOperationException("Automatic Atlas generation is already running.");
            }

            if (EditorUtility.IsDirty(project) || EditorUtility.IsDirty(project.automaticAtlases))
            {
                throw new InvalidOperationException("Save the Atlas rules before removing obsolete outputs.");
            }

            var plan = Preview(project);
            var change = plan.Changes.SingleOrDefault(x => x.Key == outputKey && x.Kind == ResourceAtlasChangeKind.Obsolete);
            if (change == null)
            {
                throw new InvalidOperationException("Only a verified obsolete output can be removed: " + outputKey);
            }

            var files = ReferenceCandidates().Where(path => path != change.Path).ToArray();
            foreach (var path in files)
            {
                if (AssetDatabase.GetDependencies(path, false).Contains(change.Path, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("Obsolete Atlas is still referenced; not deleted: " + path);
                }
            }

            foreach (var guid in AssetDatabase.FindAssets("t:ResourceProject"))
            {
                var definition = AssetDatabase.LoadAssetAtPath<ResourceProject>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var selection in definition.groups.Where(x => x != null).SelectMany(x => x.selections))
                {
                    if (selection == null)
                    {
                        continue;
                    }

                    var selected = AssetDatabase.GUIDToAssetPath(selection.guid);
                    var registered = definition.automaticAtlases ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(definition.automaticAtlases))?.Replace('\\', '/') : null;
                    var excluded = registered != null && ResourceAtlasPlan.Within(registered, change.Path) && ResourceAtlasPlan.Within(selected, registered);
                    var includedExtension = selection.extensions == null || selection.extensions.Length == 0 || selection.extensions.Contains(".spriteatlasv2", StringComparer.OrdinalIgnoreCase);
                    if (selection.guid == change.Guid || !excluded && includedExtension && AssetDatabase.IsValidFolder(selected) && ResourceAtlasPlan.Within(selected, change.Path))
                    {
                        throw new InvalidOperationException("Obsolete Atlas is still selected by a ResourceProject: " + definition.projectId);
                    }
                }
            }
            var configuration = project.automaticAtlases;
            var configPath = AssetDatabase.GetAssetPath(configuration);
            var writes = new AtlasWrites();
            var oldRecords = configuration.outputs;
            try
            {
                RequireExpectedOutput(change, oldRecords.Single(x => x.key == change.Key));
                writes.Begin(change.Path);
                if (!AssetDatabase.DeleteAsset(change.Path))
                {
                    throw new IOException("Cannot delete owned obsolete Atlas: " + change.Path);
                }

                if (File.Exists(change.Path) || File.Exists(change.Path + ".meta"))
                {
                    throw new InvalidOperationException("Deleted Atlas path was occupied by new content; not adopted.");
                }

                writes.Checkpoint(change.Path);
                writes.Begin(configPath);
                configuration.outputs = oldRecords.Where(x => x.key != change.Key).ToList();
                EditorUtility.SetDirty(configuration);
                AssetDatabase.SaveAssetIfDirty(configuration);
                writes.Checkpoint(configPath);
            }
            catch
            {
                configuration.outputs = oldRecords;
                writes.Restore();
                throw;
            }
        }

        /// <summary>枚举 Assets 与只读 Packages 的非目录资产，用于定向检查失效图集引用。</summary>
        private static IEnumerable<string> ReferenceCandidates() => AssetDatabase.GetAllAssetPaths().Where(path =>
            (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) && !AssetDatabase.IsValidFolder(path));

        /// <summary>仅返回已有效生成的当前输出；待创建或更新时拒绝继续依赖分析，失效项不进入构建。</summary>
        internal static IReadOnlyList<ResourceAtlasChange> CurrentOutputs(ResourceProject project)
        {
            if (!IsConfigured(project))
            {
                return Array.Empty<ResourceAtlasChange>();
            }

            var plan = Preview(project);
            if (plan.NeedsGeneration)
            {
                throw new InvalidOperationException("Automatic Atlases require generation/update before dependency analysis. Use Generate or the formal Build entry.\n" + plan);
            }

            return plan.Changes.Where(x => x.Kind == ResourceAtlasChangeKind.Current).ToList().AsReadOnly();
        }

        /// <summary>检查可见配置与隐藏登记 GUID 是否一致，已登记但断开时明确失败而非视为未配置。</summary>
        internal static bool IsConfigured(ResourceProject project)
        {
            if (!project)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (!project.automaticAtlases)
            {
                if (!string.IsNullOrEmpty(project.automaticAtlasConfigurationGuid))
                {
                    throw new InvalidOperationException("Registered automatic Atlas configuration is missing/disconnected; restore its original reference and GUID before building.");
                }

                return false;
            }
            if (project.automaticAtlasConfigurationGuid != AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project.automaticAtlases)))
            {
                throw new InvalidOperationException("Automatic Atlas configuration is not the one registered to this Project.");
            }

            return true;
        }
    }
}
