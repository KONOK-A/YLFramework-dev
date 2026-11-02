using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

namespace YLFramework.Tests
{
    /// <summary>Source-repository build gates, not prerequisites imposed on a bare consuming project.</summary>
    [Category("YL.Author.SourceGate")]
    public class AnalyzerSourceDistributionTests
    {
        [Serializable]
        private sealed class AnalyzerBuildInput
        {
            public string Path;
            public string Sha256;
        }

        [Serializable]
        private sealed class AnalyzerBuildReceipt
        {
            public string DllSha256;
            public AnalyzerBuildInput[] Inputs;
        }

        [Test]
        public void DeployedAnalyzerMatchesTheLastVerifiedSourceBuild()
        {
            var root = Directory.GetParent(Application.dataPath).FullName;
            var receiptPath = Path.Combine(root, "Temp/YLFramework.Analyzers/build-info.json");
            Assert.That(File.Exists(receiptPath), Is.True, "Run the maintained Analyzer build with -Deploy before source delivery acceptance.");
            var receipt = JsonUtility.FromJson<AnalyzerBuildReceipt>(File.ReadAllText(receiptPath));
            var tools = Path.Combine(root, "Tools/YLFramework.Analyzers");
            var expectedInputs = new[]
            {
                "global.json", "Directory.Build.props", "build.ps1", "Analyzer/YLFramework.Analyzers.csproj",
                "Analyzer/packages.lock.json", "../../PackageSources/com.kano.ylframework/Editor/Core/Analyzers/AnalyzerRuleCatalog.cs"
            }.Concat(Directory.GetFiles(Path.Combine(tools, "Analyzer"), "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(tools, path).Replace('\\', '/'))).ToArray();
            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.Inputs, Is.Not.Null);
            Assert.That(receipt.Inputs.Select(input => input.Path), Is.EquivalentTo(expectedInputs), "Verified source input set changed.");
            var deployed = YLFramework.Editor.Common.FrameworkPackagePaths.FullPath(typeof(YLFrameworkRoot).Assembly,
                "Runtime/Core/Analyzers/YLFramework.Analyzers.dll");
            using var sha = SHA256.Create();
            Assert.That(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(deployed))).Replace("-", ""), Is.EqualTo(receipt.DllSha256));
            foreach (var input in receipt.Inputs)
            {
                var path = Path.GetFullPath(Path.Combine(tools, input.Path));
                Assert.That(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""), Is.EqualTo(input.Sha256), input.Path);
            }
        }

        [Test]
        public void SourceBuildGateRejectsAFileAddedAfterTheVerifiedBuild()
        {
            var root = Directory.GetParent(Application.dataPath).FullName;
            var probe = Path.Combine(root, "Tools/YLFramework.Analyzers/Analyzer/InputSetProbe-" + Guid.NewGuid().ToString("N") + ".cs");
            Assert.That(File.Exists(probe), Is.False);
            try
            {
                File.WriteAllText(probe, "// Owned source input-set negative fixture.\n");
                var error = Assert.Throws<AssertionException>(DeployedAnalyzerMatchesTheLastVerifiedSourceBuild);
                Assert.That(error.Message, Does.Contain("input set"));
            }
            finally
            {
                File.Delete(probe);
            }
        }
    }
}
