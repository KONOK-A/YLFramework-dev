using System;
using System.IO;
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
                Assert.That(AssetDatabase.AssetPathToGUID(PackageRoot + move.To), Is.EqualTo(move.Guid), move.To);
                Assert.That(AssetDatabase.GUIDToAssetPath(move.Guid), Is.EqualTo(PackageRoot + move.To), move.Guid);
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
                "Runtime/Systems/Resource/Integration/Audio/ResourceAudioClipProvider.cs",
                "Editor/Systems/Shared/DomainSessionLifecycle.cs",
                "Editor/Systems/Shared/DomainResourceIconGui.cs"
            })
            {
                Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(PackageRoot + path), Is.Not.Null, path);
            }
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework/Runtime/Systems/Resource/Integration/Audio/ResourceAudioClipProvider.cs")),
                Does.Not.Contain("partial class ResourceSystem"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical("Packages/com.kano.ylframework/Editor/Systems/Skill/SkillAuthoring.cs")),
                Does.Not.Contain("class DomainResourceIconGui"));
        }
    }
}
