using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YLFramework.Buff;
using YLFramework.Character2D;
using YLFramework.DataAssets;
using YLFramework.Editor.Resource;
using YLFramework.Equipment;
using YLFramework.Mission;
using YLFramework.Overlap;
using YLFramework.Platform2D;
using YLFramework.RedDots;
using YLFramework.Resource;
using YLFramework.Skill;
using YLFramework.StateMachine;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.MigrationFixtures
{
    [Category("YL.Special.MigrationFixtures")]
    public class AssemblyMigrationFixtureFreezeTests
    {
        private const string Root = "Assets/Tests/YLFramework/Fixtures/NamespaceMigration/AssemblySplitV1";
        private const string Content = Root + "/Content";
        private const string ProjectId = "yl-before-assembly-split-v1";

        [Test]
        public void FreezeCurrentLayoutThroughUnityExcelAndResourcePublisher()
        {
            Assert.That(Application.isBatchMode, Is.True, "This producer must never run in the user's Editor.");
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var owner = JObject.Parse(File.ReadAllText(Path.Combine(projectRoot, ".yl-owner.json")));
            Assert.That((string)owner["purpose"], Is.EqualTo("YLTesting.Directory"));
            Assert.That((string)owner["kind"], Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath((string)owner["path"]), Is.EqualTo(projectRoot).IgnoreCase);
            Assert.That(Directory.Exists(Physical(Root)), Is.False, "Never replace a captured fixture or adopt unknown output.");
            Assert.That(EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(BuildTarget.StandaloneWindows64));
            Folder(Content + "/Domain");
            Folder(Content + "/Behavior");
            Folder(Root + "/Excel");
            var assets = new List<string>();
            var workbooks = new JArray();
            var definitions = new[]
            {
                (Root: typeof(SkillsConfig), Row: typeof(SkillConfigData), List: "skills", Name: "skillName", Sheet: "skills"),
                (Root: typeof(BuffsConfig), Row: typeof(BuffConfigData), List: "buffs", Name: "buffName", Sheet: "buffs"),
                (Root: typeof(MissionsConfig), Row: typeof(MissionConfigData), List: "missions", Name: "missionName", Sheet: "missions"),
                (Root: typeof(EquipmentsConfig), Row: typeof(EquipmentConfigData), List: "equipments", Name: "equipmentName", Sheet: "equipments"),
                (Root: typeof(ScriptableObjectConfigs), Row: typeof(ScriptableObjectData), List: "configs", Name: "configName", Sheet: "dataassets")
            };
            foreach (var definition in definitions)
            {
                string rootPath = Content + "/Domain/" + definition.Root.Name + ".asset";
                string rowPath = Content + "/Domain/" + definition.Row.Name + ".asset";
                var root = NewAsset(definition.Root, rootPath);
                var row = NewAsset(definition.Row, rowPath);
                var rows = (IList)definition.Root.GetField(definition.List, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(root);
                rows.Add(row);
                definition.Row.GetField("parent").SetValue(row, root);
                using (var serialized = new SerializedObject(root))
                {
                    serialized.FindProperty("typeScriptGuid").stringValue = ScriptGuid(row);
                    if (definition.Root != typeof(ScriptableObjectConfigs))
                    {
                        serialized.FindProperty("startId").intValue = 100;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                using (var serialized = new SerializedObject(row))
                {
                    serialized.FindProperty(definition.Name).stringValue = "before-assembly-" + definition.Sheet;
                    serialized.FindProperty("description").stringValue = "Generated current-layout baseline, not an archived historical asset.";
                    if (definition.Row != typeof(MissionConfigData))
                    {
                        serialized.FindProperty("id").intValue = 7;
                    }
                    serialized.FindProperty("icon.projectName").stringValue = ProjectId;
                    serialized.FindProperty("icon.iconName").stringValue = "icons/before-assembly";
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                if (row is SkillConfigData skill)
                {
                    skill.cooldownSeconds = 1.75f;
                    skill.duration = 2.5f;
                    skill.allowCancellation = true;
                    skill.simultaneousSkillIds.AddRange(new[] { 11, 13 });
                }
                if (row is BuffConfigData buff)
                {
                    string effectPath = Content + "/Domain/EffectData.asset";
                    var effect = (EffectData)NewAsset(typeof(EffectData), effectPath);
                    effect.id = 9001;
                    effect.effectName = "before-assembly-effect";
                    buff.effects.Add(effect);
                    buff.duration = 3.25f;
                    EditorUtility.SetDirty(effect);
                    assets.Add(effectPath);
                }
                if (root is MissionsConfig missions)
                {
                    missions.parameters.Add(new MissionParameter { name = "amount", type = MissionParamType.Float, onlySetInProgress = false, onlySetLarger = true, description = "before-assembly-parameter" });
                    ((MissionConfigData)row).conditions.Add(new MissionCondition { paramName = "amount" });
                    using var serialized = new SerializedObject(root);
                    var custom = serialized.FindProperty("parameters").GetArrayElementAtIndex(0).FindPropertyRelative("customData");
                    custom.arraySize = 1;
                    custom.GetArrayElementAtIndex(0).stringValue = "before-assembly-custom";
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    string parameterPath = Root + "/Excel/mission-parameters.xlsx";
                    Export("ListToExcel", typeof(MissionParameter), missions.parameters, parameterPath, "parameters");
                    workbooks.Add(Workbook(parameterPath, "parameters"));
                }
                if (row is EquipmentConfigData equipment)
                {
                    equipment.equipmentType = EquipmentType.Asset;
                    equipment.projectName = ProjectId;
                    equipment.assetName = "Behavior/Components.prefab";
                }
                EditorUtility.SetDirty(root);
                EditorUtility.SetDirty(row);
                AssetDatabase.SaveAssetIfDirty(root);
                AssetDatabase.SaveAssetIfDirty(row);
                string workbook = Root + "/Excel/" + definition.Sheet + ".xlsx";
                Export("ScriptableObjectToExcel", definition.Row, rows, workbook, definition.Sheet);
                workbooks.Add(Workbook(workbook, definition.Sheet));
                assets.Add(rootPath);
                assets.Add(rowPath);
            }
            string statePath = Content + "/Behavior/StateMachine.asset";
            var state = (RuntimeFsmController)NewAsset(typeof(RuntimeFsmController), statePath);
            state.layers.Add("BeforeAssembly");
            state.originGuid = AssetDatabase.AssetPathToGUID(statePath);
            var createState = typeof(ResourceProject).Assembly.GetType("YLFramework.Editor.StateMachine.FsmStateNodeFactory", true)
                .GetMethods(BindingFlags.Static | BindingFlags.Public).Single(method => method.Name == "CreateStateNode" && method.GetParameters().Length == 8);
            createState.Invoke(null, new object[] { state, "Idle", new Rect(0, 0, 200, 40), true, false, null, false, "" });
            createState.Invoke(null, new object[] { state, "Run", new Rect(240, 0, 200, 40), false, false, null, false, "" });
            state.AddParameters(new FsmParameterData { name = "Go", parameterType = ParameterType.Bool });
            typeof(ResourceProject).Assembly.GetType("YLFramework.Editor.StateMachine.FsmTransitionFactory", true)
                .GetMethod("CreateTransition", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new object[] { state, "Idle", "Run" });
            state.GetTransitionData("Idle", "Run").autoSwitch = true;
            state.GetTransitionData("Idle", "Run").groupConditions.Add(new GroupCondition { conditions = new List<FsmConditionData> { new FsmConditionData { parameterName = "Go", targetValue = 1 } } });
            state.Save();
            EditorUtility.SetDirty(state);
            AssetDatabase.SaveAssetIfDirty(state);
            assets.Add(statePath);
            assets.Add(ComponentsPrefab());
            var fsmHost = new GameObject("BeforeAssemblyStateMachine");
            fsmHost.SetActive(false);
            try
            {
                fsmHost.AddComponent<FsmController>().AddRuntimeFsmController(state, "Gameplay");
                string path = Content + "/Behavior/StateMachine.prefab";
                Assert.That(PrefabUtility.SaveAsPrefabAsset(fsmHost, path), Is.Not.Null);
                assets.Add(path);
            }
            finally { Object.DestroyImmediate(fsmHost); }
            AssetDatabase.SaveAssets();
            var project = (ResourceProject)NewAsset(typeof(ResourceProject), Root + "/ResourceProject.asset");
            project.projectId = ProjectId;
            project.compression = ResourceCompression.Lz4;
            project.contentEncryption = ResourceContentEncryption.None;
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(Content) });
            var group = new ResourceGroup { id = "Migration", packing = ResourcePacking.Together };
            foreach (string path in assets)
            {
                group.selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(path), address = path.Substring(Content.Length + 1) });
            }
            project.groups.Add(group);
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            string buildRoot = Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(project, BuildTarget.StandaloneWindows64));
            Assert.That(Directory.Exists(Physical(buildRoot)), Is.False, "Never adopt a previous Resource build workspace.");
            var built = ResourceBundleBuilder.Build(project, BuildTarget.StandaloneWindows64);
            var release = ResourcePublisher.Publish(built, "assembly-migration-v1");
            string relativeRelease = "ResourceV1~/" + release.Catalog.CatalogDigest;
            string capturedRelease = Physical(Root + "/" + relativeRelease);
            Assert.That(Directory.Exists(capturedRelease), Is.False);
            Directory.CreateDirectory(capturedRelease);
            foreach (string file in Directory.GetFiles(release.DirectoryPath))
            {
                File.Copy(file, Path.Combine(capturedRelease, Path.GetFileName(file)));
            }
            var snapshot = ResourceLocalSnapshot.Open(capturedRelease, ProjectId, release.Catalog.CatalogDigest, release.Catalog.Platform, release.Catalog.AppContentCompatibilityId);
            Assert.That(snapshot.Document.CatalogDigest, Is.EqualTo(release.Catalog.CatalogDigest));
            var identity = new JObject
            {
                ["schemaVersion"] = 1,
                ["captureKind"] = "GeneratedCurrentLayoutBeforeAssemblySplit",
                ["fixtureRoot"] = Root,
                ["unityVersion"] = Application.unityVersion,
                ["platform"] = BuildTarget.StandaloneWindows64.ToString(),
                ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
                ["sourceAssembly"] = typeof(SkillConfigData).Assembly.GetName().Name,
                ["assets"] = new JArray(assets.Select(CaptureAsset)),
                ["workbooks"] = workbooks,
                ["catalog"] = new JObject
                {
                    ["versionDirectory"] = relativeRelease,
                    ["projectId"] = release.Catalog.ProjectId,
                    ["catalogDigest"] = release.Catalog.CatalogDigest,
                    ["buildId"] = release.Catalog.BuildId,
                    ["unityCompatibility"] = release.Catalog.UnityCompatibility,
                    ["platform"] = release.Catalog.Platform,
                    ["appContentCompatibilityId"] = release.Catalog.AppContentCompatibilityId,
                    ["assets"] = new JArray(release.Catalog.Assets.Select(asset => new JObject
                    {
                        ["address"] = asset.Address,
                        ["assetType"] = asset.AssetType,
                        ["assetAssembly"] = asset.AssetAssembly,
                        ["guid"] = asset.Guid,
                        ["localId"] = asset.LocalId,
                        ["loadName"] = asset.LoadName
                    }))
                }
            };
            identity["files"] = new JArray(Directory.GetFiles(Physical(Root), "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).Select(path =>
                new JObject { ["path"] = path.Substring(Physical(Root).Length + 1).Replace('\\', '/'), ["length"] = new FileInfo(path).Length, ["sha256"] = Hash(path) }));
            File.WriteAllText(Physical(Root + "/FixtureIdentity.json"), identity.ToString(), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(Root + "/FixtureIdentity.json", ImportAssetOptions.ForceSynchronousImport);
            Assert.That(release.Catalog.Assets.Any(asset => asset.AssetType == typeof(ScriptableObjectData).FullName && asset.AssetAssembly == "YLFramework.Runtime"), Is.True);
            Debug.Log("Captured current-layout assembly migration inputs: " + Physical(Root));
        }

        private static string ComponentsPrefab()
        {
            var root = new GameObject("BeforeAssemblyComponents");
            root.SetActive(false);
            try
            {
                root.AddComponent<Rigidbody2D>();
                root.AddComponent<BoxCollider2D>();
                var motor = root.AddComponent<CharacterController2D>();
                motor.maxFallingVelocity = 23.5f;
                motor.maxJumpCount = 4;
                var target = new GameObject("Target2D");
                target.transform.SetParent(root.transform, false);
                var collider = target.AddComponent<BoxCollider2D>();
                var area = root.AddComponent<OverlapArea2D>();
                area.Mode = OverlapAreaMode.Transform;
                area.Transforms.Add(target.transform);
                area.results.Add(new OverlapHit<Collider2D> { collider = collider, point = new Vector2(2, 3) });
                var circle = root.AddComponent<OverlapCircle2D>();
                circle.Radius = 2;
                circle.LayerMask = 1;
                circle.IsRunning = false;
                var platform = root.AddComponent<Moveable>();
                platform.Positions.Add(Vector3.zero);
                platform.Positions.Add(Vector3.right);
                platform.Speed = 2;
                platform.MoveType = MoveType.Once;
                var indicator = new GameObject("Indicator", typeof(RectTransform), typeof(Image));
                indicator.transform.SetParent(root.transform, false);
                var reddot = root.AddComponent<Reddot>();
                reddot.Path = "BeforeAssembly/Indicator";
                reddot.Indicator = indicator;
                string path = Content + "/Behavior/Components.prefab";
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, path), Is.Not.Null);
                return path;
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static JObject CaptureAsset(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId), Is.True);
            var row = new JObject
            {
                ["path"] = path.Substring(Root.Length + 1),
                ["guid"] = guid,
                ["localId"] = localId,
                ["typeFullName"] = asset.GetType().FullName,
                ["typeAssembly"] = asset.GetType().Assembly.GetName().Name,
                ["sha256"] = Hash(Physical(path)),
                ["metaSha256"] = Hash(Physical(path + ".meta"))
            };
            if (asset is ScriptableObject data)
            {
                row["scriptGuid"] = ScriptGuid(data);
            }
            if (asset is GameObject prefab)
            {
                row["components"] = new JArray(prefab.GetComponentsInChildren<MonoBehaviour>(true).Select(component => new JObject
                {
                    ["typeFullName"] = component.GetType().FullName,
                    ["typeAssembly"] = component.GetType().Assembly.GetName().Name,
                    ["scriptGuid"] = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(component)))
                }));
            }
            return row;
        }

        private static JObject Workbook(string path, string sheet)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            using var file = File.OpenRead(Physical(path));
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var strings = new List<string>();
            var shared = zip.GetEntry("xl/sharedStrings.xml");
            if (shared != null)
            {
                using var stream = shared.Open();
                strings = XDocument.Load(stream).Descendants(ns + "si").Select(node => string.Concat(node.Descendants(ns + "t").Select(text => text.Value))).ToList();
            }
            using var worksheet = zip.GetEntry("xl/worksheets/sheet1.xml").Open();
            var headers = XDocument.Load(worksheet).Descendants(ns + "row").First().Elements(ns + "c").Select(cell =>
                (string)cell.Attribute("t") == "s" ? strings[int.Parse(cell.Element(ns + "v").Value)] : cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? cell.Element(ns + "v")?.Value).ToArray();
            Assert.That(headers, Is.Not.Empty.And.All.Not.Null);
            return new JObject
            {
                ["path"] = path.Substring(Root.Length + 1),
                ["sheet"] = sheet,
                ["headers"] = JArray.FromObject(headers),
                ["sha256"] = Hash(Physical(path)),
                ["metaSha256"] = Hash(Physical(path + ".meta")),
                ["guid"] = AssetDatabase.AssetPathToGUID(path)
            };
        }

        private static void Export(string methodName, Type element, object rows, string path, string sheet)
        {
            var type = Type.GetType("YLFramework.Integrations.Excel.Editor.ExcelAuthoring, YLFramework.Excel.Editor", true);
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(value => value.Name == methodName && value.IsGenericMethodDefinition);
            var arguments = methodName == "ListToExcel" ? new[] { rows, Physical(path), sheet } : new[] { rows, Physical(path), sheet, element };
            method.MakeGenericMethod(element).Invoke(null, arguments);
        }

        private static ScriptableObject NewAsset(Type type, string path)
        {
            var asset = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static string ScriptGuid(ScriptableObject value) => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(value)));
        private static string Physical(string path) => Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetParent(Application.dataPath).FullName, path);
        private static string Hash(string path)
        {
            using var input = File.OpenRead(path);
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(input).Select(value => value.ToString("x2")));
        }

        private static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            Folder(parent);
            Assert.That(AssetDatabase.CreateFolder(parent, Path.GetFileName(path)), Is.Not.Empty, path);
        }
    }
}
