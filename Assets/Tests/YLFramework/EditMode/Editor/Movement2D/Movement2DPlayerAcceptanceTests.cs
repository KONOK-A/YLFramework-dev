using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Character2D;
using YLFramework.Platform2D;

namespace YLFramework.Tests.Movement2D
{
    [Category("YL.Module.Movement2D")]
    [Category("YL.Special.Movement2DPackage")]
    [Category("YL.Package.Movement2DStandalone")]
    public sealed class Movement2DPlayerAcceptanceTests
    {
        private const string ProbeAssembly = "YLFramework.Movement2D.Acceptance";
        private const string RunnerScene = "Assets/YLTestingProfile/Empty.unity";
        private static string Project => Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);
        private static readonly string[] _frameworkAssemblies = { "YLFramework.Runtime", "YLFramework.Character2D", "YLFramework.Platform2D", ProbeAssembly };

        [Serializable]
        private sealed class Owner
        {
            public int schemaVersion;
            public string purpose;
            public string kind;
            public string path;
            public string workspaceId;
        }

        [Serializable]
        private sealed class OwnedAsset
        {
            public string path;
            public string guid;
            public string sha256;
            public string metaSha256;
        }

        [Serializable]
        private sealed class OwnedRun
        {
            public int schemaVersion = 1;
            public string token;
            public string output;
            public string player;
            public string folder;
            public string folderGuid;
            public string folderMetaSha256;
            public string sceneBeforeBuildSha256;
            public List<OwnedAsset> assets = new List<OwnedAsset>();
        }

        [Test]
        public void MonoPlayersRunSerializedPhysicsEventsPathsAndPooledReuseWithOnlyMainAndMovement2D()
        {
            string output = RequireContext();
            RequireEmptyScenes();
            Assert.That(Application.platform, Is.EqualTo(RuntimePlatform.WindowsEditor));
            Assert.That(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), Is.True);
            foreach (string name in new[] { "movement2d-player-location.txt", "Movement2D-owned-created.json", "Movement2D-owned-prepared.json", "Movement2D-owned-built.json", "Movement2D-owned-complete.json",
                "Movement2DPlayer-1.json", "Movement2DPlayer-2.json", "Movement2DPlayer-1.log", "Movement2DPlayer-2.log" })
            {
                Assert.That(File.Exists(Path.Combine(output, name)) || Directory.Exists(Path.Combine(output, name)), Is.False, name);
            }
            NoLinks(Physical(RunnerScene));
            Assert.That(File.Exists(Physical(RunnerScene)), Is.True, "SessionNative must prepare its owned empty scene.");
            // Only after every currently opened scene was proven empty and clean may the runner replace it.
            EditorSceneManager.OpenScene(RunnerScene, OpenSceneMode.Single);
            RequireEmptyScenes();
            var originalActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            OwnedRun run = null;
            try
            {
                if (SceneManager.GetActiveScene() != scene)
                {
                    SceneManager.SetActiveScene(scene);
                }
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scene));
                // Unity 6 can unload locally held assets during a scene switch. Create all assets after the last switch.
                run = NewOwnedRun(output);
                WriteNew(Path.Combine(output, "movement2d-player-location.txt"), run.player);
                WriteNew(Path.Combine(output, "Movement2D-owned-created.json"), JsonUtility.ToJson(run, true));
                var prefab = CreateScenarioPrefab(run, scene);
                var host = new GameObject("OwnedMovement2DPlayerProbe");
                SceneManager.MoveGameObjectToScene(host, scene);
                var probeType = Type.GetType("YLFramework.Movement2D.Acceptance.Movement2DPlayerProbe, " + ProbeAssembly, true);
                var probe = host.AddComponent(probeType);
                using (var serialized = new SerializedObject(probe))
                {
                    serialized.FindProperty("scenarioPrefab").objectReferenceValue = prefab;
                    serialized.FindProperty("runToken").stringValue = run.token;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    Assert.That(serialized.FindProperty("scenarioPrefab").objectReferenceValue, Is.SameAs(prefab));
                    Assert.That((bool)prefab, Is.True);
                }
                string scenePath = run.folder + "/Player.unity";
                Assert.That(File.Exists(Physical(scenePath)) || File.Exists(Physical(scenePath + ".meta")), Is.False);
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                CaptureNewAsset(run, scenePath);
                WriteNew(Path.Combine(output, "Movement2D-owned-prepared.json"), JsonUtility.ToJson(run, true));
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = Path.Combine(run.player, "P.exe"),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development,
                    extraScriptingDefines = new[] { "YL_MOVEMENT2D_ACCEPTANCE" }
                });
                // BuildPipeline can restore the open scene using new native object handles.
                scene = SceneManager.GetSceneByPath(scenePath);
                originalActive = SceneManager.GetSceneByPath(RunnerScene);
                Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
                var builtProbe = scene.GetRootGameObjects().Single().GetComponent(probeType);
                var builtPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(run.assets.Single(asset => asset.path.EndsWith(".prefab", StringComparison.Ordinal)).path);
                Assert.That((bool)builtProbe && (bool)builtPrefab, Is.True);
                RecordBuiltScene(run, scene, builtProbe, builtPrefab);
                WriteNew(Path.Combine(output, "Movement2D-owned-built.json"), JsonUtility.ToJson(run, true));
                Assert.That(build.summary.result, Is.EqualTo(UnityEditor.Build.Reporting.BuildResult.Succeeded));
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
                if (originalActive.IsValid() && originalActive.isLoaded)
                {
                    SceneManager.SetActiveScene(originalActive);
                    Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(originalActive));
                }
                if (scene.IsValid() && scene.isLoaded)
                {
                    Assert.That(EditorSceneManager.CloseScene(scene, true), Is.True);
                }
            }
            Assert.That(run, Is.Not.Null);
            string managed = Path.Combine(run.player, "P_Data/Managed");
            NoLinks(managed);
            CollectionAssert.AreEquivalent(_frameworkAssemblies.Select(name => name + ".dll"),
                Directory.GetFiles(managed, "YLFramework*.dll").Select(Path.GetFileName), "The real Player must contain only main, both new Runtimes and this probe.");
            int previousPid = 0;
            for (int round = 1; round <= 2; round++)
            {
                string result = Path.Combine(run.player, "result-" + round + ".json");
                string log = Path.Combine(run.player, "player-" + round + ".log");
                Assert.That(File.Exists(result) || Directory.Exists(result) || File.Exists(log) || Directory.Exists(log), Is.False);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(run.player, "P.exe"))
                {
                    UseShellExecute = false,
                    WorkingDirectory = run.player,
                    Arguments = "-batchmode -nographics -ylMovement2DResult \"" + result + "\" -logFile \"" + log + "\""
                });
                Assert.That(process, Is.Not.Null);
                try
                {
                    if (!process.WaitForExit(90000))
                    {
                        process.Kill();
                        process.WaitForExit(10000);
                        Assert.Fail("The actual Movement2D Player timed out. A killed process is not a successful run.");
                    }
                }
                finally
                {
                    // Preserve raw failures before any exit-code, parse or metric assertion can fail.
                    foreach (string source in new[] { result, log })
                    {
                        if (File.Exists(source))
                        {
                            NoLinks(source);
                            File.Copy(source, Path.Combine(output, "Movement2DPlayer-" + round + Path.GetExtension(source)), false);
                        }
                    }
                }
                Assert.That(process.ExitCode, Is.Zero, "See the preserved raw JSON and Player log, not only the NUnit failure.");
                AssertPlayerReport(ReadStrictReport(result), process.Id, previousPid, round, run.token);
                previousPid = process.Id;
            }
            DeleteOwnedAssets(run);
            WriteNew(Path.Combine(output, "Movement2D-owned-complete.json"), JsonUtility.ToJson(run, true));
        }

        private static GameObject CreateScenarioPrefab(OwnedRun run, Scene scene)
        {
            var root = new GameObject("Movement2DPackageAcceptance");
            root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                GameObject Child(string name, Vector3 position)
                {
                    var child = new GameObject(name);
                    child.transform.SetParent(root.transform, false);
                    child.transform.position = position;
                    return child;
                }
                Child("Floor", new Vector3(0, -1.5f)).AddComponent<BoxCollider2D>().size = new Vector2(12, 1);
                Child("Wall", new Vector3(0.95f, 1)).AddComponent<BoxCollider2D>().size = new Vector2(0.5f, 6);
                Child("Ceiling", new Vector3(0, 1.85f)).AddComponent<BoxCollider2D>().size = new Vector2(4, 0.5f);
                var motorObject = Child("Motor", Vector3.zero);
                var body = motorObject.AddComponent<Rigidbody2D>();
                body.gravityScale = 2;
                body.constraints = RigidbodyConstraints2D.FreezeRotation;
                motorObject.AddComponent<BoxCollider2D>().size = new Vector2(1, 2);
                var motor = motorObject.AddComponent<CharacterController2D>();
                motor.gravityScale = 2;
                motor.maxJumpCount = 2;
                motor.ChecksWalls = true;
                motor.ChecksCeiling = true;
                motor.onGroundValueChange = new Character2D.BoolUnityEvent();
                motor.onTouchWallValueChange = new Character2D.BoolUnityEvent();
                motor.onTopValueChange = new Character2D.BoolUnityEvent();
                motor.onJumpCountValueChange = new IntUnityEvent();
                motor.onVelocityValueChange = new Vector2UnityEvent();
                var receiverType = Type.GetType("YLFramework.Movement2D.Acceptance.Movement2DEventReceiver, " + ProbeAssembly, true);
                var receiver = motorObject.AddComponent(receiverType);
                SetReference(receiver, "motor", motor);
                foreach (var binding in new[] { ("onGroundValueChange", "OnGround"), ("onTouchWallValueChange", "OnWall"), ("onTopValueChange", "OnTop"),
                    ("onJumpCountValueChange", "OnJump"), ("onVelocityValueChange", "OnVelocity") })
                {
                    SetPersistentCallback(motor, binding.Item1, receiver, binding.Item2);
                }
                int index = 0;
                foreach (var mode in new[] { MoveType.Loop, MoveType.PingPong, MoveType.Once })
                {
                    var origin = new Vector3(20, -20 - index++ * 10);
                    var platformObject = Child("Platform" + mode, origin);
                    platformObject.AddComponent<BoxCollider2D>().size = new Vector2(4, 1);
                    var platform = platformObject.AddComponent<MoveablePlatform2D>();
                    platform.MoveType = mode;
                    platform.Speed = 10;
                    platform.Positions.Add(origin);
                    platform.Positions.Add(origin + Vector3.right * 2);
                    platform.onStartRun = new UnityEngine.Events.UnityEvent();
                    platform.onMove = new Vector3UnityEvent();
                    platform.onPause = new Platform2D.BoolUnityEvent();
                    platform.onStop = new UnityEngine.Events.UnityEvent();
                    using (var serialized = new SerializedObject(platform))
                    {
                        serialized.FindProperty("runOnAwake").boolValue = false;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                    platform.GetComponent<PlatformCatcher2D>().areas = new[]
                    {
                        new Area { from = new Vector3(-1.5f, 1), to = new Vector3(1.5f, 1) },
                        new Area { from = new Vector3(-1.5f, 1), to = new Vector3(1.5f, 1) }
                    };
                    var sink = platformObject.AddComponent(receiverType);
                    SetReference(sink, "platform", platform);
                    foreach (var binding in new[] { ("onStartRun", "OnStart"), ("onMove", "OnMove"), ("onPause", "OnPause"), ("onStop", "OnStop") })
                    {
                        SetPersistentCallback(platform, binding.Item1, sink, binding.Item2);
                    }
                    var passenger = Child("Passenger" + mode, origin + Vector3.up);
                    var passengerBody = passenger.AddComponent<Rigidbody2D>();
                    passengerBody.gravityScale = 0;
                    passengerBody.constraints = RigidbodyConstraints2D.FreezeRotation;
                    passenger.AddComponent<BoxCollider2D>().size = new Vector2(0.5f, 0.8f);
                    passenger.AddComponent<CircleCollider2D>().radius = 0.2f;
                }
                string path = run.folder + "/Movement2DPackageAcceptance.prefab";
                Assert.That(File.Exists(Physical(path)) || File.Exists(Physical(path + ".meta")), Is.False);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                Assert.That((bool)prefab && !prefab.activeSelf, Is.True);
                Assert.That(prefab.name, Is.EqualTo("Movement2DPackageAcceptance"));
                CaptureNewAsset(run, path);
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void SetReference(Component component, string field, UnityEngine.Object target)
        {
            using var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(field);
            Assert.That(property, Is.Not.Null, field);
            property.objectReferenceValue = target;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(property.objectReferenceValue, Is.SameAs(target));
        }

        private static void SetPersistentCallback(Component source, string field, Component receiver, string method)
        {
            using var serialized = new SerializedObject(source);
            var calls = serialized.FindProperty(field).FindPropertyRelative("m_PersistentCalls.m_Calls");
            Assert.That(calls, Is.Not.Null, field);
            Assert.That(calls.arraySize, Is.Zero);
            calls.arraySize = 1;
            var call = calls.GetArrayElementAtIndex(0);
            call.FindPropertyRelative("m_Target").objectReferenceValue = receiver;
            call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = receiver.GetType().AssemblyQualifiedName;
            call.FindPropertyRelative("m_MethodName").stringValue = method;
            call.FindPropertyRelative("m_Mode").enumValueIndex = 0;
            call.FindPropertyRelative("m_CallState").enumValueIndex = 2;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(call.FindPropertyRelative("m_Target").objectReferenceValue, Is.SameAs(receiver));
        }

        private static JObject ReadStrictReport(string path)
        {
            NoLinks(path);
            string json = File.ReadAllText(path, new UTF8Encoding(false, true));
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                while (reader.Read())
                {
                    Assert.That(reader.TokenType, Is.Not.EqualTo(JsonToken.Comment).And.Not.EqualTo(JsonToken.Undefined), "Reports must be JSON, not JavaScript or commented JSON.");
                    if (reader.TokenType == JsonToken.String || reader.TokenType == JsonToken.PropertyName)
                    {
                        Assert.That(reader.QuoteChar, Is.EqualTo('"'), "Single-quoted strings and unquoted keys are not JSON.");
                    }
                    if (reader.TokenType == JsonToken.Float)
                    {
                        double value = Convert.ToDouble(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
                        Assert.That(double.IsNaN(value) || double.IsInfinity(value), Is.False);
                    }
                }
            }
            // Json.NET accepts trailing commas. Inspect punctuation outside strings without treating escaped quotes as delimiters.
            var punctuation = json.ToCharArray();
            bool inString = false, escaped = false;
            for (int index = 0; index < punctuation.Length; index++)
            {
                char character = punctuation[index];
                if (inString)
                {
                    punctuation[index] = ' ';
                    Assert.That(character >= ' ', Is.True, "Unescaped control characters are not JSON strings.");
                    if (escaped)
                    {
                        Assert.That("\"\\/bfnrtu".IndexOf(character), Is.GreaterThanOrEqualTo(0), "Only JSON string escapes are permitted.");
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                }
                else if (character == '"')
                {
                    inString = true;
                    // Keep a token so a final string element cannot resemble a trailing comma.
                    punctuation[index] = '0';
                }
            }
            string outsideStrings = new string(punctuation);
            Assert.That(System.Text.RegularExpressions.Regex.IsMatch(outsideStrings, @",\s*[}\]]"), Is.False, "Trailing commas are not JSON.");
            foreach (System.Text.RegularExpressions.Match literal in System.Text.RegularExpressions.Regex.Matches(outsideStrings, @"[^\s{}\[\],:]+"))
            {
                Assert.That(literal.Value == "true" || literal.Value == "false" || literal.Value == "null" ||
                    System.Text.RegularExpressions.Regex.IsMatch(literal.Value, @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$"), Is.True, "Non-JSON literal: " + literal.Value);
            }
            return JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }

        private static void AssertShape(JObject value, string[] strings, string[] booleans, string[] integers, string[] floats, string[] arrays)
        {
            CollectionAssert.AreEquivalent(strings.Concat(booleans).Concat(integers).Concat(floats).Concat(arrays), value.Properties().Select(property => property.Name), "All report fields are required; unknown fields are rejected.");
            foreach (string key in strings)
            {
                Assert.That(value[key].Type == JTokenType.String || key == "error" && value[key].Type == JTokenType.Null, Is.True, key);
            }
            foreach (string key in booleans)
            {
                Assert.That(value[key].Type, Is.EqualTo(JTokenType.Boolean), key);
            }
            foreach (string key in integers)
            {
                Assert.That(value[key].Type, Is.EqualTo(JTokenType.Integer), key);
            }
            foreach (string key in floats)
            {
                Assert.That(value[key].Type == JTokenType.Float || value[key].Type == JTokenType.Integer, Is.True, key);
            }
            foreach (string key in arrays)
            {
                Assert.That(value[key].Type, Is.EqualTo(JTokenType.Array), key);
            }
        }

        private static void AssertPlayerReport(JObject report, int pid, int previousPid, int round, string token)
        {
            AssertShape(report,
                new[] { "status", "error", "unityVersion", "platform", "backend", "token", "characterAssembly", "platformAssembly", "probeAssembly" },
                new[] { "isEditor", "serializedPrefab", "committedCallbacks", "groundCollider", "wallCollider", "ceilingCollider", "pauseHeld", "callbackDisable", "pooledMotor", "pooledPlatform", "released" },
                new[] { "schemaVersion", "pid", "round", "persistentListeners", "groundEvents", "wallEvents", "ceilingEvents", "jumpEvents", "landingEvents", "velocityEvents" },
                new[] { "jumpRise" }, new[] { "frameworkAssemblies", "paths" });
            Assert.That(report.Value<int>("schemaVersion"), Is.EqualTo(1));
            Assert.That(report.Value<string>("status"), Is.EqualTo("Passed"), report.Value<string>("error"));
            Assert.That(report.Value<string>("error"), Is.Null.Or.Empty);
            Assert.That(report.Value<int>("pid"), Is.EqualTo(pid).And.Not.EqualTo(previousPid));
            Assert.That(report.Value<int>("round"), Is.EqualTo(round));
            Assert.That(report.Value<string>("token"), Is.EqualTo(token));
            Assert.That(report.Value<bool>("isEditor"), Is.False);
            Assert.That(report.Value<string>("platform"), Is.EqualTo("WindowsPlayer"));
            Assert.That(report.Value<string>("backend"), Is.EqualTo("Mono"));
            Assert.That(report.Value<string>("unityVersion"), Is.EqualTo(Application.unityVersion));
            Assert.That(report.Value<string>("characterAssembly"), Is.EqualTo("YLFramework.Character2D"));
            Assert.That(report.Value<string>("platformAssembly"), Is.EqualTo("YLFramework.Platform2D"));
            Assert.That(report.Value<string>("probeAssembly"), Is.EqualTo(ProbeAssembly));
            Assert.That(report["frameworkAssemblies"].All(value => value.Type == JTokenType.String), Is.True);
            CollectionAssert.AreEquivalent(_frameworkAssemblies, report["frameworkAssemblies"].Values<string>());
            foreach (string key in new[] { "serializedPrefab", "committedCallbacks", "groundCollider", "wallCollider", "ceilingCollider", "pauseHeld", "callbackDisable", "pooledMotor", "pooledPlatform", "released" })
            {
                Assert.That(report.Value<bool>(key), Is.True, key);
            }
            Assert.That(report.Value<int>("persistentListeners"), Is.EqualTo(17));
            Assert.That(report.Value<int>("groundEvents"), Is.GreaterThanOrEqualTo(2));
            Assert.That(report.Value<int>("jumpEvents"), Is.GreaterThanOrEqualTo(3));
            foreach (string key in new[] { "wallEvents", "ceilingEvents", "landingEvents", "velocityEvents" })
            {
                Assert.That(report.Value<int>(key), Is.GreaterThan(0), key);
            }
            Assert.That(report.Value<float>("jumpRise"), Is.InRange(0.3f, 1.1f));
            var paths = (JArray)report["paths"];
            Assert.That(paths.Count, Is.EqualTo(3));
            Assert.That(paths.All(value => value is JObject), Is.True);
            CollectionAssert.AreEquivalent(new[] { "Loop", "PingPong", "Once" }, paths.Select(value => value.Value<string>("mode")));
            foreach (JObject path in paths)
            {
                AssertShape(path, new[] { "mode" }, new[] { "onlyFixedMoves", "committedCallbacks", "wrapped", "reversed" },
                    new[] { "starts", "moves", "stops", "pauses" }, new[] { "furthest", "largestStep", "passengerError", "endpoint" }, Array.Empty<string>());
                string mode = path.Value<string>("mode");
                Assert.That(path.Value<bool>("onlyFixedMoves") && path.Value<bool>("committedCallbacks"), Is.True, mode);
                Assert.That(path.Value<int>("starts"), Is.EqualTo(mode == "Once" ? 2 : 1), mode);
                Assert.That(path.Value<int>("stops"), Is.EqualTo(mode == "Once" ? 2 : 1), mode);
                Assert.That(path.Value<int>("pauses"), Is.EqualTo(2), mode);
                Assert.That(path.Value<int>("moves"), Is.GreaterThanOrEqualTo(10), mode);
                Assert.That(path.Value<float>("furthest"), Is.InRange(1.5f, 2.01f), mode);
                Assert.That(path.Value<float>("passengerError"), Is.InRange(0, 0.03f), mode);
                Assert.That(path.Value<float>("endpoint"), Is.InRange(0, 2.01f), mode);
                Assert.That(path.Value<bool>("wrapped"), Is.EqualTo(mode == "Loop"), mode);
                if (mode == "PingPong")
                {
                    Assert.That(path.Value<bool>("reversed"), Is.True);
                    Assert.That(path.Value<float>("largestStep"), Is.InRange(0, 0.21f));
                }
                if (mode == "Once")
                {
                    Assert.That(path.Value<float>("endpoint"), Is.EqualTo(2).Within(0.001f));
                }
            }
        }

        private static string RequireContext()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlaying && !EditorApplication.isCompiling, Is.True, "Only a tool-owned idle batch host may run this fixture.");
            string output = Path.GetFullPath(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput());
            NoLinks(Project);
            string hostPath = Path.Combine(Project, ".yl-owner.json");
            NoLinks(hostPath);
            var host = JsonUtility.FromJson<Owner>(File.ReadAllText(hostPath));
            Assert.That(host.schemaVersion, Is.EqualTo(1));
            Assert.That(host.purpose, Is.EqualTo("YLTesting.Directory"));
            Assert.That(host.kind, Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath(host.path), Is.EqualTo(Project).IgnoreCase);
            Assert.That(host.workspaceId, Is.Not.Null.And.Not.Empty);
            Assert.That(Directory.Exists(output), Is.True);
            NoLinks(output);
            var ancestor = new DirectoryInfo(output);
            while (ancestor != null && !File.Exists(Path.Combine(ancestor.FullName, ".yl-owner.json")))
            {
                ancestor = ancestor.Parent;
            }
            Assert.That(ancestor, Is.Not.Null);
            string ownerPath = Path.Combine(ancestor.FullName, ".yl-owner.json");
            NoLinks(ownerPath);
            var result = JsonUtility.FromJson<Owner>(File.ReadAllText(ownerPath));
            Assert.That(result.schemaVersion, Is.EqualTo(1));
            Assert.That(result.purpose, Is.EqualTo("YLTesting.Directory"));
            Assert.That(result.kind, Is.EqualTo("Result"));
            Assert.That(result.workspaceId, Is.EqualTo(host.workspaceId));
            Assert.That(Path.GetFullPath(result.path), Is.EqualTo(ancestor.FullName).IgnoreCase);
            CollectionAssert.AreEquivalent(new[] { "com.kano.ylframework", "com.kano.ylframework.movement2d" },
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Where(package => package.name == "com.kano.ylframework" ||
                    package.name.StartsWith("com.kano.ylframework.", StringComparison.Ordinal)).Select(package => package.name));
            Assert.That(typeof(CharacterController2D).Assembly.GetName().Name, Is.EqualTo("YLFramework.Character2D"));
            Assert.That(typeof(MoveablePlatform2D).Assembly.GetName().Name, Is.EqualTo("YLFramework.Platform2D"));
            Assert.That(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Any(assembly => assembly.name == ProbeAssembly), Is.False,
                "The probe must be absent from an ordinary Player and enabled only for the explicit acceptance build.");
            var probe = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Single(assembly => assembly.name == ProbeAssembly);
            var references = probe.assemblyReferences.Select(assembly => assembly.name).ToArray();
            CollectionAssert.AreEquivalent(new[] { "YLFramework.Character2D", "YLFramework.Platform2D" },
                references.Where(name => name.StartsWith("YLFramework.", StringComparison.Ordinal)));
            Assert.That(references.All(name => name == "YLFramework.Character2D" || name == "YLFramework.Platform2D" || name == "UnityEngine.UI" || name == "UnityEditor.UI"), Is.True,
                "Only the implicit UGUI Editor/runtime references may supplement the two declared runtime references; the actual Player closure is checked after building.");
            return output;
        }

        private static void RequireEmptyScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                Assert.That(scene.isDirty, Is.False, "Never save or replace an unknown dirty scene.");
                Assert.That(scene.rootCount, Is.Zero, "The session must prepare every open scene empty, not just its active scene.");
                Assert.That(string.IsNullOrEmpty(scene.path) || scene.path == RunnerScene, Is.True);
            }
        }

        private static OwnedRun NewOwnedRun(string output)
        {
            string token = Guid.NewGuid().ToString("N");
            string folder = "Assets/__YLMovement2D_" + token;
            string player = Path.Combine(Project, "P", token.Substring(0, 8));
            NoLinks(Physical(folder));
            NoLinks(player);
            Assert.That(Directory.Exists(Physical(folder)) || File.Exists(Physical(folder)) || File.Exists(Physical(folder + ".meta")), Is.False);
            Assert.That(Directory.Exists(player) || File.Exists(player), Is.False);
            string guid = AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            Assert.That(Guid.TryParseExact(guid, "N", out _), Is.True);
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(folder));
            Assert.That(Directory.GetFileSystemEntries(Physical(folder)), Is.Empty);
            Directory.CreateDirectory(player);
            Assert.That(Directory.GetFileSystemEntries(player), Is.Empty);
            var run = new OwnedRun { token = token, output = output, player = player, folder = folder, folderGuid = guid, folderMetaSha256 = Hash(Physical(folder + ".meta")) };
            WriteNew(Path.Combine(player, ".movement2d-owner.json"), JsonUtility.ToJson(run, true));
            return run;
        }

        private static void CaptureNewAsset(OwnedRun run, string path)
        {
            Assert.That(path.StartsWith(run.folder + "/", StringComparison.Ordinal) && Path.GetDirectoryName(path).Replace('\\', '/') == run.folder, Is.True);
            Assert.That(run.assets.Any(asset => asset.path == path), Is.False);
            NoLinks(Physical(path));
            NoLinks(Physical(path + ".meta"));
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(Guid.TryParseExact(guid, "N", out _), Is.True);
            run.assets.Add(new OwnedAsset { path = path, guid = guid, sha256 = Hash(Physical(path)), metaSha256 = Hash(Physical(path + ".meta")) });
        }

        private static void RecordBuiltScene(OwnedRun run, Scene scene, Component probe, GameObject prefab)
        {
            var record = run.assets.Single(asset => asset.path.EndsWith("/Player.unity", StringComparison.Ordinal));
            AssertOwnedSet(run, false);
            Assert.That(scene.IsValid() && scene.isLoaded && !scene.isDirty && scene.path == record.path, Is.True);
            Assert.That(scene.GetRootGameObjects(), Is.EqualTo(new[] { probe.gameObject }));
            using (var serialized = new SerializedObject(probe))
            {
                Assert.That(serialized.FindProperty("scenarioPrefab").objectReferenceValue, Is.SameAs(prefab));
                Assert.That(serialized.FindProperty("runToken").stringValue, Is.EqualTo(run.token));
            }
            foreach (var asset in run.assets)
            {
                Assert.That(AssetDatabase.AssetPathToGUID(asset.path), Is.EqualTo(asset.guid));
                Assert.That(Hash(Physical(asset.path + ".meta")), Is.EqualTo(asset.metaSha256));
                if (asset != record)
                {
                    Assert.That(Hash(Physical(asset.path)), Is.EqualTo(asset.sha256));
                }
            }
            // BuildPipeline may serialize this exact owned scene again. Keep both hashes; never re-baseline other assets.
            run.sceneBeforeBuildSha256 = record.sha256;
            record.sha256 = Hash(Physical(record.path));
        }

        private static void AssertOwnedSet(OwnedRun run, bool includeHashes)
        {
            Assert.That(run.folder, Is.EqualTo("Assets/__YLMovement2D_" + run.token));
            NoLinks(Physical(run.folder));
            NoLinks(Physical(run.folder + ".meta"));
            Assert.That(AssetDatabase.AssetPathToGUID(run.folder), Is.EqualTo(run.folderGuid));
            Assert.That(Hash(Physical(run.folder + ".meta")), Is.EqualTo(run.folderMetaSha256));
            Assert.That(Directory.GetDirectories(Physical(run.folder)), Is.Empty);
            CollectionAssert.AreEquivalent(run.assets.SelectMany(asset => new[] { Physical(asset.path), Physical(asset.path + ".meta") }), Directory.GetFiles(Physical(run.folder)));
            foreach (var asset in run.assets)
            {
                NoLinks(Physical(asset.path));
                NoLinks(Physical(asset.path + ".meta"));
                if (includeHashes)
                {
                    Assert.That(AssetDatabase.AssetPathToGUID(asset.path), Is.EqualTo(asset.guid));
                    Assert.That(Hash(Physical(asset.path)), Is.EqualTo(asset.sha256));
                    Assert.That(Hash(Physical(asset.path + ".meta")), Is.EqualTo(asset.metaSha256));
                }
            }
        }

        private static void DeleteOwnedAssets(OwnedRun run)
        {
            AssertOwnedSet(run, true);
            foreach (var asset in run.assets)
            {
                Assert.That(AssetDatabase.DeleteAsset(asset.path), Is.True, asset.path);
            }
            Assert.That(Directory.GetFileSystemEntries(Physical(run.folder)), Is.Empty);
            Assert.That(AssetDatabase.DeleteAsset(run.folder), Is.True);
        }

        private static string Physical(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Project, path));

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void WriteNew(string path, string text)
        {
            Assert.That(Directory.Exists(Path.GetDirectoryName(path)), Is.True);
            NoLinks(path);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }

        private static void NoLinks(string path)
        {
            if (File.Exists(path))
            {
                Assert.That((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, Is.True, path);
            }
            for (var parent = new DirectoryInfo(File.Exists(path) ? Path.GetDirectoryName(path) : path); parent != null; parent = parent.Parent)
            {
                if (parent.Exists)
                {
                    Assert.That((parent.Attributes & FileAttributes.ReparsePoint) == 0, Is.True, parent.FullName);
                }
            }
        }
    }
}
