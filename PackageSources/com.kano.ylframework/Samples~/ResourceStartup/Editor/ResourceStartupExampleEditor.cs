using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using YLFramework.Editor.Resource;

namespace YLFramework.Samples.Resource.Editor
{
    /// <summary>只做官方作者配置、引用核验和正式内容构建；不在 Editor 初始化或运行资源服务。</summary>
    [CustomEditor(typeof(ResourceStartupExample))]
    public sealed class ResourceStartupExampleEditor : UnityEditor.Editor
    {
        private AddressableAssetSettings _settings;
        private string _message;
        private MessageType _messageType;

        private void OnEnable() => _settings = AddressableAssetSettingsDefaultObject.GetSettings(false);

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var example = (ResourceStartupExample)target;
            EditorGUILayout.LabelField("Runtime Result", example.Result, EditorStyles.wordWrappedLabel);
            EditorGUILayout.HelpBox("This inspector only authors, validates and builds. Open the saved Bootstrap scene, enter Play, and explicitly use the component's Start context command. Await normal shutdown before destroying the owner; Dispose is not a completion signal.", MessageType.Info);
            if (ResourceStartupExample.PendingShutdownCount != 0)
            {
                EditorGUILayout.HelpBox(ResourceStartupExample.PendingShutdownDetails, MessageType.Error);
            }
            _settings = (AddressableAssetSettings)EditorGUILayout.ObjectField("Official Settings", _settings, typeof(AddressableAssetSettings), false);
            if (GUILayout.Button("Open Official Addressables Groups"))
            {
                EditorApplication.ExecuteMenuItem("Window/Asset Management/Addressables/Groups");
            }
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Validate Official References (Read Only)"))
                {
                    Run(() =>
                    {
                        Validate(example, _settings);
                        return "Reference and authoring checks passed. No native initialization or content build ran.";
                    });
                }
                if (GUILayout.Button("Official Full Content Build + Archive State"))
                {
                    var path = EditorUtility.SaveFilePanel("New Player content state archive", "", "addressables_content_state.bin", "bin");
                    if (!string.IsNullOrEmpty(path))
                    {
                        Run(() =>
                        {
                            Validate(example, _settings);
                            return ResourceContentBuildExample.Build(_settings, path).Summary;
                        });
                    }
                }
                if (GUILayout.Button("Official Content Update From Frozen State"))
                {
                    var path = EditorUtility.OpenFilePanel("Select the target Player's archived official content state", "", "bin");
                    if (!string.IsNullOrEmpty(path))
                    {
                        Run(() =>
                        {
                            Validate(example, _settings);
                            return ResourceContentBuildExample.BuildUpdate(_settings, path).Summary;
                        });
                    }
                }
            }
            if (!string.IsNullOrEmpty(_message))
            {
                EditorGUILayout.HelpBox(_message, _messageType);
            }
        }

        /// <summary>只读检查样例真实引用、官方 GUID 登记与类型，不创建或修复配置。</summary>
        public static void Validate(ResourceStartupExample example, AddressableAssetSettings settings)
        {
            if (!example)
            {
                throw new ArgumentNullException(nameof(example));
            }
            example.ValidateReferences();
            var errors = ResourceAddressablesAuthoring.Validate(settings);
            if (errors.Length != 0)
            {
                throw new InvalidOperationException(string.Join("\n", errors));
            }
            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            Check(example.message, typeof(TextAsset));
            Check(example.data, typeof(ResourceStartupExampleData));
            Check(example.prefab, typeof(GameObject));
            if (!string.IsNullOrEmpty(example.contentProjectId))
            {
                CheckNamed(example.message, example.contentProjectId + "/message");
            }
            if (example.preview != null && !string.IsNullOrEmpty(example.preview.AssetGUID))
            {
                Check(example.preview, typeof(Texture2D));
            }
            if (example.audioCue != null && !string.IsNullOrEmpty(example.audioCue.AssetGUID))
            {
                Check(example.audioCue, typeof(AudioClip));
                CheckNamed(example.audioCue, example.contentProjectId + "/cue");
            }
            if (example.scene != null && !string.IsNullOrEmpty(example.scene.AssetGUID))
            {
                Check(example.scene, typeof(SceneAsset));
                if (AssetDatabase.GUIDToAssetPath(example.scene.AssetGUID) == example.gameObject.scene.path)
                {
                    throw new InvalidOperationException("Keep the application owner in a neutral Bootstrap scene, outside the Scope-owned business scene.");
                }
            }

            void Check(AssetReference reference, Type expected)
            {
                var path = AssetDatabase.GUIDToAssetPath(reference.AssetGUID);
                var actual = AssetDatabase.GetMainAssetTypeAtPath(path);
                var matches = entries.Where(entry => entry.guid == reference.AssetGUID).ToArray();
                var bundled = matches.Length == 1 ? matches[0].parentGroup.GetSchema<BundledAssetGroupSchema>() : null;
                if (actual == null || !expected.IsAssignableFrom(actual) || bundled == null ||
                    !bundled.IncludeInBuild || !bundled.IncludeGUIDInCatalog ||
                    entries.Any(entry => entry.guid != reference.AssetGUID && (entry.address == reference.AssetGUID || entry.labels.Contains(reference.AssetGUID))))
                {
                    throw new InvalidOperationException("The official AssetReference must resolve to one included GUID entry of type " + expected.Name + ": " + path);
                }
            }

            void CheckNamed(AssetReference reference, string address)
            {
                var entry = entries.Single(item => item.guid == reference.AssetGUID);
                if (entry.address != address || !entry.parentGroup.GetSchema<BundledAssetGroupSchema>().IncludeAddressInCatalog ||
                    entries.Any(item => item.labels.Contains(address)))
                {
                    throw new InvalidOperationException("The example's named bridge requires an unambiguous official address: " + address);
                }
            }
        }

        [MenuItem("YLFramework/Samples/Create Resource Startup Example")]
        private static void CreateMenu()
        {
            var folder = Create(AddressableAssetSettingsDefaultObject.GetSettings(false));
            Debug.Log("Created " + folder + ". Existing scenes were not saved or closed. Open its Bootstrap scene explicitly before Play or a Player build.");
        }

        /// <summary>显式创建独立样例内容与官方 Group；只保存本次新建场景，不接管现有资产、配置或场景。</summary>
        /// <remarks>要求已保存的官方默认 Settings、分组及场景；不会修改启动策略、Profile 或 Player 场景列表。</remarks>
        public static string Create(AddressableAssetSettings settings)
        {
            ResourceEditorOperations.ValidateBuild(settings);
            var folder = AssetDatabase.GenerateUniqueAssetPath("Assets/YLResourceStartupExample");
            if (Directory.Exists(folder) || File.Exists(folder) || File.Exists(folder + ".meta"))
            {
                throw new IOException("Choose a new sample output; existing identities are never adopted: " + folder);
            }
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            AssetDatabase.CreateFolder(folder, "Content");
            var content = folder + "/Content";
            var messagePath = content + "/Message.txt";
            var previewPath = content + "/Preview.png";
            var cuePath = content + "/Cue.wav";
            using (var output = new FileStream(messagePath, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            {
                writer.Write("Hello from official Addressables");
            }
            AssetDatabase.ImportAsset(messagePath, ImportAssetOptions.ForceSynchronousImport);
            WritePreview(previewPath);
            WriteCue(cuePath);
            AssetDatabase.ImportAsset(previewPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(cuePath, ImportAssetOptions.ForceSynchronousImport);
            var data = CreateInstance<ResourceStartupExampleData>();
            data.text = "Scope-owned borrowed data";
            var dataPath = content + "/Data.asset";
            AssetDatabase.CreateAsset(data, dataPath);
            var previous = SceneManager.GetActiveScene();
            var businessPath = content + "/Business.unity";
            var prefabPath = content + "/ScopedPrefab.prefab";
            var business = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(business);
                var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                marker.name = "Authored Business Scene Marker";
                marker.transform.position = Vector3.left * 2;
                var camera = new GameObject("Business Camera", typeof(Camera));
                camera.transform.position = new Vector3(0, 1, -8);
                var light = new GameObject("Business Light", typeof(Light));
                light.GetComponent<Light>().type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(45, -30, 0);
                var template = GameObject.CreatePrimitive(PrimitiveType.Cube);
                template.name = "Scope Created Prefab";
                try
                {
                    if (!PrefabUtility.SaveAsPrefabAsset(template, prefabPath))
                    {
                        throw new IOException("Unity did not create the sample Prefab.");
                    }
                }
                finally
                {
                    DestroyImmediate(template);
                }
                if (!EditorSceneManager.SaveScene(business, businessPath))
                {
                    throw new IOException("Unity did not save the new business scene.");
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(business, true);
                if (previous.IsValid() && previous.isLoaded)
                {
                    SceneManager.SetActiveScene(previous);
                }
            }
            var id = "startup-example-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var group = settings.CreateGroup(id, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var bundles = group.GetSchema<BundledAssetGroupSchema>();
            bundles.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            bundles.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            bundles.IncludeInBuild = true;
            bundles.IncludeAddressInCatalog = true;
            bundles.IncludeGUIDInCatalog = true;
            group.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            foreach (var item in new[] { (messagePath, "message"), (dataPath, "data"), (prefabPath, "prefab"), (businessPath, "scene"),
                (previewPath, "preview"), (cuePath, "cue") })
            {
                settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(item.Item1), group).SetAddress(id + "/" + item.Item2);
            }
            foreach (var schema in group.Schemas)
            {
                AssetDatabase.SaveAssetIfDirty(schema);
            }
            AssetDatabase.SaveAssetIfDirty(group);
            AssetDatabase.SaveAssetIfDirty(settings);

            var bootstrap = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(bootstrap);
            var owner = new GameObject("Resource Startup Owner");
            var example = owner.AddComponent<ResourceStartupExample>();
            example.message = new AssetReferenceT<TextAsset>(AssetDatabase.AssetPathToGUID(messagePath));
            example.data = new AssetReferenceT<ResourceStartupExampleData>(AssetDatabase.AssetPathToGUID(dataPath));
            example.prefab = new AssetReferenceGameObject(AssetDatabase.AssetPathToGUID(prefabPath));
            example.scene = new AssetReference(AssetDatabase.AssetPathToGUID(businessPath));
            example.preview = new AssetReferenceT<Texture2D>(AssetDatabase.AssetPathToGUID(previewPath));
            example.audioCue = new AssetReferenceT<AudioClip>(AssetDatabase.AssetPathToGUID(cuePath));
            example.contentProjectId = id;
            Validate(example, settings);
            if (!EditorSceneManager.SaveScene(bootstrap, folder + "/Bootstrap.unity"))
            {
                throw new IOException("Unity did not save the new Bootstrap scene.");
            }
            Selection.activeObject = example;
            return folder;
        }

        private static void WritePreview(string path)
        {
            const int Size = 16;
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[Size * Size];
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        var light = (x / 4 + y / 4) % 2 == 0;
                        pixels[y * Size + x] = light ? new Color32(152, 224, 194, 255) : new Color32(24, 68, 63, 255);
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply();
                var png = texture.EncodeToPNG();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                output.Write(png, 0, png.Length);
            }
            finally { DestroyImmediate(texture); }
        }

        private static void WriteCue(string path)
        {
            const int SampleRate = 16000;
            const int SampleCount = SampleRate / 5;
            using var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + SampleCount * sizeof(short));
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM mono.
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(SampleCount * sizeof(short));
            for (var index = 0; index < SampleCount; index++)
            {
                var fade = Math.Min(1.0, (double)Math.Min(index, SampleCount - index - 1) / (SampleRate / 100));
                writer.Write((short)(Math.Sin(2 * Math.PI * 440 * index / SampleRate) * fade * (short.MaxValue / 10)));
            }
        }

        private void Run(Func<string> action)
        {
            try
            {
                _message = action();
                _messageType = MessageType.Info;
            }
            catch (Exception error) when (!(error is ExitGUIException))
            {
                _message = error.ToString();
                _messageType = MessageType.Error;
            }
        }
    }
}
