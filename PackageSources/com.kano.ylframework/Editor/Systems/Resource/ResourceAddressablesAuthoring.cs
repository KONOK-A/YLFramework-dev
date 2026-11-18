using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Animations;
using UnityEngine;

namespace YLFramework.Editor.Resource
{
    /// <summary>只读检查官方 Addressables 配置，不建立第二份资源目录或隐式修改作者数据。</summary>
    public static class ResourceAddressablesAuthoring
    {
        /// <summary>检查受控启动更新及单资产键的必要前置；返回可定位的错误，不自动修复配置。</summary>
        /// <param name="settings">应用明确提供的官方配置；为空时仅报告缺失。</param>
        /// <returns>当前配置的错误快照；没有错误时为空数组。</returns>
        public static string[] Validate(AddressableAssetSettings settings)
        {
            if (!settings)
            {
                return new[] { "Addressables settings have not been created." };
            }

            var errors = new List<string>();
            if (!settings.DisableCatalogUpdateOnStartup)
            {
                errors.Add("Controlled startup updates require automatic Catalog update checks to be disabled before initialization.");
            }

            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            var keys = new Dictionary<string, AddressableAssetEntry>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (IsEditorOnly(entry.MainAssetType))
                {
                    errors.Add("Editor-only Addressables asset would be stripped from the build: " + entry.AssetPath);
                }
                if (string.IsNullOrWhiteSpace(entry.address))
                {
                    errors.Add("An exact nonempty address is required: " + entry.guid);
                    continue;
                }

                foreach (string key in new[] { entry.address, entry.guid })
                {
                    if (keys.TryGetValue(key, out var previous) && previous.guid != entry.guid)
                    {
                        errors.Add("Ambiguous Addressables key '" + key + "': " + previous.AssetPath + " / " + entry.AssetPath);
                    }
                    else
                    {
                        keys[key] = entry;
                    }
                }
            }
            return errors.ToArray();
        }

        private static bool IsEditorOnly(Type type)
        {
            // These authoring types have official runtime projections in both supported Addressables versions.
            if (type == typeof(AnimatorController) || type == typeof(SceneAsset) || type == typeof(DefaultAsset) ||
                type.FullName == "UnityEditor.Audio.AudioMixerController" || type.FullName == "UnityEditor.Audio.AudioMixerGroupController")
            {
                return false;
            }

            return type.Assembly.IsDefined(typeof(AssemblyIsEditorAssembly), true) || BuildUtility.IsEditorAssembly(type.Assembly);
        }

        /// <summary>从官方配置查找唯一主资产；缺失或歧义时拒绝，不以第一项作为静默回退。</summary>
        /// <param name="settings">应用明确提供的官方配置。</param>
        /// <param name="key">区分大小写的官方 address 或资产 GUID。</param>
        /// <returns>对应官方条目，只借读，不更改配置。</returns>
        public static AddressableAssetEntry FindUniqueEntry(AddressableAssetSettings settings, string key)
        {
            if (!settings || string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("An existing settings asset and an exact key are required.");
            }

            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            AddressableAssetEntry result = null;
            foreach (var entry in entries)
            {
                if (entry.address != key && entry.guid != key)
                {
                    continue;
                }
                if (result != null && result.guid != entry.guid)
                {
                    throw new InvalidOperationException("Ambiguous Addressables key: " + key);
                }
                result = entry;
            }
            return result ?? throw new InvalidOperationException("No Addressables entry matches: " + key);
        }
    }
}
