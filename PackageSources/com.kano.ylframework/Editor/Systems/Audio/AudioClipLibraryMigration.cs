using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Audio;
using YLFramework.Resource;

namespace YLFramework.Editor.Audio
{
    /// <summary>一次性、逐条显式映射的配置转换。只创建新资产，不修改输入或解释旧 BundleName。</summary>
    internal static class AudioClipLibraryMigration
    {
        /// <summary>校验全部旧条目的显式映射，在新路径创建并保存迁移副本。</summary>
        /// <param name="source">只读转换来源；其组、权重和直接 Clip 引用随副本保留。</param>
        /// <param name="targetPath">已有 Assets 目录下尚无文件或元数据的正斜杠 .asset 路径。</param>
        /// <param name="mappings">以每个旧条目对象为键的完整映射；值须为有效 ProjectId 和主资产逻辑地址。</param>
        /// <returns>具有新资产身份的迁移副本，不接管或复制直接引用的 Clip 资产。</returns>
        /// <remarks>直接创建并保存资产，不登记 Undo；不会根据旧 BundleName 或资源名推断目标身份。</remarks>
        /// <exception cref="ArgumentNullException">来源已销毁或为 null，或映射为 null。</exception>
        /// <exception cref="ArgumentException">路径不合要求、目标身份已存在，或旧条目映射不完整或无效。</exception>
        public static AudioClipLibrary CreateCopy(AudioClipLibrary source, string targetPath,
            IReadOnlyDictionary<AudioClipEntry, ResourceAddress> mappings)
        {
            if (!source)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (mappings == null)
            {
                throw new ArgumentNullException(nameof(mappings));
            }

            if (string.IsNullOrEmpty(targetPath) || !targetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !targetPath.EndsWith(".asset", StringComparison.Ordinal) || targetPath.Contains('\\') ||
                targetPath.Split('/').Any(x => x.Length == 0 || x == "." || x == "..") ||
                !AssetDatabase.IsValidFolder(Path.GetDirectoryName(targetPath).Replace('\\', '/')))
            {
                throw new ArgumentException("Select an existing Assets folder and a new .asset path.", nameof(targetPath));
            }

            if (File.Exists(targetPath) || File.Exists(targetPath + ".meta") || AssetDatabase.LoadMainAssetAtPath(targetPath))
            {
                throw new ArgumentException("Conversion never overwrites an existing asset or identity.", nameof(targetPath));
            }

            var legacy = (source.groups ?? new()).Where(x => x?.entries != null).SelectMany(x => x.entries)
                .Where(IsLegacy).ToArray();
            if (legacy.Length != mappings.Count || legacy.Any(x => !mappings.TryGetValue(x, out var address) || !address.IsValid || address.SubAsset != null))
            {
                throw new ArgumentException("Map every legacy entry to an explicit ProjectId and main-asset logical address; no identity is inferred.", nameof(mappings));
            }

            var copy = UnityEngine.Object.Instantiate(source);
            try
            {
                for (var i = 0; i < (source.groups?.Count ?? 0); i++)
                {
                    var entries = source.groups[i]?.entries;
                    if (entries == null)
                    {
                        continue;
                    }

                    for (var j = 0; j < entries.Count; j++)
                    {
                        if (!IsLegacy(entries[j]))
                        {
                            continue;
                        }

                        var address = mappings[entries[j]];
                        var converted = copy.groups[i].entries[j];
                        converted.source = AudioClipSource.Resource;
                        converted.projectId = address.ProjectId;
                        converted.key = address.Path;
                        converted.bundleName = "";
                    }
                }
                AssetDatabase.CreateAsset(copy, targetPath);
                AssetDatabase.SaveAssetIfDirty(copy);
                return copy;
            }
            catch
            {
                // 失败时仅释放尚未持久化的副本；已创建资产可能保留，异常不代表完整回滚。
                if (copy && !EditorUtility.IsPersistent(copy))
                {
                    UnityEngine.Object.DestroyImmediate(copy);
                }

                throw;
            }
        }

        /// <summary>识别仅供显式迁移的旧 Resources 或 AssetBundle 条目。</summary>
        /// <param name="entry">待检查条目，可为 null。</param>
        internal static bool IsLegacy(AudioClipEntry entry) => entry != null &&
            (entry.source == AudioClipSource.Resources || entry.source == AudioClipSource.AssetBundle);

        /// <summary>为指定来源库打开逐条映射窗口。</summary>
        /// <param name="source">要生成迁移副本的来源库。</param>
        internal static void Open(AudioClipLibrary source) => AudioClipLibraryMigrationWindow.Open(source);
    }

    /// <summary>收集旧条目到正式资源地址的人工映射，并让用户选择新资产路径。</summary>
    internal sealed class AudioClipLibraryMigrationWindow : EditorWindow
    {
        private AudioClipLibrary _source;
        private readonly Dictionary<AudioClipEntry, (string Project, string Address)> _mappings = new();
        private Vector2 _scroll;

        /// <summary>打开或复用迁移窗口，替换来源并清除上一轮尚未提交的映射。</summary>
        /// <param name="source">本轮迁移的只读来源库。</param>
        internal static void Open(AudioClipLibrary source)
        {
            var window = GetWindow<AudioClipLibraryMigrationWindow>("Audio Resource Migration");
            window._source = source;
            window._mappings.Clear();
        }

        /// <summary>绘制只读来源与可编辑地址映射，确认新路径后创建副本并展示参数校验错误。</summary>
        private void OnGUI()
        {
            EditorGUILayout.HelpBox("仅创建新 GUID 副本。逐条核对正式 ResourceProject 的 ProjectId 和逻辑地址，不从旧 BundleName 或资源名推断。原配置、直接 Clip、权重和外部 Provider 保持不变。", MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Source", _source, typeof(AudioClipLibrary), false);
            }

            if (!_source)
            {
                return;
            }

            var entries = (_source.groups ?? new()).Where(x => x?.entries != null).SelectMany(x => x.entries)
                .Where(AudioClipLibraryMigration.IsLegacy).ToArray();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var entry in entries)
            {
                EditorGUILayout.LabelField(entry.source + " | " + entry.bundleName + " | " + entry.key);
                _mappings.TryGetValue(entry, out var mapped);
                mapped.Project = EditorGUILayout.TextField("ProjectId", mapped.Project);
                mapped.Address = EditorGUILayout.TextField("Resource Address", mapped.Address);
                _mappings[entry] = mapped;
                EditorGUILayout.Space();
            }
            EditorGUILayout.EndScrollView();
            if (!GUILayout.Button("创建迁移副本"))
            {
                return;
            }

            try
            {
                var explicitMap = entries.ToDictionary(x => x, x => new ResourceAddress(_mappings[x].Project, _mappings[x].Address));
                var path = EditorUtility.SaveFilePanelInProject("Create migrated Audio library", _source.name + "-Resource", "asset", "Select a new YL configuration path.");
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                Selection.activeObject = AudioClipLibraryMigration.CreateCopy(_source, path, explicitMap);
                Close();
            }
            catch (ArgumentException error)
            {
                EditorUtility.DisplayDialog("Audio Resource Migration", error.Message, "Close");
            }
        }
    }
}
