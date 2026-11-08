using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace YLFramework.Tests
{
    public class FrameworkStructureTests
    {
        private const string PackageRoot = "Packages/com.kano.ylframework/";

        [Test]
        public void InstalledPackagesOwnTheirCurrentScriptIdentitiesWithoutLeakingOptionalAssets()
        {
            var anchors = new[]
            {
                ("com.kano.ylframework", "Runtime/Foundation/Events/Subscriptions/EventBus.cs", "f1cd757ac67e81c4bbf1d8919cfc2058"),
                ("com.kano.ylframework", "Editor/Common/Settings/ProjectSettingsScriptableObject.cs", "5c5731fb2ee10ba46b4c302aeb0c3717"),
                ("com.kano.ylframework", "Runtime/Systems/Resource/Integration/Audio/ResourceAudioClipProvider.cs", "e1f75cb9b4f220144a028552d1a4624d"),
                ("com.kano.ylframework.state-machine", "Runtime/Systems/StateMachine/FsmController.cs", "669ecbf1eff5cdb4fb85896497a489b6"),
                ("com.kano.ylframework.movement2d", "Runtime/Utilities/CharacterController2D/CharacterController2D.cs", "c4e9c15020213574abd044e313014268"),
                ("com.kano.ylframework.movement2d", "Runtime/Utilities/MoveablePlatform/MoveablePlatform2D.cs", "63ea22a6f7494254789144999759e98e"),
                ("com.kano.ylframework.overlap", "Runtime/Detection/OverlapDetector2D.cs", "78540f8a7d3054b47a96a10cbefaae8b"),
                ("com.kano.ylframework.overlap", "Runtime/Detection/OverlapDetector3D.cs", "93df00acc5406744487711e68d2d09a6"),
                ("com.kano.ylframework.gameplay", "Runtime/Skill/SkillConfigData.cs", "1a3a29525ba46c2468186d1837b21fb5"),
                ("com.kano.ylframework.data-assets", "Runtime/Systems/DataAssets/ScriptableObjectConfigs.cs", "5e54610ded7e2994aaa66cefe28b02ce"),
                ("com.kano.ylframework.localization", "Runtime/Systems/Localization/LanguageConfig.cs", "30a56d547611e6f448ff727ea7957e29"),
                ("com.kano.ylframework.save", "Runtime/Systems/Save/SaveManager.cs", "ff5957d84032533449f21601f372570a"),
                ("com.kano.ylframework.reddot", "Runtime/Systems/Reddot/Reddot.cs", "3ff4191f5232945449bde5c92a1b497d"),
                ("com.kano.ylframework.excel", "Editor/ExcelValues.cs", "c12cc4e0c0435a9488f224355bc0d70e"),
                ("com.kano.ylframework.hybridclr", "Runtime/HybridCLRLoader.cs", "fa5277b79a27ae34196cc6edd0e514e8")
            };
            var installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(info => info.name).ToArray();
            Assert.That(installed, Does.Contain("com.kano.ylframework"));
            foreach (var anchor in anchors)
            {
                string current = "Packages/" + anchor.Item1 + "/" + anchor.Item2;
                if (installed.Contains(anchor.Item1))
                {
                    Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(current), Is.Not.Null, current);
                    Assert.That(AssetDatabase.AssetPathToGUID(current), Is.EqualTo(anchor.Item3), current);
                    Assert.That(AssetDatabase.GUIDToAssetPath(anchor.Item3), Is.EqualTo(current), anchor.Item3);
                }
                else
                {
                    Assert.That(AssetDatabase.GUIDToAssetPath(anchor.Item3), Is.Empty, "Absent optional assets must not remain in the main package.");
                }
            }
        }

        [Test]
        public void BridgeAndSharedAuthoringTypesHaveSeparateOwnedFiles()
        {
            foreach (var path in new[]
            {
                "Runtime/Systems/Resource/Integration/Audio/ResourceSystem.Audio.cs",
                "Runtime/Systems/Resource/Integration/Audio/ResourceAudioClipProvider.cs"
            })
            {
                Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(PackageRoot + path), Is.Not.Null, path);
            }
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework/Runtime/Systems/Resource/Integration/Audio/ResourceAudioClipProvider.cs")),
                Does.Not.Contain("partial class ResourceSystem"));
            if (UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(info => info.name == "com.kano.ylframework.gameplay"))
            {
                foreach (var path in new[] { "Editor/Shared/DomainSessionLifecycle.cs", "Editor/Shared/DomainResourceIconGui.cs" })
                {
                    Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>("Packages/com.kano.ylframework.gameplay/" + path), Is.Not.Null, path);
                }
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework.gameplay/Editor/Skill/SkillAuthoring.cs")),
                    Does.Not.Contain("class DomainResourceIconGui"));
            }
        }

    }
}
