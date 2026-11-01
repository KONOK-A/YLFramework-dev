using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using YLFramework.StateMachine;
using YLFramework.Editor.StateMachine;

namespace YLFramework.Tests.Extensions.Behavior
{
    [Category("YL.Module.Behavior")]
    public class AssetFsmOfflineTests
    {
        private static string Root => Environment.GetEnvironmentVariable("YL_BEHAVIOR_ROOT") ?? Directory.GetCurrentDirectory();
        private static Type Model(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.StateMachine." + name)).FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, name + " has not been integrated.");
            return type;
        }

        [Test]
        public void RuntimeHasNoEditorOrOriginalFrameworkDependency()
        {
            var path = Path.Combine(Root, "Assets/YLFramework/Runtime/Systems/StateMachine");
            Assert.That(Directory.Exists(path), Is.True);
            foreach (var file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(file);
                Assert.That(source.Contains("using UnityEditor") || source.Contains("UnityEditor."), Is.False, file);
                Assert.That(source, Does.Contain("namespace YLFramework.StateMachine"), file);
                foreach (Match import in Regex.Matches(source, @"(?m)^using\s+([^;]+);"))
                {
                    CollectionAssert.Contains(new[] { "System", "System.Collections", "System.Collections.Generic",
                        "System.Text", "UnityEngine", "UnityEngine.Serialization" }, import.Groups[1].Value, file);
                }
                Assert.That(source.Contains("YLFramework.Utilities.FSM"), Is.False, "The two FSMs must not share a new execution kernel.");
            }
        }

        [Test]
        public void GraphUsesPublicManipulatorsAndNoGraphsInternalApi()
        {
            var path = Path.Combine(Root, "Assets/YLFramework/Editor/Systems/StateMachine");
            Assert.That(Directory.Exists(path), Is.True);
            var graph = File.ReadAllText(Path.Combine(path, "GUI/WindowGraphView/FsmStateGraphView.cs"));
            Assert.That(graph.Contains("new SelectionDragger()"), Is.True);
            Assert.That(graph.Contains("new RectangleSelector()"), Is.True);
            Assert.That(graph.Contains("new GridBackground()"), Is.True);
            Assert.That(Directory.GetFiles(path, "FSMSelectionDragger.cs", SearchOption.AllDirectories), Is.Empty);
            foreach (var source in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText))
            {
                Assert.That(source.Contains("UnityEditor.Graphs") || source.Contains("Packages/"), Is.False);
                Assert.That(source, Does.Contain("namespace YLFramework.Editor.StateMachine"));
            }
        }

        [Test]
        public void LayerDrawerDoesNotConsumeAnotherCallersDisabledScope()
        {
            var source = File.ReadAllText(Path.Combine(Root, "Assets/YLFramework/Editor/Systems/StateMachine/GUI/CustomPropertyDrawer/CustomRuntimeFsmControllerLayer.cs"));
            Assert.That(Regex.Matches(source, "BeginDisabledGroup\\(").Count, Is.EqualTo(Regex.Matches(source, "EndDisabledGroup\\(").Count));
        }

        [Test]
        public void ReorderUsesSerializedOwnershipAndConditionErrorsDoNotEraseEdges()
        {
            var source = File.ReadAllText(Path.Combine(Root, "Assets/YLFramework/Editor/Systems/StateMachine/GUI/Inspectors/FsmTransitionInspector.cs"));
            Assert.That(source.Contains("new ReorderableList(_controllerObject,"), Is.True);
            source = File.ReadAllText(Path.Combine(Root, "Assets/YLFramework/Runtime/Systems/StateMachine/Scriptable/RuntimeFsmController.cs"));
            Assert.That(source.Contains("if (FsmConfigurationValidator.Validate(states, parameters, transitions) != null) return null"), Is.False);
        }

        [Test]
        public void FinalPlayerEntryExplicitlySupportsNonDevelopmentBackends()
        {
            var source = File.ReadAllText(Path.Combine(Root, "Assets/YLFramework/Tests/Fixtures/Extensions/Behavior/Editor/BehaviorAcceptanceBuilder.cs"));
            Assert.That(source.Contains("BuildOptions.None"), Is.True);
            Assert.That(source.Contains("Build Release Mono Windows64") && source.Contains("Build Release IL2CPP Windows64"), Is.True);
        }

        [Test]
        public void ParameterChangeCommitsBeforeNotificationAndDoesNotNotifyEqualValue()
        {
            var type = Model(nameof(FsmParameterData));
            var data = Activator.CreateInstance(type);
            var value = type.GetProperty("Value");
            int notifications = 0;
            type.GetField("ValueChanged").SetValue(data, (Action)(() =>
            {
                Assert.That(value.GetValue(data), Is.EqualTo(3f));
                notifications++;
            }));
            value.SetValue(data, 3f);
            value.SetValue(data, 3f);
            Assert.That(notifications, Is.EqualTo(1));
        }

        [Test]
        public void EachScriptRetainsItsOwnCreateFlagAcrossInterruptedNodeEntry()
        {
            var type = Model("FsmCreationTracker");
            var tracker = Activator.CreateInstance(type, new object[] { 2 });
            var mark = type.GetMethod("TryMarkCreated");
            Assert.That(mark.Invoke(tracker, new object[] { 0 }), Is.EqualTo(true));
            // Entry interrupted after script 0. Script 1 has never been reached.
            Assert.That(mark.Invoke(tracker, new object[] { 0 }), Is.EqualTo(false));
            Assert.That(mark.Invoke(tracker, new object[] { 1 }), Is.EqualTo(true));
            Assert.That(mark.Invoke(tracker, new object[] { 1 }), Is.EqualTo(false));
        }

        [Test]
        public void NewScriptCreationRejectsOccupiedPathWithoutChangingExistingBytes()
        {
            string directory = Path.Combine(Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT") ?? Path.GetTempPath(),
                "WriterTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "Occupied.cs");
                File.WriteAllText(path, "// Existing filename/type mismatch must not authorize an overwrite.\nclass DifferentType {}\n");
                byte[] before = File.ReadAllBytes(path);
                var writer = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YLFramework.Editor.StateMachine.FsmStateFileWriter"))
                    .Single(type => type != null);
                var method = writer.GetMethod("TryWriteNew");
                var args = new object[] { path, "class Replacement {}", null };
                Assert.That(method.Invoke(null, args), Is.EqualTo(false));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
                Assert.That((string)args[2], Does.Contain("Occupied.cs"));
                string fresh = Path.Combine(directory, "Fresh.cs");
                Assert.That(method.Invoke(null, new object[] { fresh, "class Fresh {}", null }), Is.EqualTo(true));
                Assert.That(File.ReadAllText(fresh), Is.EqualTo("class Fresh {}"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestCase("EqualCompare", 2f, 2f, true)]
        [TestCase("NotEqualCompare", 2f, 3f, true)]
        [TestCase("GreaterCompare", 2f, 3f, false)]
        [TestCase("LessCompare", 2f, 3f, true)]
        public void OriginalComparatorsEvaluateRealDataAndRejectMissingParameter(string comparer, float value, float target, bool expected)
        {
            var parameter = Activator.CreateInstance(Model(nameof(FsmParameterData)));
            parameter.GetType().GetProperty("Value").SetValue(parameter, value);
            var compare = Activator.CreateInstance(Model(comparer));
            var method = compare.GetType().GetMethod(nameof(IParameterComparer.Matches));
            Assert.That(method.Invoke(compare, new[] { parameter, (object)target }), Is.EqualTo(expected));
            Assert.That(method.Invoke(compare, new object[] { null, target }), Is.EqualTo(false));
        }

        [Test]
        public void BaseParentCopyIsNullSafeAndDoesNotAliasHierarchy()
        {
            var type = Model(nameof(FsmStateNodeData));
            var node = Activator.CreateInstance(type);
            Assert.That((IList)type.GetMethod("GetParent").Invoke(node, null), Is.Empty);
            type.GetField("parents").SetValue(node, new List<string> { "Root", "Child" });
            var copy = (List<string>)type.GetMethod("GetParent").Invoke(node, null);
            copy.Clear();
            Assert.That(type.GetProperty("ParentPath").GetValue(node), Is.EqualTo("Root/Child"));
            Assert.That(type.GetProperty("Parent").GetValue(node), Is.EqualTo("Child"));
        }

        [Test]
        public void NewNodeBuiltInFlagsAreSafeBeforeNamingAndParentAssignment()
        {
            var node = Activator.CreateInstance(Model(nameof(FsmStateNodeData)));
            foreach (var name in new[] { "IsAnyState", "IsEntryState", "IsUpState" })
            {
                Assert.That(node.GetType().GetProperty(name).GetValue(node), Is.EqualTo(false));
            }
        }

        [TestCase("Any State", "IsAnyState")]
        [TestCase("Entry", "IsEntryState")]
        public void BuiltInIdentityRequiresExplicitMetadataInsteadOfReservedName(string builtInName, string propertyName)
        {
            var node = new FsmStateNodeData { name = builtInName };
            var property = typeof(FsmStateNodeData).GetProperty(propertyName);
            Assert.That(property.GetValue(node), Is.False, "A reserved name alone is not a current-format built-in identity.");
            node.isBuiltInState = true;
            Assert.That(property.GetValue(node), Is.False, "The built-in kind is also required.");
            node.isBuiltInState = false;
            node.builtInStateName = builtInName;
            Assert.That(property.GetValue(node), Is.False, "The explicit built-in flag is required.");
            node.isBuiltInState = true;
            node.name = "DifferentDisplayName";
            Assert.That(property.GetValue(node), Is.True);
            Assert.That(node.DisplayName, Is.EqualTo(builtInName));
        }

        [Test]
        public void TriggerQueueConsumesOnceAndResetOrCloseDiscardPendingTokens()
        {
            var type = Model("FsmTriggerQueue");
            var queue = Activator.CreateInstance(type, true);
            type.GetMethod("Enqueue").Invoke(queue, new object[] { 42 });
            type.GetMethod("Enqueue").Invoke(queue, new object[] { 42 });
            Assert.That(type.GetMethod("TryTake").Invoke(queue, new object[] { 42, false }), Is.EqualTo(false));
            Assert.That(type.GetMethod("TryTake").Invoke(queue, new object[] { 42, true }), Is.EqualTo(true));
            Assert.That(type.GetMethod("GetCount").Invoke(queue, new object[] { 42 }), Is.EqualTo(1));
            type.GetMethod("Reset").Invoke(queue, new object[] { 42 });
            Assert.That(type.GetMethod("TryTake").Invoke(queue, new object[] { 42, true }), Is.EqualTo(false));
            type.GetMethod("Enqueue").Invoke(queue, new object[] { 42 });
            type.GetMethod("Clear").Invoke(queue, null);
            Assert.That(type.GetMethod("GetCount").Invoke(queue, new object[] { 42 }), Is.EqualTo(0));
        }

        [Test]
        public void TransitionCycleGuardIsFrameBasedAndRecoversWithoutScaledTime()
        {
            var type = Model("FsmTransitionBudget");
            var budget = Activator.CreateInstance(type, true);
            var enter = type.GetMethod("TryEnter");
            for (int i = 0; i < 32; i++)
            {
                Assert.That(enter.Invoke(budget, new object[] { 7, "Loop" }), Is.EqualTo(true));
            }

            Assert.That(enter.Invoke(budget, new object[] { 7, "Loop" }), Is.EqualTo(false));
            Assert.That(enter.Invoke(budget, new object[] { 7, "Other" }), Is.EqualTo(false));
            Assert.That(enter.Invoke(budget, new object[] { 8, "Loop" }), Is.EqualTo(true));
        }

        private static string Validate(IList<FsmStateNodeData> states, IList<FsmParameterData> parameters, IList<FsmTransitionData> transitions)
            => (string)Model("FsmConfigurationValidator").GetMethod("Validate").Invoke(null, new object[] { states, parameters, transitions });

        [Test]
        public void ValidHierarchyAndTransitionSchemaIsAccepted()
        {
            var states = new[] { new FsmStateNodeData { name = "Root", isDefaultState = true, isSubStateMachine = true }, new FsmStateNodeData { name = "Child", isDefaultState = true, parents = new List<string> { "Root" } } };
            Assert.That(Validate(states, new FsmParameterData[0], new FsmTransitionData[0]), Is.Null);
        }

        [Test]
        public void BrokenParentReferenceIsDiagnosedWithStateAndField()
        {
            var states = new[] { new FsmStateNodeData { name = "Idle", isDefaultState = true }, new FsmStateNodeData { name = "Walk", parents = new List<string> { "Deleted" } } };
            var error = Validate(states, new FsmParameterData[0], new FsmTransitionData[0]);
            Assert.That(error, Does.Contain("Walk").And.Contain("parents").And.Contain("Deleted"));
        }

        [Test]
        public void MissingTransitionParameterIsDiagnosedBeforeStateConstruction()
        {
            var states = new[] { new FsmStateNodeData { name = "Idle", isDefaultState = true }, new FsmStateNodeData { name = "Walk" } };
            var transition = new FsmTransitionData { fromStateName = "Idle", toStateName = "Walk" };
            transition.conditions.Add(new FsmConditionData { parameterName = "Deleted" });
            var error = Validate(states, new FsmParameterData[0], new[] { transition });
            Assert.That(error, Does.Contain("Idle:Walk").And.Contain("Deleted"));
        }

        [Test]
        public void DuplicateDefaultsAndCyclicParentsAreRejected()
        {
            var states = new[] { new FsmStateNodeData { name = "A", isDefaultState = true }, new FsmStateNodeData { name = "B", isDefaultState = true } };
            Assert.That(Validate(states, new FsmParameterData[0], new FsmTransitionData[0]), Does.Contain("default"));
            states[1].isDefaultState = false;
            states[1].parents = new List<string> { "B" };
            Assert.That(Validate(states, new FsmParameterData[0], new FsmTransitionData[0]), Does.Contain("parents"));
        }
    }
}
