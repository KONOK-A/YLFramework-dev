using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Tests.Extensions.Behavior
{
    [Category("YL.Module.Behavior")]
    public class BehaviorAssemblyMigrationTests
    {
        private const string Root = "Assets/Tests/YLFramework/Fixtures/NamespaceMigration/AssemblySplitV1/";

        [Serializable]
        private sealed class Fixture
        {
            public Asset[] assets;
        }

        [Serializable]
        private sealed class Asset
        {
            public string path;
            public string typeFullName;
            public string scriptGuid;
            public string guid;
            public long localId;
            public Component[] components;
        }

        [Serializable]
        private sealed class Component
        {
            public string typeFullName;
            public string scriptGuid;
        }

        [Test]
        public void FrozenBehaviorAssetsReopenWithEveryOriginalScriptGuidLocalIdAndComponent()
        {
            var capture = JsonUtility.FromJson<Fixture>(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Root + "FixtureIdentity.json")));
            var assets = capture.assets.Where(item => item.path.StartsWith("Content/Behavior/", StringComparison.Ordinal)).ToArray();
            Assert.That(assets, Has.Length.EqualTo(3));
            foreach (var expected in assets)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(Root + expected.path);
                Assert.That(asset, Is.Not.Null, expected.path);
                Assert.That(asset.GetType().FullName, Is.EqualTo(expected.typeFullName));
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId), Is.True);
                Assert.That(guid, Is.EqualTo(expected.guid));
                Assert.That(localId, Is.EqualTo(expected.localId));
                if (asset is ScriptableObject data)
                {
                    Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(data))), Is.EqualTo(expected.scriptGuid));
                }
                if (asset is GameObject prefab)
                {
                    var components = prefab.GetComponentsInChildren<MonoBehaviour>(true);
                    Assert.That(components, Has.Length.EqualTo(expected.components.Length));
                    foreach (var component in components)
                    {
                        Assert.That(component, Is.Not.Null, "An original serialized public component became a missing script.");
                        string script = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(component)));
                        Assert.That(expected.components.Any(item => item.typeFullName == component.GetType().FullName && item.scriptGuid == script), Is.True);
                    }
                }
            }
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeFsmController>(Root + "Content/Behavior/StateMachine.asset");
            Assert.That(controller.GetType().Assembly.GetName().Name, Is.EqualTo("YLFramework.StateMachine"));
            Assert.That(controller.layers, Is.EqualTo(new[] { "BeforeAssembly" }));
            Assert.That(controller.states.Select(state => state.name), Is.EqualTo(new[] { "Idle", "Run" }));
            Assert.That(controller.states[0].isDefaultState, Is.True);
            Assert.That(controller.parameters.Single().name, Is.EqualTo("Go"));
            var transition = controller.transitions.Single();
            Assert.That(transition.fromStateName, Is.EqualTo("Idle"));
            Assert.That(transition.toStateName, Is.EqualTo("Run"));
            Assert.That(transition.autoSwitch, Is.True);
            Assert.That(transition.groupConditions.Single().conditions.Single().parameterName, Is.EqualTo("Go"));
            Assert.That(transition.groupConditions.Single().conditions.Single().targetValue, Is.EqualTo(1f));
        }

        [Test]
        public void FinalPlayerEntryExplicitlySupportsNonDevelopmentBackends()
        {
            string repository = Environment.GetEnvironmentVariable("YL_BEHAVIOR_ROOT") ?? Directory.GetCurrentDirectory();
            var source = File.ReadAllText(Path.Combine(repository, "Assets/Tests/YLFramework/Fixtures/Extensions/Behavior/Editor/BehaviorAcceptanceBuilder.cs"));
            Assert.That(source.Contains("BuildOptions.None"), Is.True);
            Assert.That(source.Contains("Build Release Mono Windows64") && source.Contains("Build Release IL2CPP Windows64"), Is.True);
        }
    }
}
