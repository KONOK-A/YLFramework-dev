using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Integrations.HybridCLR.Editor;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRPackageTests
    {
        [Test]
        public void InstalledAdapterKeepsItsDependencyAndSampleBoundaries()
        {
            var package = PackageInfo.FindForAssembly(typeof(HybridCLRLoader).Assembly);
            Assert.That(package, Is.Not.Null);
            Assert.That(package.name, Is.EqualTo("com.kano.ylframework.hybridclr"));
            Assert.That(package.version, Is.EqualTo("0.1.0-preview.1"));
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(package.resolvedPath, "package.json")));
            Assert.That((string)manifest["dependencies"]["com.kano.ylframework"], Is.EqualTo(package.version));
            Assert.That((string)manifest["dependencies"]["com.code-philosophy.hybridclr"], Is.EqualTo("9.0.3"));
            Assert.That(((JArray)manifest["samples"]).Any(sample => (string)sample["path"] == "Samples~/HybridCLR"), Is.True);
            Assert.That(Directory.Exists(Path.Combine(package.resolvedPath, "Samples")), Is.False);
            Assert.That(Directory.GetFiles(package.resolvedPath, "*.dll", SearchOption.AllDirectories), Is.Empty, "SDK, native and generated binaries are external inputs.");
            foreach (var folder in new[] { "Assets", "Packages", "ProjectSettings", "Tests", "Tools", "Library", ".kilo" })
            {
                Assert.That(Directory.Exists(Path.Combine(package.resolvedPath, folder)), Is.False, folder);
            }
            Assert.That(typeof(HybridCLRWindow).Assembly.GetType("YLFramework.Integrations.HybridCLR.Editor.HybridCLRIntegrationExporter"), Is.Null);
        }
    }
}
