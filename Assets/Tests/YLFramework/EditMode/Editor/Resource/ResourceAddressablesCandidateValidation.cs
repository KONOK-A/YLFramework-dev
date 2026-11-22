#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using YLFramework.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Testing
{
    /// <summary>Projected only into a separate owned Editor host; never an application bootstrap or Apply.</summary>
    [InitializeOnLoad]
    public static class ResourceAddressablesCandidateValidation
    {
        private const string Key = "YL.CandidateValidation.";
        private static bool _running;
        private static readonly List<string> Errors = new();

        static ResourceAddressablesCandidateValidation()
        {
            EditorApplication.playModeStateChanged += PlayChanged;
            if (SessionState.GetString(Key + "request", "").Length != 0)
                EditorApplication.delayCall += Resume;
        }

        public static void Execute()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var index = Array.IndexOf(args, "-ylCandidateValidationRequest");
                Require(index >= 0 && index + 1 < args.Length, "An explicit owned validation request is required.");
                Require(!EditorApplication.isPlayingOrWillChangePlaymode, "The validator never takes over an existing Play session.");
                SessionState.SetString(Key + "request", Path.GetFullPath(args[index + 1]));
                Require((string)ReadRequest()["mode"] != "OriginalSettings", "Use the dedicated read-only original Settings entry.");
                SessionState.SetString(Key + "phase", "preparing");
                // executeMethod itself runs inside project startup. Leave that stack before Build/Play.
                EditorApplication.delayCall += PrepareAndEnter;
            }
            catch (Exception error) { FailBeforePlay(error); }
        }

        public static void CaptureOriginalSettings()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var index = Array.IndexOf(args, "-ylCandidateValidationRequest");
                Require(index >= 0 && index + 1 < args.Length && !EditorApplication.isPlayingOrWillChangePlaymode, "Original Settings capture requires an explicit idle owned host.");
                SessionState.SetString(Key + "request", Path.GetFullPath(args[index + 1]));
                var request = ReadRequest();
                Require((string)request["mode"] == "OriginalSettings", "This entry captures original Settings only.");
                VerifyHostInputs();
                var preview = Read(Path.Combine((string)request["result"], "preview.json"));
                Require((bool?)preview["settingsExpectedAbsent"] == false, "Existing Settings were not explicitly selected.");
                var settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>((string)preview["settingsPath"]);
                Require(settings, "The original selected Settings is missing; no synthesized baseline is allowed.");
                var footprint = SettingsFootprint(settings);
                // Every original persistent dependency must be part of the pinned clean input closure.
                var pins = ((JArray)Read(Local(".yl-inputs.json"))["files"]).OfType<JObject>().ToDictionary(row => (string)row["path"], StringComparer.Ordinal);
                foreach (var file in ((JArray)footprint["files"]).OfType<JObject>())
                    Require(pins.TryGetValue((string)file["path"], out var pin) && string.Equals((string)pin["sha256"], (string)file["sha256"], StringComparison.OrdinalIgnoreCase), "Expand the frozen original Settings/registration/dependency closure: " + file["path"]);
                VerifyHostInputs();
                var proof = Proof(request);
                proof["purpose"] = "YLResource.OriginalSettingsFootprint";
                proof["status"] = "Captured";
                proof["requestSha256"] = Hash(SessionState.GetString(Key + "request", ""));
                proof["footprint"] = footprint;
                WriteOriginalProof(request, proof);
                SessionState.EraseString(Key + "request");
                EditorApplication.Exit(0);
            }
            catch (Exception error) { FailBeforePlay(error); }
        }

        private static void PrepareAndEnter()
        {
            try
            {
                if (SessionState.GetString(Key + "phase", "") != "preparing") return;
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    EditorApplication.update -= PrepareAndEnter;
                    EditorApplication.update += PrepareAndEnter;
                    return;
                }
                EditorApplication.update -= PrepareAndEnter;
                Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Preparation requires an idle owned Edit session.");
                var request = ReadRequest();
                Debug.Log("YL_CANDIDATE_PHASE Preparing in idle Editor");
                VerifyHostInputs();
                var preview = Read(Path.Combine((string)request["result"], "preview.json"));
                var proof = Proof(request);
                proof["retired"] = VerifyRetirement(request, preview);
                proof["consumers"] = ColdReadConsumers(preview);
                ((JArray)proof["checks"]).Add("ConsumerColdRead");
                proof["foreignSettings"] = ForeignSettings(request, preview);

                for (var i = 0; i < SceneManager.sceneCount; i++)
                    Require(!SceneManager.GetSceneAt(i).isDirty, "Dirty scenes prevent owned validation.");
                var settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>((string)preview["settingsPath"]);
                Require(settings && ((bool?)preview["useAsDefault"] != true || settings == AddressableAssetSettingsDefaultObject.GetSettings(false)), "The candidate's explicitly requested Settings/default registration differs.");
                Require(settings.DisableCatalogUpdateOnStartup, "The actual candidate Settings must disable automatic startup checks.");
                Require(settings.ActivePlayerDataBuilder is BuildScriptPackedMode, "An official Packed builder is required for the selected Settings.");
                var build = settings.ActivePlayerDataBuilder.BuildData<AddressablesPlayerBuildResult>(new AddressablesDataBuilderInput(settings));
                Require(build != null && string.IsNullOrEmpty(build.Error), "Official BuildPlayerContent failed: " + build?.Error);
                proof["assetContent"] = CaptureAssetContent(preview);
                proof["atlasBindings"] = CaptureAtlasBindings(request, preview);
                var data = Path.GetFullPath(Addressables.ResolveInternalId(Addressables.RuntimePath + "/settings.json"));
                Require(File.Exists(data), "The official build did not produce its actual runtime settings.json.");
                proof["runtimeDataPath"] = data;
                proof["runtimeDataSha256"] = Hash(data);
                proof["contentFiles"] = new JArray(Directory.EnumerateFiles(Path.GetDirectoryName(data), "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal).Select(path => new JObject
                    {
                        ["path"] = Path.GetRelativePath(Path.GetDirectoryName(data), path).Replace('\\', '/'), ["sha256"] = Hash(path)
                    }));
                Require(((JArray)proof["contentFiles"]).Any(item => ((string)item["path"]).EndsWith(".bundle", StringComparison.Ordinal)), "No actual packed Bundle output exists.");
                var boot = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Require(EditorSceneManager.SaveScene(boot, "Assets/YLCandidateValidation/Bootstrap.unity"), "The owned neutral scene could not be saved.");
                SessionState.SetBool(Key + "hadPath", PlayerPrefs.HasKey(Addressables.kAddressablesRuntimeDataPath));
                SessionState.SetString(Key + "oldPath", PlayerPrefs.GetString(Addressables.kAddressablesRuntimeDataPath));
                Require(!Addressables.ResourceLocators.Any() && ResourceSystem.Current == null, "The validation host was initialized before its first Packed Play session.");
                SessionState.SetBool(Key + "ownsPath", true);
                PlayerPrefs.SetString(Addressables.kAddressablesRuntimeDataPath, data);
                // The official ExitingEditMode hook may select Fast Mode. Bind our built data after that hook,
                // before any EnteredPlayMode subscriber (e.g. the Localization menu) can initialize Addressables.
                BuildScript.buildCompleted += BindPackedPlayData;
                Save(proof);
                SessionState.SetString(Key + "phase", "entering");
                Debug.Log("YL_CANDIDATE_PHASE Entering real Play");
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { FailBeforePlay(error); }
        }

        private static void BindPackedPlayData(AddressableAssetBuildResult result)
        {
            if (result is not AddressablesPlayModeBuildResult || SessionState.GetString(Key + "phase", "") != "entering") return;
            BuildScript.buildCompleted -= BindPackedPlayData;
            PlayerPrefs.SetString(Addressables.kAddressablesRuntimeDataPath, (string)ReadSaved()["runtimeDataPath"]);
        }

        private static void RestoreRuntimeDataPath()
        {
            BuildScript.buildCompleted -= BindPackedPlayData;
            if (!SessionState.GetBool(Key + "ownsPath", false)) return;
            if (SessionState.GetBool(Key + "hadPath", false)) PlayerPrefs.SetString(Addressables.kAddressablesRuntimeDataPath, SessionState.GetString(Key + "oldPath", ""));
            else PlayerPrefs.DeleteKey(Addressables.kAddressablesRuntimeDataPath);
            SessionState.EraseBool(Key + "ownsPath");
        }

        private static void Resume()
        {
            var phase = SessionState.GetString(Key + "phase", "");
            if (phase == "preparing") PrepareAndEnter();
            else if (phase == "entering") BeginPlay();
            else if (phase == "exiting" && !EditorApplication.isPlayingOrWillChangePlaymode) FinishEdit();
        }

        private static void PlayChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetString(Key + "phase", "") == "entering")
                EditorApplication.delayCall += BeginPlay;
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetString(Key + "phase", "") == "exiting")
                EditorApplication.delayCall += FinishEdit;
        }

        private static async void BeginPlay()
        {
            if (_running || SessionState.GetString(Key + "phase", "") != "entering") return;
            if (!Application.isPlaying || !EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) SessionState.EraseFloat(Key + "playWait");
                else
                {
                    var began = SessionState.GetFloat(Key + "playWait", -1);
                    if (began < 0)
                    {
                        SessionState.SetFloat(Key + "playWait", (float)EditorApplication.timeSinceStartup);
                        Debug.Log("YL_CANDIDATE_PHASE Awaiting real Play flags: application=" + Application.isPlaying + ", editor=" + EditorApplication.isPlaying);
                    }
                    else if (EditorApplication.timeSinceStartup - began > 60)
                    {
                        EditorApplication.update -= BeginPlay;
                        FailBeforePlay(new TimeoutException("Owned Editor stayed idle for 60 seconds without real Play admission; application=" + Application.isPlaying + ", editor=" + EditorApplication.isPlaying));
                        return;
                    }
                }
                EditorApplication.QueuePlayerLoopUpdate();
                EditorApplication.update -= BeginPlay;
                EditorApplication.update += BeginPlay;
                return;
            }
            EditorApplication.update -= BeginPlay;
            SessionState.EraseFloat(Key + "playWait");
            _running = true;
            SessionState.SetString(Key + "phase", "running");
            Debug.Log("YL_CANDIDATE_PHASE Running real Play");
            JObject proof = null;
            ResourceStartup startup = null;
            ResourceSystem system = null;
            try
            {
                var request = ReadRequest();
                proof = ReadSaved();
                Require(Application.isPlaying, "Runtime validation must execute in a real Play session.");
                proof["playModeObserved"] = true;
                proof["playFrame"] = Time.frameCount;
                Require(ResourceSystem.Current == null && Addressables.InternalIdTransformFunc == null && Addressables.WebRequestOverride == null,
                    "Unexpected bootstrap or load override prevents official Packed validation.");
                proof["locatorsBeforeYl"] = new JArray(Addressables.ResourceLocators.Select(locator => locator.LocatorId));
                var data = (string)proof["runtimeDataPath"];
                Require(PlayerPrefs.GetString(Addressables.kAddressablesRuntimeDataPath) == data && Hash(data) == (string)proof["runtimeDataSha256"],
                    "Built runtime data was not selected before Play initialization or changed across Play entry.");
                Application.logMessageReceived += CaptureError;
                startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
                using (var started = startup.StartAsync()) await started;
                system = startup.Resources;
                // External Localization may have initialized this fresh domain first. Accept only the actual loaded
                // official Catalog from this build, never a Fast Mode or unknown previously registered locator.
                proof["packedCatalog"] = VerifyPackedLocator(proof);
                var preview = Read(Path.Combine((string)request["result"], "preview.json"));
                foreach (var entry in ((JArray)preview["entries"]).OfType<JObject>())
                {
                    string address = (string)entry["address"];
                    string path = (string)entry["path"];
                    var loaded = new JObject
                    {
                        ["guid"] = entry["guid"].DeepClone(), ["address"] = address,
                        ["expectedType"] = entry["type"].DeepClone(), ["passed"] = false
                    };
                    if (path.EndsWith(".unity", StringComparison.Ordinal))
                    {
                        Require(!path.EndsWith("InitTestScene.unity", StringComparison.Ordinal) && !path.Contains("/InitTestScene"), "UTF's initialization scene is not candidate content.");
                        using var loading = system.LoadSceneAsync(address, LoadSceneMode.Additive);
                        using var lease = await loading;
                        var scene = lease.Scene;
                        Require(scene.IsValid() && scene.isLoaded && scene.path == path, "The actual additive scene/path did not match the selected Entry: " + address);
                        loaded["actualType"] = scene.GetType().AssemblyQualifiedName;
                        loaded["scenePath"] = scene.path;
                        using var unloading = lease.UnloadAsync();
                        Require(await unloading, "Native additive scene unload did not complete.");
                        Require(!scene.isLoaded && !lease.IsValid, "The candidate scene remained loaded after real unload: " + address);
                        loaded["unloaded"] = true;
                    }
                    else
                    {
                        var expectedType = Type.GetType((string)entry["type"], true);
                        Require(typeof(Object).IsAssignableFrom(expectedType), "The selected Entry has no Unity asset type.");
                        using var scope = system.CreateScope("Candidate validation " + (string)entry["guid"]);
                        var asset = await LoadExpectedAsset(scope, expectedType, address);
                        Require(asset && expectedType.IsInstanceOfType(asset), "The packed asset's real type differs from Preview: " + address);
                        loaded["actualType"] = asset.GetType().AssemblyQualifiedName;
                        loaded["name"] = asset.name;
                        var baseline = ((JArray)proof["assetContent"]).OfType<JObject>().Single(item => (string)item["guid"] == (string)entry["guid"]);
                        Require(JToken.DeepEquals(baseline["content"], AssetContent(asset)), "The real packed asset content differs from its frozen imported source: " + address);
                        loaded["contentVerified"] = true;
                        if (asset is SpriteAtlas atlas)
                        {
                            var binding = ((JArray)proof["atlasBindings"]).OfType<JObject>().Single(item => (string)item["guid"] == (string)entry["guid"]);
                            ((JArray)proof["atlases"]).Add(await VerifyAtlas(atlas, binding));
                        }
                        using var close = scope.CloseAsync();
                        Require(await close, "The Entry scope did not close.");
                    }
                    loaded["passed"] = true;
                    ((JArray)proof["loaded"]).Add(loaded);
                    Save(proof);
                }
                ((JArray)proof["checks"]).Add("OfficialContentLoad");
            }
            catch (Exception error)
            {
                proof ??= Proof(ReadRequest());
                proof["error"] = error.ToString();
                Debug.LogException(error);
            }
            finally
            {
                try
                {
                    if (startup != null)
                    {
                        using var stop = startup.StopAsync();
                        Require(await stop, "The owned resource root did not stop.");
                    }
                    var ledger = system?.GetDiagnostics();
                    proof ??= Proof(ReadRequest());
                    if (ledger != null) proof["cleanupLedger"] = new JObject
                    {
                        ["state"] = ledger.State.ToString(), ["leases"] = ledger.LeaseCount, ["scopes"] = ledger.ScopeCount,
                        ["requests"] = ledger.RequestCount, ["work"] = ledger.PendingWorkCount, ["draining"] = ledger.DrainingHandleCount,
                        ["errors"] = JArray.FromObject(ledger.Errors)
                    };
                    proof["cleanupVerified"] = ledger != null && ledger.State == ResourceSystemState.Closed &&
                        ledger.LeaseCount == 0 && ledger.ScopeCount == 0 && ledger.RequestCount == 0 &&
                        ledger.PendingWorkCount == 0 && ledger.DrainingHandleCount == 0 && ledger.Errors.Count == 0;
                    Require((bool)proof["cleanupVerified"], "Runtime responsibilities or cleanup errors remained.");
                }
                catch (Exception error)
                {
                    proof ??= Proof(ReadRequest());
                    proof["cleanupError"] = error.ToString();
                    proof["cleanupVerified"] = false;
                }
                Application.logMessageReceived -= CaptureError;
                proof["runtimeErrors"] = JArray.FromObject(Errors);
                Save(proof);
                SessionState.SetString(Key + "phase", "exiting");
                EditorApplication.ExitPlaymode();
            }
        }

        private static JObject VerifyPackedLocator(JObject proof)
        {
            var locators = Addressables.ResourceLocators.ToArray();
            var catalogs = locators.Select(Addressables.GetLocatorInfo).Where(info => info?.CatalogLocation != null).ToArray();
            Require(Addressables.InternalIdTransformFunc == null && Addressables.WebRequestOverride == null && catalogs.Length == 1 &&
                locators.All(locator => ReferenceEquals(locator, catalogs[0].Locator) ||
                    locator.GetType().Assembly == typeof(Addressables).Assembly && locator.GetType().Name == "DynamicResourceLocator"),
                "The runtime did not register only the official Packed catalog and its auxiliary locator.");
            var catalog = catalogs[0].CatalogLocation;
            Require(Addressables.ResourceManager.ResourceProviders.Count(provider => provider.ProviderId == catalog.ProviderId &&
                provider.GetType() == typeof(ContentCatalogProvider)) == 1, "An official Catalog provider is required.");
            var internalPath = Addressables.ResolveInternalId(catalog.InternalId);
            if (internalPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) internalPath = new Uri(internalPath).LocalPath;
            var path = Path.GetFullPath(internalPath);
            var relative = Path.GetRelativePath(Path.GetDirectoryName((string)proof["runtimeDataPath"]), path).Replace('\\', '/');
            var expected = ((JArray)proof["contentFiles"]).OfType<JObject>().SingleOrDefault(file => (string)file["path"] == relative);
            Require(expected != null && File.Exists(path) && Hash(path) == (string)expected["sha256"], "The registered official Catalog is not from this Packed build: " + relative);
            return new JObject { ["locator"] = catalogs[0].Locator.LocatorId, ["provider"] = catalog.ProviderId, ["path"] = relative, ["sha256"] = expected["sha256"].DeepClone() };
        }

        private static void CaptureError(string text, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Errors.Add(text + "\n" + stack);
        }

        private static void FinishEdit()
        {
            if (SessionState.GetString(Key + "phase", "") != "exiting") return;
            SessionState.SetString(Key + "phase", "finished");
            var exit = 1;
            JObject proof = null;
            try
            {
                var request = ReadRequest();
                proof = ReadSaved();
                Require(!Application.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode, "The validation did not return to Edit mode.");
                proof["returnedToEditMode"] = true;
                RestoreRuntimeDataPath();
                VerifyHostInputs();
                var preview = Read(Path.Combine((string)request["result"], "preview.json"));
                proof["foreignSettings"] = ForeignSettings(request, preview);
                proof["retired"] = VerifyRetirement(request, preview);
                proof["projects"] = new JArray(((JArray)preview["projects"]).OfType<JObject>().Select(project =>
                {
                    var prefix = (string)project["projectId"] + "/";
                    var expected = ((JArray)preview["entries"]).Count(entry => ((string)entry["address"]).StartsWith(prefix, StringComparison.Ordinal));
                    var actual = ((JArray)proof["loaded"]).Count(entry => (bool?)entry["passed"] == true && ((string)entry["address"]).StartsWith(prefix, StringComparison.Ordinal));
                    Require(expected > 0 && actual == expected, "A selected project has missing runtime Entries.");
                    return new JObject { ["guid"] = project["guid"].DeepClone(), ["projectId"] = project["projectId"].DeepClone(), ["candidateResolution"] = project["candidateResolution"].DeepClone(), ["entries"] = actual, ["passed"] = true };
                }));
                ((JArray)proof["checks"]).Add("ForeignSettingsPreserved");
                Require(proof["error"] == null && proof["cleanupError"] == null && (bool?)proof["cleanupVerified"] == true &&
                    ((JArray)proof["runtimeErrors"]).Count == 0 && ((JArray)proof["loaded"]).Count == ((JArray)preview["entries"]).Count &&
                    ((JArray)proof["consumers"]).Count == ((JArray)preview["consumers"]).Count, "The candidate's selected checks did not all complete successfully.");
                proof["status"] = "Passed";
                proof["runtimeVerified"] = true;
                exit = 0;
            }
            catch (Exception error)
            {
                proof ??= Proof(ReadRequest());
                if (proof["error"] == null) proof["error"] = error.ToString();
                else proof["finalizationError"] = error.ToString();
                proof["status"] = "Failed";
                Debug.LogException(error);
            }
            finally
            {
                WriteProof(proof);
                SessionState.EraseString(Key + "request");
                SessionState.EraseString(Key + "phase");
                EditorApplication.Exit(exit);
            }
        }

        private static JArray ColdReadConsumers(JObject preview)
        {
            var proof = new JArray();
            foreach (var file in ((JArray)preview["consumers"]).OfType<JObject>().GroupBy(item => (string)item["assetPath"]))
            {
                Scene scene = default;
                Object[] objects;
                if (file.Key.EndsWith(".unity", StringComparison.Ordinal))
                {
                    scene = EditorSceneManager.OpenScene(file.Key, OpenSceneMode.Additive);
                    objects = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Component>(true)).Cast<Object>().ToArray();
                }
                else if (file.Key.EndsWith(".prefab", StringComparison.Ordinal))
                    objects = AssetDatabase.LoadAssetAtPath<GameObject>(file.Key).GetComponentsInChildren<Component>(true).Cast<Object>().ToArray();
                else objects = AssetDatabase.LoadAllAssetsAtPath(file.Key);
                try
                {
                    foreach (var consumer in file)
                    {
                        var matches = objects.Where(value => Identity(value, consumer)).ToArray();
                        Require(matches.Length == 1 && matches[0].GetType().AssemblyQualifiedName == (string)consumer["type"], "The cold consumer identity/type changed: " + file.Key);
                        using var serialized = new SerializedObject(matches[0]);
                        var count = 0;
                        foreach (var field in (JObject)consumer["properties"])
                        {
                            // Exported process-local PPtr cache IDs are not persisted object identities.
                            if (field.Key.EndsWith(".m_FileID", StringComparison.Ordinal) || field.Key.EndsWith(".m_PathID", StringComparison.Ordinal)) continue;
                            using var property = serialized.FindProperty(field.Key);
                            Require(property != null && property.propertyType.ToString() == (string)field.Value["propertyType"] && property.type == (string)field.Value["type"], "A cold SerializedProperty disappeared: " + field.Key);
                            var rewrite = ((JArray)preview["rewrites"]).OfType<JObject>().SingleOrDefault(item => (string)item["guid"] == (string)consumer["guid"] && (long)item["localId"] == (long)consumer["localId"] && (string)item["propertyPath"] == field.Key);
                            VerifyProperty(property, field.Value, rewrite == null ? field.Value["value"] : rewrite["newValue"]);
                            count++;
                        }
                        Require(count > 0, "The consumer has no cold-read property evidence.");
                        proof.Add(new JObject { ["guid"] = consumer["guid"].DeepClone(), ["localId"] = consumer["localId"].DeepClone(), ["path"] = file.Key, ["propertiesChecked"] = count, ["passed"] = true });
                    }
                }
                finally { if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true); }
            }
            return proof;
        }

        private static void VerifyProperty(SerializedProperty property, JToken exported, JToken expected)
        {
            bool matches;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Generic:
                    matches = (bool?)exported["isArray"] != true || property.isArray && property.arraySize == (int)exported["arraySize"];
                    break;
                case SerializedPropertyType.String: matches = property.stringValue == (string)expected; break;
                case SerializedPropertyType.Integer: matches = property.longValue == (long)expected; break;
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.LayerMask: matches = property.intValue == (int)expected; break;
                case SerializedPropertyType.Boolean: matches = property.boolValue == (bool)expected; break;
                case SerializedPropertyType.Float: matches = property.type == "float" ? property.floatValue.Equals((float)expected) : property.doubleValue.Equals((double)expected); break;
                case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.ExposedReference:
                    var reference = property.propertyType == SerializedPropertyType.ObjectReference ? property.objectReferenceValue : property.exposedReferenceValue;
                    matches = (bool?)expected["isNull"] == true ? !reference : Identity(reference, (JObject)expected);
                    break;
                default: matches = property.hasVisibleChildren && property.propertyType != SerializedPropertyType.ManagedReference; break;
            }
            Require(matches, "A cold SerializedProperty value/GUID differs: " + property.propertyPath);
        }

        private static bool Identity(Object value, JObject expected)
        {
            if (!value) return false;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id))
                return guid == (string)expected["guid"] && id == (long)expected["localId"] &&
                    (expected["globalId"] == null || GlobalObjectId.GetGlobalObjectIdSlow(value).ToString() == (string)expected["globalId"]);
            var global = GlobalObjectId.GetGlobalObjectIdSlow(value);
            return global.assetGUID.ToString() == (string)expected["guid"] && global.targetObjectId == (ulong)expected["localId"] && global.targetPrefabId == ((ulong?)expected["targetPrefabId"] ?? 0);
        }

        private static JArray CaptureAssetContent(JObject preview)
        {
            var result = new JArray();
            foreach (var entry in ((JArray)preview["entries"]).OfType<JObject>().Where(item => !((string)item["path"]).EndsWith(".unity", StringComparison.Ordinal)))
            {
                var asset = AssetDatabase.LoadAssetAtPath((string)entry["path"], Type.GetType((string)entry["type"], true));
                Require(asset, "A selected frozen asset is missing after retirement: " + entry["path"]);
                result.Add(new JObject { ["guid"] = entry["guid"].DeepClone(), ["content"] = AssetContent(asset) });
            }
            return result;
        }

        private static JObject AssetContent(Object asset)
        {
            var content = new JObject { ["name"] = asset.name, ["type"] = asset.GetType().AssemblyQualifiedName };
            if (asset is TextAsset text) content["bytesSha256"] = HashBytes(text.bytes);
            if (asset is Texture texture) { content["width"] = texture.width; content["height"] = texture.height; }
            if (asset is Sprite sprite) { content["width"] = sprite.rect.width; content["height"] = sprite.rect.height; }
            if (asset is GameObject prefab)
                content["hierarchy"] = new JArray(prefab.GetComponentsInChildren<Transform>(true).Select(transform =>
                    new JObject { ["name"] = transform.name, ["active"] = transform.gameObject.activeSelf, ["components"] = new JArray(transform.GetComponents<Component>().Select(component => component ? component.GetType().AssemblyQualifiedName : "MissingScript")) }));
            if (asset is SpriteAtlas atlas) content["spriteCount"] = atlas.spriteCount;
            return content;
        }

        private static Task<Object> LoadExpectedAsset(ResourceScope scope, Type expectedType, string address)
        {
            // The JSON declares the real Unity type. Only select this tool's generic adapter;
            // the actual acquisition remains the normal public typed Scope API.
            return (Task<Object>)typeof(ResourceAddressablesCandidateValidation)
                .GetMethod(nameof(LoadExpectedAssetTyped), BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(expectedType).Invoke(null, new object[] { scope, address });
        }

        private static async Task<Object> LoadExpectedAssetTyped<T>(ResourceScope scope, string address) where T : Object
        {
            using var loading = scope.LoadAsync<T>(address);
            return await loading;
        }

        private static JArray CaptureAtlasBindings(JObject request, JObject preview)
        {
            var result = new JArray();
            var indexPath = Path.Combine((string)request["result"], "recovery-index.json");
            var recovered = File.Exists(indexPath) ? Read(indexPath).SelectTokens("projects[*].assets[*]").OfType<JObject>().ToArray() : Array.Empty<JObject>();
            if (File.Exists(indexPath)) Require(Hash(indexPath) == (string)request["recoveryIndexSha256"], "The authenticated Atlas index changed.");
            foreach (var entry in ((JArray)preview["entries"]).OfType<JObject>().Where(item => ((string)item["type"]).StartsWith("UnityEngine.U2D.SpriteAtlas,", StringComparison.Ordinal)))
            {
                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>((string)entry["path"]);
                var archived = recovered.SingleOrDefault(item => (string)item["guid"] == (string)entry["guid"]);
                var binding = archived?["oldEditorAtlasBinding"] as JObject;
                JArray sprites;
                string origin;
                if (binding != null)
                {
                    Require((string)binding["canonical"] == (string)entry["address"] && (string)binding["path"] == (string)entry["path"], "An authenticated GetSprite canonical differs from Preview.");
                    sprites = (JArray)binding["sprites"].DeepClone();
                    origin = "AuthenticatedLegacyGetSpriteEvidence";
                }
                else
                {
                    // An original manual Atlas has no derived binding. Read its actual pinned packables,
                    // never strip '(Clone)' or invent a Sprite name/local ID/canonical selector.
                    Require(archived == null || (bool?)archived["isGeneratedAtlas"] == false, "A derived Atlas has no authenticated GetSprite evidence.");
                    sprites = new JArray();
                    var sources = new Dictionary<string, Sprite>(StringComparer.Ordinal);
                    foreach (var packable in atlas.GetPackables())
                    {
                        var path = AssetDatabase.GetAssetPath(packable);
                        var paths = AssetDatabase.IsValidFolder(path) ? AssetDatabase.FindAssets("t:Sprite", new[] { path }).Select(AssetDatabase.GUIDToAssetPath) : new[] { path };
                        foreach (var file in paths)
                            foreach (var source in AssetDatabase.LoadAllAssetsAtPath(file).OfType<Sprite>())
                            {
                                Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long id), "A manual Atlas source has no measured identity.");
                                var key = guid + ":" + id;
                                sources[key] = source;
                            }
                    }
                    foreach (var source in sources.Values)
                    {
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long id);
                        sprites.Add(new JObject { ["name"] = source.name, ["sourceGuid"] = guid, ["sourceLocalId"] = id, ["rectWidth"] = source.rect.width, ["rectHeight"] = source.rect.height });
                    }
                    origin = "MeasuredFrozenPreviewManualAtlasPackables";
                }
                Require(atlas && sprites.Count > 0 && sprites.Count == atlas.spriteCount && sprites.Select(sprite => (string)sprite["name"]).Distinct(StringComparer.Ordinal).Count() == sprites.Count, "The real Atlas's exact Sprite membership is ambiguous or differs.");
                result.Add(new JObject { ["guid"] = entry["guid"].DeepClone(), ["address"] = entry["address"].DeepClone(), ["origin"] = origin, ["sprites"] = sprites });
            }
            return result;
        }

        private static async Task<JObject> VerifyAtlas(SpriteAtlas atlas, JObject binding)
        {
            var clones = new JArray();
            Require(atlas.spriteCount == ((JArray)binding["sprites"]).Count, "The loaded packed Atlas changed Sprite membership.");
            foreach (var expected in ((JArray)binding["sprites"]).OfType<JObject>())
            {
                var clone = atlas.GetSprite((string)expected["name"]);
                Require(clone && !EditorUtility.IsPersistent(clone), "GetSprite did not return an independently owned runtime clone.");
                try
                {
                    Require(clone.texture && clone.rect.width.Equals((float)expected["rectWidth"]) && clone.rect.height.Equals((float)expected["rectHeight"]), "The actual GetSprite content/packed texture differs.");
                }
                finally { if (clone) Object.Destroy(clone); }
                var deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (clone && Time.realtimeSinceStartupAsDouble < deadline) await NextEditorFrame();
                Require(!clone, "The owned Sprite clone was not actually destroyed before returning its Atlas lease.");
                clones.Add(new JObject { ["name"] = expected["name"].DeepClone(), ["sourceGuid"] = expected["sourceGuid"].DeepClone(), ["sourceLocalId"] = expected["sourceLocalId"].DeepClone(), ["destroyed"] = true });
            }
            return new JObject { ["guid"] = binding["guid"].DeepClone(), ["address"] = binding["address"].DeepClone(), ["origin"] = binding["origin"].DeepClone(), ["clones"] = clones, ["passed"] = true };
        }

        private static Task NextEditorFrame()
        {
            var completion = new TaskCompletionSource<bool>();
            void Next() { EditorApplication.update -= Next; EditorApplication.QueuePlayerLoopUpdate(); completion.SetResult(true); }
            EditorApplication.update += Next;
            return completion.Task;
        }

        private static JArray VerifyRetirement(JObject request, JObject preview)
        {
            var manifest = Read(Path.Combine((string)request["result"], "migration.json"));
            var deleted = ((JArray)manifest["operations"]).OfType<JObject>().Where(item => (string)item["action"] == "Delete").ToArray();
            var result = new JArray();
            foreach (var operation in deleted)
            {
                var frozen = ((JArray)preview["retirement"]["entries"]).OfType<JObject>().Single(item => (string)item["path"] == (string)operation["path"] && (string)item["originalSha256"] == (string)operation["inputSha256"] && (string)item["guid"] == (string)operation["retiredGuid"]);
                var path = (string)operation["path"];
                var before = Path.Combine((string)request["result"], "RetiredBefore", path);
                Require(Hash(before) == (string)frozen["originalSha256"] && !File.Exists(Local(path)) && !Directory.Exists(Local(path)) && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath((string)frozen["guid"])), "An exact retired path/GUID reappeared or its preimage changed: " + path);
                result.Add(new JObject { ["path"] = path, ["guid"] = frozen["guid"].DeepClone(), ["kind"] = frozen["kind"].DeepClone(), ["negative"] = frozen["negative"].DeepClone(), ["originalSha256"] = frozen["originalSha256"].DeepClone(), ["absent"] = true, ["passed"] = true });
            }
            Require(result.Count == (int)request["retiredCount"], "The final retired membership differs.");
            return result;
        }

        private static JObject ForeignSettings(JObject request, JObject preview)
        {
            var settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>((string)preview["settingsPath"]);
            Require(settings, "The selected official Settings asset is missing.");
            if ((int?)request["atlasCount"] > 0) Require((int)EditorSettings.spritePackerMode == 5, "The frozen SpriteAtlasV2 EditorSettings was not honored.");
            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            foreach (var item in ((JArray)preview["entries"]).OfType<JObject>())
            {
                var entry = settings.FindAssetEntry((string)item["guid"]);
                Require(entry != null && entry.address == (string)item["address"] && entry.parentGroup.Name == (string)item["group"] && entry.labels.Contains((string)item["label"]), "An allowed candidate Entry/Label/Group differs.");
                var schema = entry.parentGroup.GetSchema<BundledAssetGroupSchema>();
                var packing = (string)item["packing"];
                Require(packing == "Together" || packing == "PerAsset", "Unsupported candidate packing intent.");
                var compression = (string)item["compression"] switch
                {
                    "Lz4" => BundledAssetGroupSchema.BundleCompressionMode.LZ4,
                    "Lzma" => BundledAssetGroupSchema.BundleCompressionMode.LZMA,
                    "Uncompressed" => BundledAssetGroupSchema.BundleCompressionMode.Uncompressed,
                    _ => throw new InvalidOperationException("Unsupported candidate compression intent.")
                };
                Require(schema && schema.BundleMode == (packing == "Together" ? BundledAssetGroupSchema.BundlePackingMode.PackTogether : BundledAssetGroupSchema.BundlePackingMode.PackSeparately) && schema.Compression == compression, "The actual official Group packing/compression differs from Preview.");
                Require(AssetDatabase.AssetPathToGUID((string)item["path"]) == (string)item["guid"], "An output asset GUID changed.");
            }
            foreach (var alias in ((JArray)preview["aliases"]).OfType<JObject>())
            {
                string legacy = (string)alias["legacy"];
                Require(legacy == (string)alias["canonical"] || !settings.GetLabels().Contains(legacy) && !entries.Any(entry => entry.address == legacy), "Legacy alias evidence became a runtime address/Label.");
            }
            if ((bool?)preview["settingsExpectedAbsent"] == false)
            {
                var evidence = request["originalSettingsProof"] as JObject;
                Require(evidence != null, "An ordinary owned original-Settings capture is mandatory.");
                var originalPath = (string)evidence["path"];
                Require(Hash(originalPath) == (string)evidence["sha256"], "The original Settings proof changed.");
                var original = Read(originalPath);
                Require((string)original["purpose"] == "YLResource.OriginalSettingsFootprint" && (string)original["status"] == "Captured" &&
                    (string)original["mode"] == "OriginalSettings" && (string)original["resultId"] == (string)evidence["resultId"] && (string)original["requestSha256"] == (string)evidence["requestSha256"] &&
                    (bool?)original["runtimeVerified"] == false && (string)original["hostId"] == (string)evidence["hostId"] &&
                    (string)original["inputHash"] == (string)evidence["inputHash"] && (string)original["previewSha256"] == (string)request["previewSha256"] &&
                    (string)original["manifestSha256"] == (string)request["manifestSha256"] && (string)original["unityVersion"] == Application.unityVersion &&
                    (string)original["addressablesVersion"] == (string)request["expectedAddressablesVersion"], "Foreign or incomplete original Settings proof.");
                var cold = SessionState.GetString(Key + "settingsCold", "");
                // Compare the cold persistent footprint, not mutable build-time SDK caches. Both
                // ends also verify every target input byte before/after Build/Play.
                if (cold.Length == 0)
                {
                    cold = SettingsFootprint(settings).ToString();
                    SessionState.SetString(Key + "settingsCold", cold);
                }
                var preservation = ResourceAddressablesSettingsProof.Verify((JObject)original["footprint"], JObject.Parse(cold), preview);
                preservation["originalProofSha256"] = evidence["sha256"].DeepClone();
                preservation["originalHostId"] = original["hostId"].DeepClone();
                preservation["originalInputHash"] = original["inputHash"].DeepClone();
                return preservation;
            }
            Require((bool?)preview["settingsExpectedAbsent"] == true, "The Preview must explicitly declare the Settings baseline state.");
            var baseline = Path.Combine((string)request["result"], "Original", "ProjectSettings", "EditorBuildSettings.asset");
            var before = File.ReadAllText(baseline).Replace("\r\n", "\n");
            var after = File.ReadAllText(Local("ProjectSettings/EditorBuildSettings.asset")).Replace("\r\n", "\n");
            const string registration = @"(?m)^    com\.unity\.addressableassets: \{fileID: 11400000, guid: ([0-9a-f]{32}), type: 2\}\n?";
            var added = Regex.Matches(after, registration);
            Require(added.Count == 1 && AssetDatabase.GUIDToAssetPath(added[0].Groups[1].Value) == "Assets/AddressableAssetsData/DefaultObject.asset", "Unexpected official default-object registration.");
            string Normalize(string text) => Regex.Replace(text, registration, "").Replace("  m_configObjects: {}", "  m_configObjects:").TrimEnd();
            Require(Normalize(before) == Normalize(after), "A foreign BuildSettings scene/config field was overwritten.");
            return new JObject
            {
                ["baselineSettingsPresent"] = false, ["baselineForeignEntries"] = 0, ["baselineForeignLabels"] = 0,
                ["currentEntries"] = entries.Count, ["currentLabels"] = JArray.FromObject(settings.GetLabels()),
                ["preservedOriginalBuildSettingsExceptAddressablesRegistration"] = true,
                ["note"] = "This Preview created Settings in an absent namespace; existing-Settings migration is not claimed. All other original compiler/input bytes are separately verified."
            };
        }

        public static JObject SettingsFootprint(AddressableAssetSettings settings)
        {
            Require(settings && !EditorUtility.IsDirty(settings), "A saved persistent original Settings is required.");
            var nodes = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var files = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var folders = new JObject();
            JObject Reference(Object value)
            {
                if (!value) return null;
                Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long localId), "A transient Settings reference cannot prove an original identity.");
                var path = AssetDatabase.GetAssetPath(value).Replace('\\', '/');
                Require(guid.Length == 32 && localId != 0 && path.Length != 0, "Missing canonical Settings reference.");
                return new JObject { ["guid"] = guid, ["localId"] = localId, ["path"] = path, ["type"] = value.GetType().AssemblyQualifiedName };
            }
            void Pin(string path)
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) && path != "ProjectSettings/EditorBuildSettings.asset") return;
                if (Directory.Exists(Local(path)))
                {
                    var members = Directory.EnumerateFiles(Local(path), "*", SearchOption.AllDirectories)
                        .Select(file => Path.GetRelativePath(Path.GetFullPath(Application.dataPath + "/.."), file).Replace('\\', '/')).OrderBy(file => file, StringComparer.Ordinal).ToArray();
                    folders[path] = JArray.FromObject(members);
                    foreach (var member in members) files[member] = new JObject { ["path"] = member, ["sha256"] = Hash(Local(member)) };
                    var metadata = path + ".meta";
                    files[metadata] = new JObject { ["path"] = metadata, ["sha256"] = Hash(Local(metadata)) };
                    return;
                }
                foreach (var input in path.StartsWith("Assets/", StringComparison.Ordinal) ? new[] { path, path + ".meta" } : new[] { path })
                    files[input] = new JObject { ["path"] = input, ["sha256"] = Hash(Local(input)) };
            }
            JObject Fields(Object asset)
            {
                // Unity invokes the SDK's serialization callbacks here: Profile/Group/Entry/Schema
                // sorting follows the installed official implementation, not blanket array sorting.
                EditorJsonUtility.ToJson(asset);
                var managed = new Dictionary<long, string>();
                JToken Value(SerializedProperty property)
                {
                    if (property.isArray && property.propertyType != SerializedPropertyType.String)
                    {
                        var items = new JArray();
                        for (var i = 0; i < property.arraySize; i++)
                        {
                            using var item = property.GetArrayElementAtIndex(i);
                            items.Add(Value(item));
                        }
                        return items;
                    }
                    switch (property.propertyType)
                    {
                        case SerializedPropertyType.ManagedReference:
                        case SerializedPropertyType.Generic:
                            var isManaged = property.propertyType == SerializedPropertyType.ManagedReference;
                            var managedType = isManaged ? property.managedReferenceFullTypename : null;
                            var managedId = isManaged ? property.managedReferenceId : 0;
                            if (isManaged && string.IsNullOrEmpty(managedType))
                            {
                                Require(property.managedReferenceValue == null, "A missing managed-reference type cannot be treated as a null field.");
                                return new JObject { ["managedType"] = JValue.CreateNull(), ["managedId"] = managedId };
                            }
                            if (isManaged && managed.TryGetValue(managedId, out var previousType))
                            {
                                Require(previousType == managedType, "A managed-reference identity changed type.");
                                return new JObject { ["managedRef"] = managedId, ["managedType"] = managedType };
                            }
                            if (isManaged) managed.Add(managedId, managedType);
                            var children = new JObject();
                            using (var child = property.Copy())
                            using (var end = property.GetEndProperty())
                            {
                                var moved = child.Next(true);
                                while (moved && !SerializedProperty.EqualContents(child, end))
                                {
                                    Require(child.depth == property.depth + 1, "Unknown nested serialized field layout.");
                                    children.Add(child.name, Value(child));
                                    moved = child.Next(false);
                                }
                            }
                            return isManaged ? new JObject { ["managedType"] = managedType, ["managedId"] = managedId, ["fields"] = children } : children;
                        case SerializedPropertyType.ObjectReference:
                            Require(property.objectReferenceValue || property.objectReferenceInstanceIDValue == 0, "An unresolved persistent Settings reference remains.");
                            return Reference(property.objectReferenceValue) ?? (JToken)JValue.CreateNull();
                        case SerializedPropertyType.String: return property.stringValue;
                        case SerializedPropertyType.Boolean: return property.boolValue;
                        case SerializedPropertyType.Integer: return property.longValue;
                        case SerializedPropertyType.Enum:
                        case SerializedPropertyType.ArraySize:
                        case SerializedPropertyType.Character:
                        case SerializedPropertyType.LayerMask:
                        case SerializedPropertyType.FixedBufferSize: return property.intValue;
                        case SerializedPropertyType.Float: return property.type == "float" ? new JValue(property.floatValue) : new JValue(property.doubleValue);
                        case SerializedPropertyType.Hash128: return property.hash128Value.ToString();
                        case SerializedPropertyType.Color:
                            var color = property.colorValue; return new JArray(color.r, color.g, color.b, color.a);
                        case SerializedPropertyType.Vector2:
                            var v2 = property.vector2Value; return new JArray(v2.x, v2.y);
                        case SerializedPropertyType.Vector3:
                            var v3 = property.vector3Value; return new JArray(v3.x, v3.y, v3.z);
                        case SerializedPropertyType.Vector4:
                            var v4 = property.vector4Value; return new JArray(v4.x, v4.y, v4.z, v4.w);
                        case SerializedPropertyType.Quaternion:
                            var q = property.quaternionValue; return new JArray(q.x, q.y, q.z, q.w);
                        case SerializedPropertyType.Rect:
                            var rect = property.rectValue; return new JArray(rect.x, rect.y, rect.width, rect.height);
                        case SerializedPropertyType.Bounds:
                            var b = property.boundsValue; return new JArray(b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z);
                        case SerializedPropertyType.Vector2Int:
                            var i2 = property.vector2IntValue; return new JArray(i2.x, i2.y);
                        case SerializedPropertyType.Vector3Int:
                            var i3 = property.vector3IntValue; return new JArray(i3.x, i3.y, i3.z);
                        case SerializedPropertyType.RectInt:
                            var ri = property.rectIntValue; return new JArray(ri.x, ri.y, ri.width, ri.height);
                        case SerializedPropertyType.BoundsInt:
                            var bi = property.boundsIntValue; return new JArray(bi.position.x, bi.position.y, bi.position.z, bi.size.x, bi.size.y, bi.size.z);
                        default: throw new InvalidOperationException("Unsupported original serialized Settings field; no silent omission: " + property.propertyPath + " / " + property.propertyType);
                    }
                }
                var fields = new JObject();
                using var serialized = new SerializedObject(asset);
                using var root = serialized.GetIterator();
                var next = root.Next(true);
                while (next) { Require(root.depth == 0, "Unknown serialized root layout."); fields.Add(root.name, Value(root)); next = root.Next(false); }
                return fields;
            }
            JObject Node(Object asset)
            {
                Require(asset && !EditorUtility.IsDirty(asset), "A dirty/missing original Settings dependency cannot be captured.");
                var identity = Reference(asset);
                var key = (string)identity["guid"] + ":" + (long)identity["localId"];
                if (nodes.TryGetValue(key, out var found)) return found;
                var fields = Fields(asset);
                Pin((string)identity["path"]);
                var node = new JObject { ["identity"] = identity, ["fields"] = fields.DeepClone(), ["sha256"] = ((string)identity["path"]).StartsWith("Assets/", StringComparison.Ordinal) ? Hash(Local((string)identity["path"])) : null };
                nodes.Add(key, node);
                return node;
            }
            var original = Node(settings);
            var groups = new JArray();
            foreach (var group in settings.groups)
            {
                Require(group && group.Schemas.All(schema => schema), "An original Group/Schema dependency is missing.");
                var groupNode = Node(group);
                var schemas = new JArray(group.Schemas.Select(schema => Node(schema).DeepClone()));
                var entries = new JArray();
                foreach (var entry in group.entries.OrderBy(entry => entry.guid, StringComparer.Ordinal))
                {
                    var path = AssetDatabase.GUIDToAssetPath(entry.guid);
                    Require(path.Length != 0, "Expand the original foreign/localization Entry closure: " + entry.guid);
                    Pin(path);
                    var assetIdentity = Reference(AssetDatabase.LoadMainAssetAtPath(path));
                    Require(assetIdentity != null && (string)assetIdentity["guid"] == entry.guid, "An original Entry has no canonical imported main-asset GUID/localId/path.");
                    var stored = ((JArray)groupNode["fields"]["m_SerializeEntries"]).OfType<JObject>().Single(item => (string)item["m_GUID"] == entry.guid);
                    entries.Add(new JObject { ["guid"] = entry.guid, ["path"] = path, ["identity"] = assetIdentity, ["address"] = entry.address, ["labels"] = new JArray(entry.labels.OrderBy(label => label, StringComparer.Ordinal)), ["readOnly"] = entry.ReadOnly, ["flagged"] = entry.FlaggedDuringContentUpdateRestriction, ["fields"] = stored.DeepClone() });
                }
                groups.Add(new JObject { ["node"] = groupNode.DeepClone(), ["name"] = group.Name, ["schemas"] = schemas, ["entries"] = entries });
            }
            foreach (var dependency in AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(settings), true))
            {
                if (!dependency.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                Pin(dependency);
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(dependency).Where(asset => asset is ScriptableObject)) Node(asset);
            }
            var configurations = new JObject();
            foreach (var name in EditorBuildSettings.GetConfigObjectNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                Require(EditorBuildSettings.TryGetConfigObject<Object>(name, out var config) && config, "Expand the original foreign default configuration closure: " + name);
                configurations[name] = Reference(config);
                Node(config);
                foreach (var dependency in AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(config), true).Where(path => path.StartsWith("Assets/", StringComparison.Ordinal)))
                {
                    Pin(dependency);
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(dependency).Where(asset => asset is ScriptableObject)) Node(asset);
                }
            }
            var selectedDefault = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Require(configurations["com.unity.addressableassets"] == null || selectedDefault, "The original default Settings registration is unresolved; expand/fix the frozen input instead of adopting it.");
            if (selectedDefault)
            {
                Node(selectedDefault);
                foreach (var dependency in AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(selectedDefault), true).Where(path => path.StartsWith("Assets/", StringComparison.Ordinal)))
                {
                    Pin(dependency);
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(dependency).Where(asset => asset is ScriptableObject)) Node(asset);
                }
                var foreignEntries = new List<AddressableAssetEntry>();
                selectedDefault.GetAllAssets(foreignEntries, false);
                foreach (var entry in foreignEntries) { var path = AssetDatabase.GUIDToAssetPath(entry.guid); Require(path.Length > 0, "Missing external default Entry dependency."); Pin(path); }
            }
            var registration = new JObject
            {
                ["configs"] = configurations, ["defaultSettings"] = Reference(selectedDefault) ?? (JToken)JValue.CreateNull(),
                ["scenes"] = new JArray(EditorBuildSettings.scenes.Select(scene => new JObject { ["path"] = scene.path, ["guid"] = scene.guid.ToString(), ["enabled"] = scene.enabled }))
            };
            var buildSettings = Resources.FindObjectsOfTypeAll<EditorBuildSettings>();
            Require(buildSettings.Length == 1, "The official BuildSettings serialized singleton cannot be identified uniquely.");
            registration["fields"] = Fields(buildSettings[0]);
            Pin("ProjectSettings/EditorBuildSettings.asset");
            return new JObject { ["schemaVersion"] = 1, ["settings"] = original.DeepClone(), ["layout"] = new JObject { ["root"] = settings.ConfigFolder.Replace('\\', '/'), ["groups"] = settings.GroupFolder.Replace('\\', '/'), ["schemas"] = settings.GroupSchemaFolder.Replace('\\', '/') }, ["labels"] = JArray.FromObject(settings.GetLabels()), ["groups"] = groups, ["nodes"] = new JArray(nodes.Values.Select(node => node.DeepClone())), ["registration"] = registration, ["files"] = new JArray(files.Values.Select(file => file.DeepClone())), ["folders"] = folders };
        }

        private static void WriteOriginalProof(JObject request, JObject proof)
        {
            using var stream = new FileStream(Path.Combine((string)request["result"], "original-settings.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(proof.ToString());
        }

        private static void VerifyHostInputs()
        {
            var inputs = Read(Local(".yl-inputs.json"));
            foreach (var item in ((JArray)inputs["files"]).OfType<JObject>())
                Require(string.Equals(Hash(Local((string)item["path"])), (string)item["sha256"], StringComparison.OrdinalIgnoreCase), "An original/candidate/compiler input changed during validation: " + item["path"]);
        }

        private static JObject ReadRequest()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var owner = Read(Path.Combine(root, ".yl-owner.json"));
            string workspace = Directory.GetParent(root)?.Parent?.FullName;
            var workspaceOwner = Read(Path.Combine(workspace ?? "", ".yl-workspace.json"));
            var request = Read(SessionState.GetString(Key + "request", ""));
            var inputs = Read(Path.Combine(root, ".yl-inputs.json"));
            var output = Read(Path.Combine((string)request["result"], ".yl-owner.json"));
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Addressables).Assembly);
            Require(Application.isBatchMode && (string)owner["purpose"] == "YLTesting.Directory" && (string)owner["kind"] == "Host" && (string)owner["path"] == root &&
                (string)workspaceOwner["purpose"] == "YLTesting.Workspace" && (string)workspaceOwner["id"] == (string)owner["workspaceId"] &&
                (string)request["purpose"] == "YLResource.CandidateValidationRequest" && (int?)request["schemaVersion"] == 1 &&
                ((string)request["mode"] == "CandidateOverlay" || (string)request["mode"] == "AppliedSource" || (string)request["mode"] == "OriginalSettings") &&
                (string)request["hostId"] == (string)owner["id"] && (string)request["inputHash"] == (string)inputs["inputHash"] &&
                (string)inputs["hostId"] == (string)owner["id"] && (string)output["kind"] == "Result" && (string)output["id"] == (string)request["resultId"] &&
                (string)output["workspaceId"] == (string)owner["workspaceId"] &&
                ((string)request["result"]).StartsWith(Path.Combine(workspace, "Results") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                (string)request["expectedUnityVersion"] == Application.unityVersion && package != null && package.source == UnityEditor.PackageManager.PackageSource.Registry &&
                (string)request["expectedAddressablesVersion"] == package.version, "The owned request, compiler input or actual Unity/AA profile is foreign.");
            Require(Hash(Path.Combine((string)request["result"], "preview.json")) == (string)request["previewSha256"] &&
                Hash(Path.Combine((string)request["result"], "migration.json")) == (string)request["manifestSha256"], "The frozen Preview/Manifest changed.");
            return request;
        }

        private static JObject Proof(JObject request) => new JObject
        {
            ["schemaVersion"] = 1, ["purpose"] = "YLResource.CandidateNativeProof", ["status"] = "Failed", ["runtimeVerified"] = false,
            ["hostId"] = request["hostId"].DeepClone(), ["resultId"] = request["resultId"].DeepClone(), ["inputHash"] = request["inputHash"].DeepClone(),
            ["subjectHostId"] = request["subjectHostId"].DeepClone(), ["subjectInputHash"] = request["subjectInputHash"].DeepClone(), ["manifestSha256"] = request["manifestSha256"].DeepClone(),
            ["mode"] = request["mode"].DeepClone(),
            ["previewSha256"] = request["previewSha256"].DeepClone(), ["outputDigest"] = request["outputDigest"].DeepClone(),
            ["unityVersion"] = Application.unityVersion,
            ["addressablesVersion"] = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Addressables).Assembly)?.version,
            ["addressablesSource"] = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Addressables).Assembly)?.source.ToString(),
            ["checks"] = new JArray(), ["loaded"] = new JArray(), ["consumers"] = new JArray(), ["atlases"] = new JArray(), ["retired"] = new JArray(), ["cleanupVerified"] = false, ["returnedToEditMode"] = false
        };

        private static void FailBeforePlay(Exception error)
        {
            RestoreRuntimeDataPath();
            Debug.LogException(error);
            try { var proof = Proof(ReadRequest()); proof["error"] = error.ToString(); WriteProof(proof); }
            catch (Exception writeError) { Debug.LogException(writeError); }
            SessionState.EraseString(Key + "request");
            SessionState.EraseString(Key + "phase");
            EditorApplication.Exit(1);
        }

        private static string Local(string relative)
        {
            Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) && !relative.Contains('\\') && !relative.Contains(':') &&
                !relative.Split('/').Any(part => part == "." || part == ".." || part.Length == 0), "A canonical host-relative path is required.");
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var path = Path.GetFullPath(Path.Combine(root, relative));
            Require(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "The validation path escapes its owned host.");
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked input paths are refused.");
            return path;
        }

        private static JObject Read(string path) => JObject.Parse(File.ReadAllText(path));
        private static JObject ReadSaved() => JObject.Parse(SessionState.GetString(Key + "proof", ""));
        private static void Save(JObject proof) => SessionState.SetString(Key + "proof", proof.ToString());
        private static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static string HashBytes(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static void WriteProof(JObject proof)
        {
            var request = ReadRequest();
            using var stream = new FileStream(Path.Combine((string)request["result"], "native-proof.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(proof.ToString());
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }

    // Pure comparison is separately exercisable without loading Unity. Captures are produced only
    // by the owned original/target AssetDatabase sessions above, never by editing expected YAML.
    public static class ResourceAddressablesSettingsProof
    {
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static string Id(JToken value) => (string)value["guid"] + ":" + (long)value["localId"];
        private static bool Same(JToken left, JToken right) => JToken.DeepEquals(left, right);
        private static JObject Except(JObject value, params string[] fields)
        {
            var copy = (JObject)value.DeepClone();
            foreach (var field in fields) Check(copy.Remove(field), "Unsupported official serialized field layout: " + field);
            return copy;
        }
        private static Dictionary<string, JObject> Unique(IEnumerable<JObject> rows, Func<JObject, string> key)
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var row in rows) Check(result.TryAdd(key(row), row), "Duplicate Settings footprint identity.");
            return result;
        }
        private static string[] Strings(JToken value) => ((JArray)value).Values<string>().ToArray();

        public static JObject Verify(JObject before, JObject after, JObject preview)
        {
            Check((int?)before["schemaVersion"] == 1 && (int?)after["schemaVersion"] == 1 && (bool?)preview["settingsExpectedAbsent"] == false, "A complete existing original Settings footprint is mandatory.");
            Check(Same(before["settings"]["identity"], after["settings"]["identity"]) && (string)before["settings"]["identity"]["path"] == (string)preview["settingsPath"], "Selected Settings GUID/localId/path/type changed.");
            Check(Same(before["layout"], after["layout"]) && (string)before["layout"]["root"] == (string)preview["settingsRoot"], "The official Settings/Group/Schema namespace changed.");
            var originalFields = (JObject)before["settings"]["fields"];
            var currentFields = (JObject)after["settings"]["fields"];
            Check((bool?)currentFields["m_DisableCatalogUpdateOnStart"] == true, "Automatic catalog checks were not disabled.");
            // The official Settings cache is recomputed from the groups and labels being compared below.
            var authoredBefore = Except(originalFields, "m_GroupAssets", "m_LabelTable", "m_DisableCatalogUpdateOnStart", "m_currentHash");
            var authoredAfter = Except(currentFields, "m_GroupAssets", "m_LabelTable", "m_DisableCatalogUpdateOnStart", "m_currentHash");
            var changed = authoredBefore.Properties().Select(property => property.Name).Union(authoredAfter.Properties().Select(property => property.Name))
                .Where(name => !Same(authoredBefore[name], authoredAfter[name])).ToArray();
            Check(changed.Length == 0, "Unrelated Settings fields changed: " + string.Join(", ", changed));
            var entries = ((JArray)preview["entries"]).OfType<JObject>().ToArray();
            var expected = Unique(entries, row => (string)row["guid"]);
            var labels = Strings(before["labels"]);
            var currentLabels = Strings(after["labels"]);
            var labelAdditions = entries.Select(entry => (string)entry["label"]).Except(labels, StringComparer.Ordinal).ToArray();
            Check(labels.Distinct(StringComparer.Ordinal).Count() == labels.Length && currentLabels.Distinct(StringComparer.Ordinal).Count() == currentLabels.Length &&
                currentLabels.Take(labels.Length).SequenceEqual(labels, StringComparer.Ordinal) &&
                new HashSet<string>(currentLabels.Skip(labels.Length), StringComparer.Ordinal).SetEquals(labelAdditions), "External Labels were removed/reordered or an unauthorized Label was added.");
            Check(Same(Except((JObject)originalFields["m_LabelTable"], "m_LabelNames"), Except((JObject)currentFields["m_LabelTable"], "m_LabelNames")), "Unknown LabelTable fields changed.");
            Check(Strings(originalFields["m_LabelTable"]["m_LabelNames"]).SequenceEqual(labels, StringComparer.Ordinal) && Strings(currentFields["m_LabelTable"]["m_LabelNames"]).SequenceEqual(currentLabels, StringComparer.Ordinal), "Label public/serialized footprints disagree.");

            var originalGroups = Unique(((JArray)before["groups"]).OfType<JObject>(), row => Id(row["node"]["identity"]));
            var currentGroups = Unique(((JArray)after["groups"]).OfType<JObject>(), row => Id(row["node"]["identity"]));
            Check(new HashSet<string>(((JArray)originalFields["m_GroupAssets"]).Select(Id), StringComparer.Ordinal).SetEquals(originalGroups.Keys) &&
                new HashSet<string>(((JArray)currentFields["m_GroupAssets"]).Select(Id), StringComparer.Ordinal).SetEquals(currentGroups.Keys) &&
                ((JArray)currentFields["m_GroupAssets"]).Count == currentGroups.Count, "Settings group membership is incomplete or duplicated.");
            var allowedNewNames = new HashSet<string>(entries.Select(row => (string)row["group"]).Except(originalGroups.Values.Select(row => (string)row["name"]), StringComparer.Ordinal), StringComparer.Ordinal);
            var newGroups = currentGroups.Where(pair => !originalGroups.ContainsKey(pair.Key)).Select(pair => pair.Value).ToArray();
            Check(newGroups.Select(row => (string)row["name"]).Distinct(StringComparer.Ordinal).Count() == newGroups.Length && new HashSet<string>(newGroups.Select(row => (string)row["name"]), StringComparer.Ordinal).SetEquals(allowedNewNames), "An unknown Group was added or a selected Group is missing.");
            var allowedNodes = new HashSet<string>(StringComparer.Ordinal) { Id(after["settings"]["identity"]) };
            var allowedChangedFiles = new HashSet<string>(StringComparer.Ordinal) { (string)after["settings"]["identity"]["path"] };
            var actualEntries = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var foreignCount = 0;
            foreach (var pair in currentGroups)
            {
                var current = pair.Value;
                var currentRows = Unique(((JArray)current["entries"]).OfType<JObject>(), row => (string)row["guid"]);
                originalGroups.TryGetValue(pair.Key, out var original);
                allowedNodes.Add(pair.Key);
                if (original != null)
                {
                    Check(Same(original["node"]["identity"], current["node"]["identity"]) && Same(Except((JObject)original["node"]["fields"], "m_SerializeEntries"), Except((JObject)current["node"]["fields"], "m_SerializeEntries")) && Same(original["schemas"], current["schemas"]), "An original Group/schema identity, path or unrelated serialized field changed.");
                    foreach (var old in ((JArray)original["entries"]).OfType<JObject>())
                    {
                        Check(currentRows.TryGetValue((string)old["guid"], out var value), "A foreign/localization Entry was removed.");
                        if (!expected.TryGetValue((string)old["guid"], out var authorized)) { foreignCount++; Check(Same(old, value), "A foreign/localization Entry or labels changed."); }
                        else
                        {
                            Check((string)original["name"] == (string)authorized["group"] && (string)old["address"] == (string)authorized["address"] &&
                                Same(Except(old, "labels", "fields"), Except(value, "labels", "fields")) &&
                                Same(Except((JObject)old["fields"], "m_SerializedLabels"), Except((JObject)value["fields"], "m_SerializedLabels")) &&
                                new HashSet<string>(Strings(old["labels"]).Append((string)authorized["label"]), StringComparer.Ordinal).SetEquals(Strings(value["labels"])), "An existing selected Entry changed beyond its certified label addition.");
                        }
                    }
                    Check(currentRows.Keys.Except(((JArray)original["entries"]).Select(row => (string)row["guid"]), StringComparer.Ordinal).All(expected.ContainsKey), "An unauthorized Entry was added to an original Group.");
                    if (((JArray)original["entries"]).All(row => expected.ContainsKey((string)row["guid"])) && entries.Any(row => (string)row["group"] == (string)original["name"]))
                        allowedChangedFiles.Add((string)original["node"]["identity"]["path"]);
                }
                foreach (var row in currentRows.Values)
                {
                    Check(actualEntries.TryAdd((string)row["guid"], row), "A source GUID appears in multiple Groups.");
                    if (expected.TryGetValue((string)row["guid"], out var item))
                        Check((string)row["path"] == (string)item["path"] && (string)row["address"] == (string)item["address"] && (string)current["name"] == (string)item["group"] && Strings(row["labels"]).Contains((string)item["label"], StringComparer.Ordinal), "A selected Entry canonical identity/path differs.");
                    else Check(original != null, "A new Group contains an unselected Entry.");
                }
                if (original == null)
                {
                    Check((string)current["node"]["identity"]["path"] == (string)after["layout"]["groups"] + "/" + (string)current["name"] + ".asset", "A new migration Group borrowed an unapproved path/identity.");
                    var schemas = ((JArray)current["schemas"]).OfType<JObject>().ToArray();
                    Check(schemas.Length == 2 && schemas.Count(schema => ((string)schema["identity"]["type"]).StartsWith("UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema,", StringComparison.Ordinal)) == 1 &&
                        schemas.Count(schema => ((string)schema["identity"]["type"]).StartsWith("UnityEditor.AddressableAssets.Settings.GroupSchemas.ContentUpdateGroupSchema,", StringComparison.Ordinal)) == 1, "An unknown schema was introduced in a new migration Group.");
                    foreach (var schema in schemas)
                    {
                        var type = ((string)schema["identity"]["type"]).Split(',')[0].Split('.').Last();
                        Check((string)schema["identity"]["path"] == (string)after["layout"]["schemas"] + "/" + (string)current["name"] + "_" + type + ".asset", "A new schema borrowed an unapproved path/identity.");
                        allowedNodes.Add(Id(schema["identity"]));
                    }
                    foreach (var row in currentRows.Values) Check(Strings(row["labels"]).SequenceEqual(new[] { (string)expected[(string)row["guid"]]["label"] }, StringComparer.Ordinal) && (bool?)row["readOnly"] == false && (bool?)row["flagged"] == false, "An uncertified new Entry field/label was introduced.");
                }
            }
            Check(originalGroups.Keys.All(currentGroups.ContainsKey) && expected.Keys.All(actualEntries.ContainsKey), "Original Groups or selected Entries disappeared.");

            var previousRegistration = (JObject)before["registration"];
            var registration = (JObject)after["registration"];
            Check(Same(previousRegistration["scenes"], registration["scenes"]), "A foreign BuildSettings scene/path/order changed.");
            Check(Same(Except((JObject)previousRegistration["fields"], "m_configObjects"), Except((JObject)registration["fields"], "m_configObjects")), "An unrelated serialized BuildSettings field changed.");
            var previousConfigs = (JObject)previousRegistration["configs"];
            var configs = (JObject)registration["configs"];
            foreach (var property in previousConfigs.Properties()) Check(Same(property.Value, configs[property.Name]), "A foreign/default config identity changed: " + property.Name);
            var addedConfigs = configs.Properties().Where(property => previousConfigs[property.Name] == null).ToArray();
            if ((bool?)preview["useAsDefault"] == true && previousRegistration["defaultSettings"].Type == JTokenType.Null)
            {
                Check(addedConfigs.Length == 1 && addedConfigs[0].Name == "com.unity.addressableassets" && Same(registration["defaultSettings"], after["settings"]["identity"]) &&
                    (string)addedConfigs[0].Value["path"] == "Assets/AddressableAssetsData/DefaultObject.asset", "The only authorized default registration was not created exactly.");
                allowedNodes.Add(Id(addedConfigs[0].Value));
                allowedChangedFiles.Add("ProjectSettings/EditorBuildSettings.asset");
            }
            else Check(addedConfigs.Length == 0 && Same(previousRegistration["defaultSettings"], registration["defaultSettings"]), "An unapproved default Settings/configuration changed.");
            var oldNodes = Unique(((JArray)before["nodes"]).OfType<JObject>(), row => Id(row["identity"]));
            var nodes = Unique(((JArray)after["nodes"]).OfType<JObject>(), row => Id(row["identity"]));
            foreach (var pair in oldNodes)
            {
                Check(nodes.TryGetValue(pair.Key, out var node), "An original persistent dependency disappeared.");
                if (!allowedNodes.Contains(pair.Key)) Check(Same(pair.Value, node), "An unrelated schema/profile/configuration/dependency field or byte changed.");
            }
            Check(nodes.Keys.Except(oldNodes.Keys, StringComparer.Ordinal).All(allowedNodes.Contains), "An unknown persistent dependency was added.");
            var files = Unique(((JArray)after["files"]).OfType<JObject>(), row => (string)row["path"]);
            foreach (var file in ((JArray)before["files"]).OfType<JObject>())
                Check(files.TryGetValue((string)file["path"], out var actual) && (allowedChangedFiles.Contains((string)file["path"]) || Same(file, actual)), "An original foreign asset/schema/profile/config/meta byte changed: " + file["path"]);
            Check(Same(before["folders"], after["folders"]), "An original foreign Folder Entry membership changed.");
            return new JObject { ["existingSettingsVerified"] = true, ["baselineSettingsPresent"] = true, ["baselineForeignEntries"] = foreignCount, ["baselineForeignLabels"] = labels.Length, ["preservedOriginalGroups"] = originalGroups.Count, ["preservedPersistentNodes"] = oldNodes.Count, ["currentEntries"] = actualEntries.Count, ["currentLabels"] = after["labels"].DeepClone(), ["comparison"] = "Pinned cold official serialization; canonical GUID/localId/path; only certified groups/labels/startup policy/default registration deltas." };
        }
    }
}
#endif
