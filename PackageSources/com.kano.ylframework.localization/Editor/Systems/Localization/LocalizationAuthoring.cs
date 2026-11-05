using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using YLFramework.Localization;

namespace YLFramework.Editor.Localization
{
    /// <summary>只编辑显式目标根及其语言资产；不安装全工程删除/自动保存监听。</summary>
    internal static class LocalizationAuthoring
    {
        /// <summary>以已保存根的资产 GUID 计算语言存放目录，不在此创建目录。</summary>
        public static string GetLanguageFolder(MultiLanguageConfigs root)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(root));
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("Save the YL localization root asset before authoring languages.");
            }

            return "Assets/YLFrameworkData/Localization/" + guid;
        }

        /// <summary>创建独立语言资产并记录创建及根引用的 Undo，根标脏后等待显式保存。</summary>
        /// <param name="root">接收语言引用的已保存根，创建前须通过现有配置校验。</param>
        /// <param name="language">尚未在根中存在且不是 Unknown 的已定义语言。</param>
        /// <returns>包含已有条目 ID、类型和备注，但未复制翻译内容的新语言资产。</returns>
        public static LanguageConfig CreateLanguage(MultiLanguageConfigs root, SystemLanguage language)
        {
            if (!root)
            {
                throw new ArgumentNullException(nameof(root));
            }

            RequireValid(root);
            if (!Enum.IsDefined(typeof(SystemLanguage), language) || language == SystemLanguage.Unknown || root.GetLanguageConfigData(language))
            {
                throw new InvalidOperationException("Unknown or duplicate language: " + language);
            }

            var folder = GetLanguageFolder(root);
            EnsureFolder(folder);
            var asset = ScriptableObject.CreateInstance<LanguageConfig>();
            asset.language = language;
            asset.displayName = language.ToString();
            // 新语言沿用现有所有ID/类型，不拷贝另一语言的内容冒充翻译。
            var ids = new HashSet<int>();
            foreach (var source in root.Languages)
            {
                foreach (var entry in source.languagesInfo)
                {
                    if (ids.Add(entry.id))
                    {
                        asset.languagesInfo.Add(new LanguageInfo { id = entry.id, type = entry.type, notes = entry.notes });
                    }
                }
            }

            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + language + ".asset");
            AssetDatabase.CreateAsset(asset, path);
            Undo.RegisterCreatedObjectUndo(asset, "Create YL language");
            Undo.RecordObject(root, "Add YL language reference");
            root.Languages.Add(asset);
            EditorUtility.SetDirty(root);
            return asset;
        }

        /// <summary>在根的每个语言中新增同 ID 条目；先检查冲突，再逐资产记录 Undo、失效缓存并标脏。</summary>
        /// <param name="root">至少包含一个语言且配置有效的目标根。</param>
        /// <param name="id">所有目标语言中均尚未使用的条目 ID。</param>
        /// <param name="type">新增条目的内容类型。</param>
        /// <param name="notes">新条目的作者备注，不填充翻译内容。</param>
        public static void AddEntry(MultiLanguageConfigs root, int id, MultiLanguageType type, string notes = null)
        {
            RequireValid(root);
            if (root.Languages.Count == 0)
            {
                throw new InvalidOperationException("Create a language first.");
            }

            foreach (var language in root.Languages)
            {
                if (language.GetLanguageInfo(id) != null)
                {
                    throw new InvalidOperationException($"Localization '{root.name}': ID {id} already exists in {language.language}.");
                }
            }

            foreach (var language in root.Languages)
            {
                Undo.RecordObject(language, "Add YL localization entry");
                language.languagesInfo.Add(new LanguageInfo { id = id, type = type, notes = notes });
                language.ClearCache();
                EditorUtility.SetDirty(language);
            }
        }

        /// <summary>按语言复制已有源条目并替换 ID，跳过缺失源条目的语言；目标冲突时修改前报错。</summary>
        /// <param name="root">本次复制范围内的配置根。</param>
        /// <param name="sourceId">至少在一个语言中存在的源 ID。</param>
        /// <param name="newId">所有语言均未使用的目标 ID；成功写入记录 Undo 并标脏，不自动保存。</param>
        public static void DuplicateEntry(MultiLanguageConfigs root, int sourceId, int newId)
        {
            RequireValid(root);
            var copies = new Dictionary<LanguageConfig, LanguageInfo>();
            foreach (var language in root.Languages)
            {
                if (language.GetLanguageInfo(newId) != null)
                {
                    throw new InvalidOperationException("Duplicate destination ID: " + newId);
                }

                var source = language.GetLanguageInfo(sourceId);
                if (source == null)
                {
                    continue;
                }

                var copy = JsonUtility.FromJson<LanguageInfo>(JsonUtility.ToJson(source));
                copy.id = newId;
                copies.Add(language, copy);
            }
            if (copies.Count == 0)
            {
                throw new InvalidOperationException("Missing source ID: " + sourceId);
            }

            foreach (var pair in copies)
            {
                Undo.RecordObject(pair.Key, "Duplicate YL localization entry");
                pair.Key.languagesInfo.Add(pair.Value);
                pair.Key.ClearCache();
                EditorUtility.SetDirty(pair.Key);
            }
        }

        /// <summary>从根的所有语言中移除指定 ID，记录受影响语言的 Undo 并标脏，不删除语言资产。</summary>
        public static void RemoveEntry(MultiLanguageConfigs root, int id)
        {
            RequireValid(root);
            foreach (var language in root.Languages)
            {
                if (language.GetLanguageInfo(id) == null)
                {
                    continue;
                }

                Undo.RecordObject(language, "Remove YL localization entry");
                language.languagesInfo.RemoveAll(entry => entry.id == id);
                language.ClearCache();
                EditorUtility.SetDirty(language);
            }
        }

        /// <summary>仅移除根中的指定语言引用，记录根的 Undo 并标脏，保留语言文件及其他根的引用。</summary>
        public static void RemoveLanguageReference(MultiLanguageConfigs root, LanguageConfig language)
        {
            if (!root || !root.Languages.Contains(language))
            {
                return;
            }

            Undo.RecordObject(root, "Remove YL language reference");
            root.Languages.Remove(language);
            root.ClearCache();
            EditorUtility.SetDirty(root);
            // 引用可共享；不因移除列表项而删除语言文件或递归删除外来目录。
        }

        /// <summary>按运行时同一套结构与键唯一性规则校验目标根，不修复或保存配置。</summary>
        public static void RequireValid(MultiLanguageConfigs root) => LocalizationManager.Validate(new[] { root });

        /// <summary>通过资产数据库递归补建目标路径中缺失的目录。</summary>
        private static void EnsureFolder(string path)
        {
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
            }
        }
    }
}
