using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Animations;
using UnityEngine;
#if YL_ADDRESSABLES_LEGACY_PLAYER_DATA
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceProviders;
#endif
using BuildUtility = UnityEditor.AddressableAssets.Build.BuildUtility;

namespace YLFramework.Editor.Resource
{
    /// <summary>检查官方 Addressables 配置，不建立第二份资源目录。</summary>
    /// <remarks>检查不写入资产或 .meta，也不主动标记资产为脏；官方枚举可刷新内存中的 IsFolder 和 SubAssets 等缓存。</remarks>
    public static class ResourceAddressablesAuthoring
    {
        /// <summary>检查受控启动更新及单资产键的必要前置；返回可定位的错误，不自动修复配置。</summary>
        /// <param name="settings">应用明确提供的官方配置；为空时仅报告缺失。</param>
        /// <returns>当前配置的错误快照；没有错误时为空数组。</returns>
        public static string[] Validate(AddressableAssetSettings settings) => Inspect(settings, validateStartup: true).Errors.ToArray();

        internal sealed class PublishedKeys
        {
            internal readonly List<string> Errors = new();
            internal readonly List<string> Warnings = new();
            internal readonly HashSet<string> Addresses = new(StringComparer.Ordinal);
            internal readonly HashSet<string> Labels = new(StringComparer.Ordinal);
        }

        internal static PublishedKeys Inspect(AddressableAssetSettings settings, bool validateStartup = false)
        {
            var result = new PublishedKeys();
            if (!settings)
            {
                result.Errors.Add("Addressables settings have not been created.");
                return result;
            }

            if (validateStartup && !settings.DisableCatalogUpdateOnStartup)
            {
                result.Errors.Add("Controlled startup updates require automatic Catalog update checks to be disabled before initialization.");
            }

            var singleKeys = new Dictionary<string, AddressableAssetEntry>(StringComparer.Ordinal);
            var labelEntries = new Dictionary<string, List<AddressableAssetEntry>>(StringComparer.Ordinal);
            var registeredLabels = new HashSet<string>(settings.GetLabels(), StringComparer.Ordinal);
#if YL_ADDRESSABLES_LEGACY_PLAYER_DATA
            var playerKeys = GatherPlayerDataKeys(settings, registeredLabels);
            var locationChecks = new Dictionary<(AddressableAssetEntry Entry, bool Bundled), bool>();
            var locations = new List<ContentCatalogDataEntry>();
            var providerTypes = new HashSet<Type>();
            var playerConflicts = new HashSet<string>(StringComparer.Ordinal);
#endif
            var entries = new List<AddressableAssetEntry>();
            foreach (var group in settings.groups)
            {
                if (!group || group.entries.Count == 0)
                {
                    continue;
                }
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (!schema || !schema.IncludeInBuild)
                {
#if YL_ADDRESSABLES_LEGACY_PLAYER_DATA
                    if (!schema && group.GetSchema<PlayerDataGroupSchema>())
                    {
                        continue;
                    }
#endif
                    result.Warnings.Add(schema
                        ? $"Group '{group.Name}' is excluded by IncludeInBuild; it contributes no generated keys."
                        : $"Group '{group.Name}' has no packed Bundle schema or supported Player Data schema; custom key publication is unsupported.");
                    continue;
                }

                var disabled = new List<string>(3);
                if (!schema.IncludeAddressInCatalog)
                {
                    disabled.Add(nameof(schema.IncludeAddressInCatalog));
                }
                if (!schema.IncludeGUIDInCatalog)
                {
                    disabled.Add(nameof(schema.IncludeGUIDInCatalog));
                }
                if (!schema.IncludeLabelsInCatalog)
                {
                    disabled.Add(nameof(schema.IncludeLabelsInCatalog));
                }
                if (disabled.Count != 0)
                {
                    result.Warnings.Add($"Group '{group.Name}' omits keys disabled by {string.Join(", ", disabled)}.");
                }

                entries.Clear();
                foreach (var raw in group.entries)
                {
                    if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(raw.guid)))
                    {
                        result.Errors.Add("Missing asset for Addressables entry: " + Describe(raw));
                        continue;
                    }
                    raw.GatherAllAssets(entries, true, true, false);
                }

                foreach (var entry in entries)
                {
                    if (!IsRuntimeAsset(entry.MainAssetType))
                    {
                        result.Errors.Add("Editor-only or unsupported Addressables asset would be stripped from the build: " + Describe(entry));
                        continue;
                    }
                    if (schema.IncludeAddressInCatalog)
                    {
                        if (string.IsNullOrWhiteSpace(entry.address))
                        {
                            result.Errors.Add("An exact nonempty address is required: " + Describe(entry));
                        }
                        else
                        {
                            RegisterSingleKey(entry.address, entry);
                            result.Addresses.Add(entry.address);
                        }
                    }
                    if (schema.IncludeGUIDInCatalog)
                    {
                        RegisterSingleKey(entry.guid, entry);
                    }
                    if (!schema.IncludeLabelsInCatalog)
                    {
                        continue;
                    }
                    // CreateKeyList removes unregistered labels; validation must not modify authoring data.
                    foreach (var label in entry.labels.Where(registeredLabels.Contains))
                    {
#if YL_ADDRESSABLES_LEGACY_PLAYER_DATA
                        CheckPlayerDataCollision(label, entry, isLabel: true);
#endif
                        if (!labelEntries.TryGetValue(label, out var members))
                        {
                            labelEntries.Add(label, members = new List<AddressableAssetEntry>());
                        }
                        members.Add(entry);
                        result.Labels.Add(label);
                    }
                }
            }

            foreach (var (key, members) in labelEntries)
            {
                if (!singleKeys.TryGetValue(key, out var single))
                {
                    continue;
                }
                foreach (var member in members.Where(entry => entry.guid != single.guid))
                {
                    // All ordinary assets can match an Object query; scenes use the separate SceneInstance query.
                    if (member.IsScene == single.IsScene)
                    {
                        var queryType = single.IsScene ? "SceneInstance" : "UnityEngine.Object";
                        result.Errors.Add($"Ambiguous Addressables key '{key}' for {queryType}: " +
                            $"{Describe(single)} / label on {Describe(member)}");
                    }
                }
            }
            return result;

            void RegisterSingleKey(string key, AddressableAssetEntry entry)
            {
#if YL_ADDRESSABLES_LEGACY_PLAYER_DATA
                CheckPlayerDataCollision(key, entry, isLabel: false);
#endif
                if (singleKeys.TryGetValue(key, out var previous) && previous.guid != entry.guid)
                {
                    result.Errors.Add($"Ambiguous Addressables key '{key}': {Describe(previous)} / {Describe(entry)}");
                }
                else
                {
                    singleKeys[key] = entry;
                }
            }

#if YL_ADDRESSABLES_LEGACY_PLAYER_DATA
            void CheckPlayerDataCollision(string key, AddressableAssetEntry bundle, bool isLabel)
            {
                if (!playerKeys.TryGetValue((key, bundle.IsScene), out var matches))
                {
                    return;
                }
                var queryType = bundle.IsScene ? typeof(SceneInstance) : typeof(UnityEngine.Object);
                foreach (var (player, playerIsLabel) in matches)
                {
                    if ((isLabel && playerIsLabel) || !HasLocation(player, false, queryType) || !HasLocation(bundle, true, queryType))
                    {
                        continue;
                    }
                    var error = $"Ambiguous Addressables key '{key}' for {queryType}: built-in {Describe(player)} / bundle {Describe(bundle)}";
                    if (playerConflicts.Add(error))
                    {
                        result.Errors.Add(error);
                    }
                }
            }

            bool HasLocation(AddressableAssetEntry entry, bool bundled, Type queryType)
            {
                if (!locationChecks.TryGetValue((entry, bundled), out var hasLocation))
                {
                    locations.Clear();
                    var provider = bundled ? typeof(BundledAssetProvider).FullName :
                        entry.IsScene ? string.Empty : typeof(LegacyResourcesProvider).FullName;
                    // Probe runtime types for the active target without running the bundle build.
                    // CreateKeyList can prune invalid labels from a HashSet shared with the authoring entry.
                    entry.CreateCatalogEntries(locations, bundled, provider, null, null, null, providerTypes,
                        includeAddress: true, includeGUID: false, includeLabels: false, assetsInBundle: null);
                    hasLocation = locations.Any(location => queryType.IsAssignableFrom(location.ResourceType));
                    locationChecks.Add((entry, bundled), hasLocation);
                }
                return hasLocation;
            }
#endif
        }

#if YL_ADDRESSABLES_LEGACY_PLAYER_DATA
        private static Dictionary<(string Key, bool IsScene), List<(AddressableAssetEntry Entry, bool IsLabel)>> GatherPlayerDataKeys(
            AddressableAssetSettings settings, HashSet<string> registeredLabels)
        {
            var keys = new Dictionary<(string, bool), List<(AddressableAssetEntry, bool)>>();
            var entries = new List<AddressableAssetEntry>();
            foreach (var group in settings.groups)
            {
                var schema = group ? group.GetSchema<PlayerDataGroupSchema>() : null;
                if (!schema || (!schema.IncludeBuildSettingsScenes && !schema.IncludeResourcesFolders))
                {
                    continue;
                }

                // Match BuildScriptBase.CreateLocationsForPlayerData, including the official pseudo-entry expansion.
                entries.Clear();
                group.GatherAllAssets(entries, true, true, false);
                foreach (var entry in entries)
                {
                    if ((!entry.IsInSceneList && !entry.IsInResources) ||
                        (entry.IsInSceneList && !schema.IncludeBuildSettingsScenes) ||
                        (entry.IsInResources && !schema.IncludeResourcesFolders))
                    {
                        continue;
                    }
                    Add(entry.address, entry, isLabel: false);
                    Add(entry.guid, entry, isLabel: false);
                    foreach (var label in entry.labels.Where(registeredLabels.Contains))
                    {
                        Add(label, entry, isLabel: true);
                    }
                }
            }
            return keys;

            void Add(string key, AddressableAssetEntry entry, bool isLabel)
            {
                if (string.IsNullOrEmpty(key))
                {
                    return;
                }
                var lookupKey = (key, entry.IsScene);
                if (!keys.TryGetValue(lookupKey, out var matches))
                {
                    keys.Add(lookupKey, matches = new List<(AddressableAssetEntry, bool)>());
                }
                matches.Add((entry, isLabel));
            }
        }
#endif

        private static string Describe(AddressableAssetEntry entry) =>
            $"group '{entry.parentGroup.Name}', address '{entry.address}', GUID '{entry.guid}', path '{entry.AssetPath}'";

        private static bool IsRuntimeAsset(Type type)
        {
            if (type == null || type == typeof(DefaultAsset))
            {
                return false;
            }
            // These Editor types have official runtime projections in both supported Addressables versions.
            return type == typeof(AnimatorController) || type == typeof(SceneAsset) ||
                type.FullName == "UnityEditor.Audio.AudioMixerController" || type.FullName == "UnityEditor.Audio.AudioMixerGroupController" ||
                (!type.Assembly.IsDefined(typeof(AssemblyIsEditorAssembly), true) && !BuildUtility.IsEditorAssembly(type.Assembly));
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
