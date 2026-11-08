using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Mission;
using YLFramework.Skill;
using YLFramework.Editor.Buff;
using YLFramework.Editor.Mission;
using YLFramework.Editor.Skill;
#if UNITY_6000_2_OR_NEWER
using TreeView = UnityEditor.IMGUI.Controls.TreeView<int>;
#else
using TreeView = UnityEditor.IMGUI.Controls.TreeView;
#endif

namespace YLFramework.Tests.Extensions.Domain
{
    public class DomainAuthoringTests
    {
        private string _folder;
        private string[] _absentParents = Array.Empty<string>();
        [SetUp]
        public void Setup()
        {
            _absentParents = DomainTestAssets.CaptureAbsentParents();
            _folder = "Assets/DomainFixture-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(_folder));
        }
        [TearDown] public void Cleanup() => DomainTestAssets.DeleteFolder(_folder, _absentParents);

        [TestCase("Skill")]
        [TestCase("Buff")]
        [TestCase("Mission")]
        [Order(-1), Category("YL.Test.ParentCleanup")]
        public void CleanupReturnsNewEmptyGeneratedParents(string module)
        {
            var rootType = module == "Skill" ? typeof(SkillsConfig) : module == "Buff" ? typeof(BuffsConfig) : typeof(MissionsConfig);
            var dataType = module == "Skill" ? typeof(SkillConfigData) : module == "Buff" ? typeof(BuffConfigData) : typeof(MissionConfigData);
            var root = ScriptableObject.CreateInstance(rootType);
            AssetDatabase.CreateAsset(root, _folder + "/Root.asset");
            var controller = typeof(SkillsConfigController).Assembly.GetType("YLFramework.Editor." + module + "." +
                (module == "Skill" ? "Skills" : module == "Buff" ? "Buffs" : "Missions") + "ConfigController");
            controller.GetMethod("Create" + module + "ConfigData", new[] { typeof(Type), rootType })
                .Invoke(null, new object[] { dataType, root });
            Cleanup();
            foreach (var path in _absentParents)
            {
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.False, path);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.False, path + ".meta");
            }
        }

        [Test]
        public void MissionParameterClipboardPrefixKeepsItsExistingProtocolIdentity()
        {
            var type = typeof(MissionsConfigController).Assembly.GetType("YLFramework.Editor.Mission.ParamListTree", true);
            var prefix = type.GetField("ParameterCopyFlag", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(prefix, Is.Not.Null);
            Assert.That(prefix.GetRawConstantValue(), Is.EqualTo("YLFramework.MissionSystem.CopyParamter"));
        }

        [TestCase("Skill", false)]
        [TestCase("Skill", true)]
        [TestCase("Buff", false)]
        [TestCase("Buff", true)]
        [TestCase("Mission", false)]
        [TestCase("Mission", true)]
        [TestCase("Equipment", false)]
        [TestCase("Equipment", true)]
        public void CurrentDomainRootsKeepValuesListsParentAndScriptBindings(string module, bool enabled)
        {
            var types = module switch
            {
                "Skill" => (typeof(SkillsConfig), typeof(SkillConfigData), "skills"),
                "Buff" => (typeof(BuffsConfig), typeof(BuffConfigData), "buffs"),
                "Mission" => (typeof(MissionsConfig), typeof(MissionConfigData), "missions"),
                "Equipment" => (typeof(YLFramework.Equipment.EquipmentsConfig), typeof(YLFramework.Equipment.EquipmentConfigData), "equipments"),
                _ => throw new ArgumentOutOfRangeException(nameof(module), module, "Unknown Gameplay domain.")
            };
            var root = ScriptableObject.CreateInstance(types.Item1);
            var row = ScriptableObject.CreateInstance(types.Item2);
            root.name = "LegacyRoot";
            row.name = "LegacyRow";
            string rootPath = _folder + "/Root.asset";
            string rowPath = _folder + "/Row.asset";
            AssetDatabase.CreateAsset(root, rootPath);
            AssetDatabase.CreateAsset(row, rowPath);
            var list = (IList)types.Item1.GetField(types.Item3, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(root);
            list.Add(row);
            types.Item2.GetField("parent").SetValue(row, root);
            string scriptGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(row)));
            var rootFields = new List<(string Current, object Value)>
            {
                ("typeScriptGuid", scriptGuid),
                ("startId", enabled ? 42 : 0)
            };

            var rowFields = new List<(string Current, object Value)>();
            EffectData effect = null;
            var effectFields = new[] { (Current: "effectName", Value: (object)"legacy-effect") };
            if (module == "Skill")
            {
                rowFields.AddRange(new (string, object)[]
                {
                    ("skillName", "legacy-name"), ("cooldownSeconds", enabled ? 1.75f : 0f),
                    ("simultaneousSkillIds", new[] { 7, 9 }),
                    ("allowCancellation", enabled),
                    ("skillClassFullName", "Legacy.Runtime.Skill"), ("skillClassGuid", "legacy-guid")
                });
            }
            else if (module == "Buff")
            {
                effect = ScriptableObject.CreateInstance<EffectData>();
                AssetDatabase.AddObjectToAsset(effect, row);
                ((BuffConfigData)row).effects.Add(effect);
                rowFields.AddRange(new (string, object)[]
                {
                    ("id", enabled ? 17 : 0), ("buffName", "legacy-name"),
                    ("buffClassFullName", "Legacy.Runtime.Buff"), ("buffClassGuid", "legacy-guid")
                });
            }
            else if (module == "Mission")
            {
                rootFields.Add(("displayName", "legacy-root"));
                ((MissionsConfig)root).parameters.Add(new MissionParameter { name = "amount" });
                ((MissionConfigData)row).conditions.Add(new MissionCondition { paramName = "amount" });
                rootFields.AddRange(new (string, object)[]
                {
                    ("parameters", null),
                    ("parameters.Array.data[0].onlySetInProgress", !enabled),
                    ("parameters.Array.data[0].onlySetLarger", enabled),
                    ("parameters.Array.data[0].customData", new[] { "legacy-custom" })
                });
                rowFields.AddRange(new (string, object)[]
                {
                    ("missionName", "legacy-name"), ("missionTypeGuid", "legacy-guid"),
                    ("missionTypeClassFullName", "Legacy.Runtime.Mission"),
                    ("resetParamOnCompleted", enabled),
                    ("conditions.Array.data[0].paramName", "amount")
                });
            }
            else if (module == "Equipment")
            {
                rowFields.AddRange(new (string, object)[]
                {
                    ("equipmentName", "legacy-name"),
                    ("equipmentClassFullName", "Legacy.Runtime.Equipment"),
                    ("equipmentClassGuid", "legacy-guid")
                });
            }
            CurrentAssetValues(new SerializedObject(root), rootFields, true);
            CurrentAssetValues(new SerializedObject(row), rowFields, true);
            if (effect)
            {
                CurrentAssetValues(new SerializedObject(effect), effectFields, true);
                EditorUtility.SetDirty(effect);
            }
            var icon = new SerializedObject(row);
            icon.FindProperty("icon.projectName").stringValue = "legacy-project";
            icon.FindProperty("icon.iconName").stringValue = "icons/legacy";
            icon.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(root);
            EditorUtility.SetDirty(row);
            AssetDatabase.SaveAssetIfDirty(root);
            AssetDatabase.SaveAssetIfDirty(row);
            string rootGuid = AssetDatabase.AssetPathToGUID(rootPath);
            string rowGuid = AssetDatabase.AssetPathToGUID(rowPath);
            var rootMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(rootPath + ".meta"));
            var rowMeta = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(rowPath + ".meta"));
            Resources.UnloadAsset(row);
            Resources.UnloadAsset(root);
            AssetDatabase.ImportAsset(rootPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(rowPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            root = (ScriptableObject)AssetDatabase.LoadAssetAtPath(rootPath, types.Item1);
            row = (ScriptableObject)AssetDatabase.LoadAssetAtPath(rowPath, types.Item2);
            CurrentAssetValues(new SerializedObject(root), rootFields, false);
            CurrentAssetValues(new SerializedObject(row), rowFields, false);
            if (module == "Buff")
            {
                var restoredEffect = AssetDatabase.LoadAllAssetsAtPath(rowPath).OfType<EffectData>().Single();
                CurrentAssetValues(new SerializedObject(restoredEffect), effectFields, false);
                Assert.That(((BuffConfigData)row).effects.Single(), Is.SameAs(restoredEffect));
            }
            Assert.That(types.Item2.GetField("parent").GetValue(row), Is.SameAs(root));
            Assert.That(((IList)types.Item1.GetField(types.Item3, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(root))[0], Is.SameAs(row));
            Assert.That(new SerializedObject(row).FindProperty("icon.iconName").stringValue, Is.EqualTo("icons/legacy"));
            Assert.That(AssetDatabase.AssetPathToGUID(rootPath), Is.EqualTo(rootGuid));
            Assert.That(AssetDatabase.AssetPathToGUID(rowPath), Is.EqualTo(rowGuid));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(rootPath + ".meta")), Is.EqualTo(rootMeta));
            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(rowPath + ".meta")), Is.EqualTo(rowMeta));
            EditorUtility.SetDirty(root);
            EditorUtility.SetDirty(row);
            AssetDatabase.SaveAssetIfDirty(root);
            AssetDatabase.SaveAssetIfDirty(row);
            AssetDatabase.ImportAsset(rootPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(rowPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            CurrentAssetValues(new SerializedObject(AssetDatabase.LoadAssetAtPath(rootPath, types.Item1)), rootFields, false);
            CurrentAssetValues(new SerializedObject(AssetDatabase.LoadAssetAtPath(rowPath, types.Item2)), rowFields, false);
        }

        [TestCase(typeof(SkillsConfig))]
        [TestCase(typeof(SkillConfigData))]
        [TestCase(typeof(BuffsConfig))]
        [TestCase(typeof(BuffConfigData))]
        [TestCase(typeof(EffectData))]
        [TestCase(typeof(MissionsConfig))]
        [TestCase(typeof(MissionConfigData))]
        [TestCase(typeof(MissionCondition))]
        [TestCase(typeof(MissionParameter))]
        [TestCase(typeof(YLFramework.Equipment.EquipmentsConfig))]
        [TestCase(typeof(YLFramework.Equipment.EquipmentConfigData))]
        public void CurrentDomainFieldsHaveNoRetiredUnityReader(Type type)
        {
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(field => field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>()), Is.Empty);
        }

        private static void CurrentAssetValues(SerializedObject serialized,
            IEnumerable<(string Current, object Value)> fields, bool prepare)
        {
            foreach (var field in fields)
            {
                var property = serialized.FindProperty(field.Current);
                Assert.That(property, Is.Not.Null, "Current field missing: " + field.Current);
                if (field.Value == null)
                {
                    Assert.That(property.arraySize, Is.EqualTo(1));
                }
                else if (field.Value is bool boolean)
                {
                    if (prepare)
                    {
                        property.boolValue = boolean;
                    }
                    else
                    {
                        Assert.That(property.boolValue, Is.EqualTo(boolean), field.Current);
                    }
                }
                else if (field.Value is int integer)
                {
                    if (prepare)
                    {
                        property.intValue = integer;
                    }
                    else
                    {
                        Assert.That(property.intValue, Is.EqualTo(integer), field.Current);
                    }
                }
                else if (field.Value is float number)
                {
                    if (prepare)
                    {
                        property.floatValue = number;
                    }
                    else
                    {
                        Assert.That(property.floatValue, Is.EqualTo(number), field.Current);
                    }
                }
                else if (field.Value is string text)
                {
                    if (prepare)
                    {
                        property.stringValue = text;
                    }
                    else
                    {
                        Assert.That(property.stringValue, Is.EqualTo(text), field.Current);
                    }
                }
                else
                {
                    var array = (Array)field.Value;
                    if (prepare)
                    {
                        property.arraySize = array.Length;
                    }

                    Assert.That(property.arraySize, Is.EqualTo(array.Length));
                    for (int i = 0; i < array.Length; i++)
                    {
                        var element = property.GetArrayElementAtIndex(i);
                        if (array.GetValue(i) is int item)
                        {
                            if (prepare)
                            {
                                element.intValue = item;
                            }
                            else
                            {
                                Assert.That(element.intValue, Is.EqualTo(item), field.Current);
                            }
                        }
                        else
                        {
                            if (prepare)
                            {
                                element.stringValue = (string)array.GetValue(i);
                            }
                            else
                            {
                                Assert.That(element.stringValue, Is.EqualTo(array.GetValue(i)), field.Current);
                            }
                        }
                    }
                }
            }

            if (prepare)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        [TestCase("Skill")]
        [TestCase("Buff")]
        [TestCase("Mission")]
        public void RootDeletionUsesItsOwnGeneratedDirectoryAndPreservesExternalAssets(string module)
        {
            var external = ScriptableObject.CreateInstance<EffectData>();
            AssetDatabase.CreateAsset(external, _folder + "/external.asset");
            ScriptableObject root = module == "Skill" ? ScriptableObject.CreateInstance<SkillsConfig>() : module == "Buff" ? ScriptableObject.CreateInstance<BuffsConfig>() : ScriptableObject.CreateInstance<MissionsConfig>();
            var rootPath = _folder + "/root.asset";
            AssetDatabase.CreateAsset(root, rootPath);
            string generated;
            if (root is SkillsConfig skill)
            {
                SkillsConfigController.CreateSkillConfigData(typeof(SkillConfigData), skill);
                generated = SkillsConfigController.GetSkillConfigDataPath(skill);
            }
            else if (root is BuffsConfig buff)
            {
                BuffsConfigController.CreateBuffConfigData(typeof(BuffConfigData), buff);
                generated = BuffsConfigController.GetBuffConfigDataPath(buff);
                buff.buffs[0].effects.Add(external);
            }
            else
            {
                var mission = (MissionsConfig)root;
                MissionsConfigController.CreateMissionConfigData(typeof(MissionConfigData), mission);
                generated = "Assets/YLFrameworkData/Mission/" + AssetDatabase.AssetPathToGUID(rootPath);
            }
            Assert.That(AssetDatabase.IsValidFolder(generated), Is.True);
            Assert.That(AssetDatabase.DeleteAsset(rootPath), Is.True);
            Assert.That(AssetDatabase.IsValidFolder(generated), Is.False);
            Assert.That(AssetDatabase.LoadAssetAtPath<EffectData>(_folder + "/external.asset"), Is.SameAs(external));
        }

        [TestCase("Skill")]
        [TestCase("Buff")]
        [TestCase("Mission")]
        public void AuthorTreeCopiesReordersUndoRedoesAndReopensCurrentDataBindings(string module)
        {
            var rootType = module == "Skill" ? typeof(SkillsConfig) : module == "Buff" ? typeof(BuffsConfig) : typeof(MissionsConfig);
            var dataType = module == "Skill" ? typeof(SkillConfigData) : module == "Buff" ? typeof(BuffConfigData) : typeof(MissionConfigData);
            var root = ScriptableObject.CreateInstance(rootType);
            string rootPath = _folder + "/" + module + ".asset";
            AssetDatabase.CreateAsset(root, rootPath);
            var listField = rootType.GetField(module == "Skill" ? "skills" : module == "Buff" ? "buffs" : "missions");
            IList Values() => (IList)listField.GetValue(root);
            var id = dataType.GetField("id");
            var controller = typeof(SkillsConfigController).Assembly.GetType("YLFramework.Editor." + module + "." +
                (module == "Skill" ? "Skills" : module == "Buff" ? "Buffs" : "Missions") + "ConfigController");
            controller.GetMethod("Create" + module + "ConfigData", new[] { typeof(Type), rootType }).Invoke(null, new object[] { dataType, root });
            var first = (ScriptableObject)Values()[0];
            string binding = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(first)));
            rootType.GetField("typeScriptGuid").SetValue(root, binding);
            var author = controller.Assembly.GetType("YLFramework.Editor." + module + "." + module + "Authoring");
            Assert.That(author.GetMethod("GetDataType", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { root }), Is.EqualTo(dataType));
            var icon = dataType.GetField("icon").GetValue(first);
            icon.GetType().GetField("projectName").SetValue(icon, "author-fixture");
            icon.GetType().GetField("iconName").SetValue(icon, "selected-icon");
            if (first is MissionConfigData mission)
            {
                ((MissionsConfig)root).parameters.Add(new MissionParameter { name = "amount", type = MissionParamType.Float });
                mission.conditions.Add(new MissionCondition { paramName = "amount", compareType = CompareType.GreaterEqual, target = 3 });
                mission.rewards.Add(new RewardInfo { id = 2, count = 4 });
            }
            if (first is BuffConfigData buff)
            {
                var effect = ScriptableObject.CreateInstance<EffectData>();
                effect.id = 8;
                AssetDatabase.CreateAsset(effect, _folder + "/effect.asset");
                buff.effects.Add(effect);
            }
            var treeType = controller.Assembly.GetType("YLFramework.Editor." + module + "." + module + "ListTree");
            var tree = (TreeView)Activator.CreateInstance(treeType, new UnityEditor.IMGUI.Controls.TreeViewState(), root);
            var previousFocus = EditorWindow.focusedWindow;
            var window = ScriptableObject.CreateInstance<YLFramework.Tests.Extensions.Shared.E0ReorderableTestWindow>();
            window.Tree = tree;
            window.ShowUtility();
            int copiedId;
            try
            {
                copiedId = (int)treeType.GetMethod("Copy" + module, BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(tree, new[] { module == "Skill" ? (object)(int)id.GetValue(first) : first });
                Assert.That(Values().Count, Is.EqualTo(2));
                tree.Reload();
                var basis = treeType.BaseType;
                // 调用实际TreeView准备/放置路径，不冒称物理鼠标拖拽。
                window.Dispatch(EventType.MouseDrag, () => Assert.That(basis.GetMethod("PrepareDrag", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(tree, new object[] { new[] { (int)id.GetValue(first) } }), Is.True));
                var drop = basis.GetMethod("HandleDragAndDrop", BindingFlags.NonPublic | BindingFlags.Instance);
                var args = Activator.CreateInstance(drop.GetParameters()[0].ParameterType);
                void Set(string name, object value)
                {
                    var type = args.GetType();
                    var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property != null)
                    {
                        property.SetValue(args, value);
                    }
                    else
                    {
                        type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(args, value);
                    }
                }
                Set("parentItem", typeof(TreeView).GetProperty("rootItem", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tree));
                Set("insertAtIndex", 2);
                Set("performDrop", true);
                window.Dispatch(EventType.DragPerform, () =>
                {
                    var key = (string)basis.GetField("_dragKey", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    var drag = DragAndDrop.GetGenericData(key);
                    Assert.That(drop.Invoke(tree, new[] { args }), Is.EqualTo(DragAndDropVisualMode.Move),
                        $"Drag payload={drag != null}; refs={DragAndDrop.objectReferences.Length}; paths={DragAndDrop.paths?.Length ?? 0}");
                });
                Assert.That((int)id.GetValue(Values()[0]), Is.EqualTo(copiedId));
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(Values()[0], Is.SameAs(first));
                Undo.PerformRedo();
                Assert.That((int)id.GetValue(Values()[0]), Is.EqualTo(copiedId));
                treeType.GetMethod("Delete" + module, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(tree, new object[] { (int)id.GetValue(first) });
                Assert.That(Values().Count, Is.EqualTo(1));
                var copy = (ScriptableObject)Values()[0];
                EditorUtility.SetDirty(copy);
                EditorUtility.SetDirty(root);
                AssetDatabase.SaveAssetIfDirty(copy);
                AssetDatabase.SaveAssetIfDirty(root);
            }
            finally
            {
                window.Close();
                Undo.ClearUndo(root);
                if (previousFocus)
                {
                    previousFocus.Focus();
                }
            }
            AssetDatabase.ImportAsset(rootPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            root = (ScriptableObject)AssetDatabase.LoadAssetAtPath(rootPath, rootType);
            var reopened = (ScriptableObject)Values()[0];
            Assert.That((int)id.GetValue(reopened), Is.EqualTo(copiedId));
            Assert.That(dataType.GetField("parent").GetValue(reopened), Is.SameAs(root));
            Assert.That(rootType.GetField("typeScriptGuid").GetValue(root), Is.EqualTo(binding));
            Assert.That(icon.GetType().GetField("iconName").GetValue(dataType.GetField("icon").GetValue(reopened)), Is.EqualTo("selected-icon"));
            if (reopened is MissionConfigData restoredMission)
            {
                Assert.That(restoredMission.conditions.Single().paramName, Is.EqualTo(((MissionsConfig)root).parameters.Single().name));
                Assert.That(restoredMission.rewards.Single().count, Is.EqualTo(4));
            }
            if (reopened is BuffConfigData restoredBuff)
            {
                Assert.That(restoredBuff.effects.Single().id, Is.EqualTo(8));
            }
        }

        [Test]
        public void RootConfigsDoNotStoreWorkbookPaths()
        {
            Assert.That(typeof(SkillsConfig).GetField("excel_path"), Is.Null);
            Assert.That(typeof(BuffsConfig).GetField("excel_path"), Is.Null);
            Assert.That(typeof(MissionsConfig).GetField("excel_path"), Is.Null);
            var root = ScriptableObject.CreateInstance<MissionsConfig>();
            AssetDatabase.CreateAsset(root, _folder + "/missions.asset");
            Assert.That(EditorJsonUtility.ToJson(root), Does.Not.Contain("excel_path"));
        }

        [Test]
        public void MissionParameterRenameUpdatesOnlyItsConditionsAndCanUndo()
        {
            var root = ScriptableObject.CreateInstance<MissionsConfig>();
            var data = ScriptableObject.CreateInstance<MissionConfigData>();
            AssetDatabase.CreateAsset(root, _folder + "/missions.asset");
            AssetDatabase.AddObjectToAsset(data, root);
            root.parameters.Add(new MissionParameter { name = "old" });
            root.missions.Add(data);
            data.conditions.Add(new MissionCondition { paramName = "old" });
            AssetDatabase.SaveAssetIfDirty(root);
            var tree = new ParamListTree(new UnityEditor.IMGUI.Controls.TreeViewState(), root);
            tree.Reload();
            try
            {
                tree.RenameParam("old", "new");
                Assert.That(data.conditions[0].paramName, Is.EqualTo("new"));
                Assert.That(root.parameters[0].name, Is.EqualTo("new"));
                Undo.PerformUndo();
                Assert.That(root.parameters[0].name, Is.EqualTo("old"));
                Assert.That(data.conditions[0].paramName, Is.EqualTo("old"));
            }
            finally
            {
                tree.Dispose();
            }
        }
    }

    internal static class DomainTestAssets
    {
        internal static string[] CaptureAbsentParents() => new[]
        {
            "Assets/YLFrameworkData/Skill/Skills", "Assets/YLFrameworkData/Buff/Buffs", "Assets/YLFrameworkData/Skill",
            "Assets/YLFrameworkData/Buff", "Assets/YLFrameworkData/Mission", "Assets/YLFrameworkData"
        }.Where(path => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))).ToArray();

        internal static void DeleteFolder(string folder, string[] absentParents)
        {
            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                Console.WriteLine("Preserved failed domain fixture: " + folder);
                return;
            }

            if (AssetDatabase.IsValidFolder(folder))
            {
                // Unity删除父目录不会逐个调用子资产的OnWillDeleteAsset，先删除自己的配表根。
                foreach (var guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { folder }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var root = AssetDatabase.LoadMainAssetAtPath(path);
                    if (root is SkillsConfig || root is BuffsConfig || root is MissionsConfig)
                    {
                        AssetDatabase.DeleteAsset(path);
                    }
                }
                AssetDatabase.DeleteAsset(folder);
            }

            foreach (var path in absentParents)
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Length == 0)
                {
                    Assert.That(AssetDatabase.DeleteAsset(path), Is.True, path);
                }
            }
        }
    }
}
