using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    internal sealed class HybridCLRBuildInputs
    {
        private readonly AssetInput _profile;
        private readonly string _settings;
        private AssetInput[] _authoring;

        private HybridCLRBuildInputs(HybridCLRBuildProfile profile)
        {
            _profile = new AssetInput(profile);
            _settings = HybridCLRBuildPipeline.SettingsFingerprint(profile);
            CaptureAuthoring(profile);
        }

        internal static HybridCLRBuildInputs Capture(HybridCLRBuildProfile profile) => new(profile);

        internal HybridCLRBuildProfile Reload()
        {
            // Native builds can unload SOs; a retained managed wrapper is not an input authority.
            var profile = _profile.Reload<HybridCLRBuildProfile>();
            HybridCLRBuildPipeline.ValidateResourceInputs(profile);
            if (HybridCLRBuildPipeline.SettingsFingerprint(profile) != _settings)
            {
                throw new InvalidDataException("The saved HybridCLR execution inputs or native settings changed during the build.");
            }
            foreach (var input in _authoring ?? Array.Empty<AssetInput>())
            {
                input.Reload<UnityEngine.Object>();
            }
            return profile;
        }

        // 仅在本工具即将更改自有 Entries 时解除旧快照；官方构建结束后立即冻结其真实结果。
        internal void BeginResourceWrite()
        {
            Reload();
            _authoring = null;
        }

        internal void CaptureAuthoring(HybridCLRBuildProfile profile)
        {
            var settings = profile.addressableSettings;
            _authoring = settings.groups.Where(group => group).Select(group => (UnityEngine.Object)group)
                .Concat(settings.groups.Where(group => group).SelectMany(group => group.Schemas).Where(schema => schema))
                .Prepend(settings).Distinct().Select(asset => new AssetInput(asset)).ToArray();
        }

        private sealed class AssetInput
        {
            private readonly string _guid;
            private readonly long _localId;
            private readonly string _fileHash;
            private readonly string _metaHash;
            private readonly Type _type;
            private readonly JObject _definition;

            internal AssetInput(UnityEngine.Object asset)
            {
                if (!asset || !EditorUtility.IsPersistent(asset) || EditorUtility.IsDirty(asset) ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _guid, out _localId))
                {
                    throw new InvalidDataException("Clean persisted HybridCLR inputs with stable GUID and local file ID are required.");
                }
                var path = AssetDatabase.GetAssetPath(asset);
                _type = asset.GetType();
                _fileHash = FileHash(path);
                _metaHash = FileHash(path + ".meta");
                _definition = Definition(asset);
            }

            internal T Reload<T>() where T : UnityEngine.Object
            {
                var path = AssetDatabase.GUIDToAssetPath(_guid);
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || FileHash(path) != _fileHash || FileHash(path + ".meta") != _metaHash)
                {
                    throw new InvalidDataException("A frozen HybridCLR input was removed or its persisted bytes changed.");
                }
                var asset = AssetDatabase.LoadAllAssetsAtPath(path).OfType<T>().SingleOrDefault(value =>
                    value && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id) && guid == _guid && id == _localId);
                if (!asset || !EditorUtility.IsPersistent(asset) || asset.GetType() != _type)
                {
                    throw new InvalidDataException("A frozen HybridCLR input changed identity or type: " + path);
                }
                if (EditorUtility.IsDirty(asset))
                {
                    throw new InvalidDataException("A frozen HybridCLR input has unsaved changes: " + path);
                }
                var definition = Definition(asset);
                if (!JToken.DeepEquals(_definition, definition))
                {
                    throw new InvalidDataException($"A frozen HybridCLR input changed in-memory definition: {path} ({FirstDifference(_definition, definition)}).");
                }
                return asset;
            }

            private static string FileHash(string path)
            {
                HybridCLRBuildBaseline.RejectLinks(path);
                if (!File.Exists(path))
                {
                    throw new InvalidDataException("A frozen HybridCLR input file or metadata is missing.");
                }
                return HybridCLRManifest.Hash(File.ReadAllBytes(path));
            }

            private static JObject Definition(UnityEngine.Object asset)
            {
                var serialized = JObject.Parse(EditorJsonUtility.ToJson(asset));
                var definition = serialized["MonoBehaviour"] as JObject ?? serialized;
                if (asset is AddressableAssetSettings)
                {
                    FillOfficialProviderDefault(definition, "m_AssetBundleProviderType", typeof(AssetBundleProvider));
                    FillOfficialProviderDefault(definition, "m_BundledAssetProviderType", typeof(BundledAssetProvider));
                }
                var references = new JObject();
                using var data = new SerializedObject(asset);
                using var property = data.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        continue;
                    }
                    var token = definition.SelectToken(property.propertyPath.Replace(".Array.data[", "["));
                    var value = property.objectReferenceValue;
                    JToken identity;
                    if (!value)
                    {
                        if (!ReferenceEquals(value, null) || property.objectReferenceInstanceIDValue != 0)
                        {
                            throw new InvalidDataException("A serialized HybridCLR input reference is no longer live.");
                        }
                        identity = JValue.CreateNull();
                    }
                    else
                    {
                        if (!EditorUtility.IsPersistent(value) || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id))
                        {
                            throw new InvalidDataException("A serialized HybridCLR input reference must have a persisted identity.");
                        }
                        identity = new JObject { ["guid"] = guid, ["localId"] = id };
                    }
                    // Preserve all reference slots, including native fields omitted by EditorJsonUtility.
                    references[property.propertyPath] = identity;
                    token?.Replace(identity.DeepClone());
                }
                return new JObject { ["definition"] = serialized, ["references"] = references };
            }

            private static void FillOfficialProviderDefault(JObject definition, string field, Type fallback)
            {
                // Addressables 2.x 在冷读时填这两个空默认值；自定义和部分填写的值仍按原样比较。
                if (definition[field] is not JObject provider || !provider.ContainsKey("m_AssemblyName") || !provider.ContainsKey("m_ClassName") ||
                    !string.IsNullOrEmpty((string)provider["m_AssemblyName"]) || !string.IsNullOrEmpty((string)provider["m_ClassName"]))
                {
                    return;
                }
                provider["m_AssemblyName"] = fallback.Assembly.FullName;
                provider["m_ClassName"] = fallback.FullName;
            }

            private static string FirstDifference(JToken expected, JToken actual)
            {
                if (expected is JObject expectedObject && actual is JObject actualObject)
                {
                    foreach (var property in expectedObject.Properties())
                    {
                        var value = actualObject[property.Name];
                        if (!JToken.DeepEquals(property.Value, value))
                        {
                            return FirstDifference(property.Value, value);
                        }
                    }
                    return actualObject.Properties().FirstOrDefault(property => expectedObject.Property(property.Name) == null)?.Path ?? expected.Path;
                }
                if (expected is JArray expectedArray && actual is JArray actualArray && expectedArray.Count == actualArray.Count)
                {
                    for (var index = 0; index < expectedArray.Count; index++)
                    {
                        if (!JToken.DeepEquals(expectedArray[index], actualArray[index]))
                        {
                            return FirstDifference(expectedArray[index], actualArray[index]);
                        }
                    }
                }
                return expected.Path;
            }
        }
    }
}
