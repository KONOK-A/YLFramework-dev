using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;

namespace YLFramework.Integrations.HybridCLR.Editor
{
    internal sealed class HybridCLRBuildInputs
    {
        private readonly AssetInput _profile;
        private readonly AssetInput _recipe;
        private readonly AssetInput _project;
        private readonly string _settings;

        private HybridCLRBuildInputs(HybridCLRBuildProfile profile)
        {
            _profile = new AssetInput(profile);
            _recipe = new AssetInput(profile.recipe);
            _project = new AssetInput(profile.recipe.project);
            _settings = HybridCLRBuildPipeline.SettingsFingerprint(profile);
        }

        internal static HybridCLRBuildInputs Capture(HybridCLRBuildProfile profile) => new(profile);

        internal HybridCLRBuildProfile Reload()
        {
            // Native builds can unload SOs; a retained managed wrapper is not an input authority.
            var project = _project.Reload<ResourceProject>();
            var recipe = _recipe.Reload<ResourceBuildRecipe>();
            var profile = _profile.Reload<HybridCLRBuildProfile>();
            if (profile.recipe != recipe || recipe.project != project || HybridCLRBuildPipeline.SettingsFingerprint(profile) != _settings)
            {
                throw new InvalidDataException("The saved HybridCLR execution inputs or native settings changed during the build.");
            }
            return profile;
        }

        private sealed class AssetInput
        {
            private readonly string _guid;
            private readonly long _localId;
            private readonly string _fileHash;
            private readonly string _metaHash;
            private readonly string _definition;

            internal AssetInput(UnityEngine.Object asset)
            {
                if (!asset || !EditorUtility.IsPersistent(asset) || EditorUtility.IsDirty(asset) ||
                    !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _guid, out _localId))
                {
                    throw new InvalidDataException("Clean persisted HybridCLR inputs with stable GUID and local file ID are required.");
                }
                var path = AssetDatabase.GetAssetPath(asset);
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
                if (!asset || !EditorUtility.IsPersistent(asset) || EditorUtility.IsDirty(asset) || Definition(asset) != _definition)
                {
                    throw new InvalidDataException("A frozen HybridCLR input changed identity, type or in-memory definition.");
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

            private static string Definition(UnityEngine.Object asset)
            {
                var serialized = JObject.Parse(EditorJsonUtility.ToJson(asset));
                var definition = serialized["MonoBehaviour"] as JObject ?? serialized;
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
                var complete = new JObject { ["definition"] = serialized, ["references"] = references };
                return HybridCLRManifest.Hash(Encoding.UTF8.GetBytes(complete.ToString(Formatting.None)));
            }
        }
    }
}
