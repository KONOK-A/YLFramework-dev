using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Tests
{
    public class FrameworkStructureTests
    {
        private const string PackageRoot = "Packages/com.kano.ylframework/";

        [Serializable]
        private sealed class Move
        {
            public string From;
            public string To;
            public string Guid;
        }

        [Serializable]
        private sealed class MoveList
        {
            public Move[] Moves;
        }

        [Test]
        public void ProductLayoutPreservesEveryRecordedAssetIdentity()
        {
            var manifest = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Tests/YLFramework/Fixtures/NamespaceMigration/ProductMoves.json");
            Assert.That(manifest, Is.Not.Null);
            var moves = JsonUtility.FromJson<MoveList>(manifest.text).Moves;
            Assert.That(moves.Length, Is.EqualTo(28));
            foreach (var move in moves)
            {
                string current = CurrentPath(move.To);
                string package = current.Split('/')[1];
                if (UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(info => info.name == package))
                {
                    Assert.That(AssetDatabase.AssetPathToGUID(current), Is.EqualTo(move.Guid), move.To);
                    Assert.That(AssetDatabase.GUIDToAssetPath(move.Guid), Is.EqualTo(current), move.Guid);
                }
                else
                {
                    Assert.That(AssetDatabase.GUIDToAssetPath(move.Guid), Is.Empty, "Absent optional assets must not remain in the main package.");
                }
                // From records the historical Assets layout, not a path in the installed package.
                var oldPath = DevelopmentHostBoundaryTests.SourcePath("Assets/YLFramework/" + move.From);
                Assert.That(File.Exists(oldPath) || Directory.Exists(oldPath), Is.False, move.From);
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

        private static string CurrentPath(string path)
        {
            foreach (var domain in new[] { "Skill", "Buff", "Mission", "Equipment", "Shared" })
            {
                if (path.StartsWith("Editor/Systems/" + domain + "/", StringComparison.Ordinal))
                    return "Packages/com.kano.ylframework.gameplay/Editor/" + path.Substring("Editor/Systems/".Length);
            }
            if (path.StartsWith("Editor/Systems/StateMachine/", StringComparison.Ordinal))
                return "Packages/com.kano.ylframework.state-machine/" + path;
            if (path == "Runtime/Utilities/CharacterController2D" || path == "Runtime/Utilities/MoveablePlatform" ||
                path == "Editor/Utilities/CharacterController2D" || path == "Editor/Utilities/MoveablePlatform")
                return "Packages/com.kano.ylframework.movement2d/" + path;
            return PackageRoot + path;
        }
    }
}
