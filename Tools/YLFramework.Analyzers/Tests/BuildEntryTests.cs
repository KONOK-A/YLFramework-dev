using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace YLFramework.Analyzers.Tests
{
    [NonParallelizable]
    public class BuildEntryTests
    {
        [Test]
        public async Task ConcurrentEntryIsRejectedBeforeBuildTestOrDeployment()
        {
            if (Environment.GetEnvironmentVariable("YL64_BUILD_LOCK_CHILD") == "1")
                Assert.Fail("Second entry reached SDK tests instead of rejecting the held build lock.");
            var output = Path.Combine(AnalyzerFixture.Root, "Temp/YLFramework.Analyzers");
            Directory.CreateDirectory(output);
            FileStream ownedLock = null;
            try
            {
                try
                {
                    ownedLock = new FileStream(Path.Combine(output, "entry.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) { /* 唯一正式入口执行本测试时已持有合作锁，不接管该句柄。 */ }
                var start = new ProcessStartInfo("pwsh") { WorkingDirectory = AnalyzerFixture.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(AnalyzerFixture.Root, "Tools/YLFramework.Analyzers/build.ps1") })
                    start.ArgumentList.Add(argument);
                start.Environment["YL64_BUILD_LOCK_CHILD"] = "1";
                using var process = Process.Start(start);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                    Assert.Fail("Second entry did not fail fast under the held build lock.");
                }
                var text = await stdout + await stderr;
                Assert.That(process.ExitCode, Is.Not.EqualTo(0));
                Assert.That(text, Does.Contain("Another analyzer build/deploy entry is running"));
                Assert.That(text, Does.Not.Contain("SDK="));
            }
            finally
            {
                ownedLock?.Dispose();
            }
        }

        [TestCase("missing", "meta")]
        [TestCase("guid", "GUID")]
        [TestCase("label", "RoslynAnalyzer")]
        [TestCase("reference", "Runtime")]
        [TestCase("platform", "platform")]
        public async Task DeployRejectsInvalidMetaBeforeCallingSdk(string kind, string reason)
        {
            var root = Path.Combine(AnalyzerFixture.Root, "Temp", "AnalyzerMetaGuard-" + Guid.NewGuid().ToString("N"));
            var tools = Path.Combine(root, "Tools", "YLFramework.Analyzers");
            var meta = Path.Combine(root, "PackageSources", "com.kano.ylframework", "Runtime", "Core", "Analyzers", "YLFramework.Analyzers.dll.meta");
            Directory.CreateDirectory(tools);
            Directory.CreateDirectory(Path.Combine(root, "Temp"));
            Directory.CreateDirectory(Path.GetDirectoryName(meta));
            File.Copy(Path.Combine(AnalyzerFixture.Root, "Tools", "YLFramework.Analyzers", "build.ps1"), Path.Combine(tools, "build.ps1"));
            const string valid = "fileFormatVersion: 2\nguid: 471f81c4273ee66429677df44cbd6960\nlabels:\n- RoslynAnalyzer\nPluginImporter:\n  isExplicitlyReferenced: 1\n  platformData:\n  - first:\n      Any: \n    second:\n      enabled: 0\n";
            if (kind != "missing")
            {
                var text = kind == "guid" ? valid.Replace("471f81c4273ee66429677df44cbd6960", "00000000000000000000000000000000") :
                    kind == "label" ? valid.Replace("RoslynAnalyzer", "roslynanalyzer") :
                    kind == "reference" ? valid.Replace("isExplicitlyReferenced: 1", "isExplicitlyReferenced: 0") :
                    valid.Replace("enabled: 0", "enabled: 1");
                File.WriteAllText(meta, text);
            }
            try
            {
                var start = new ProcessStartInfo("pwsh")
                {
                    WorkingDirectory = tools,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(tools, "build.ps1"), "-Deploy" })
                {
                    start.ArgumentList.Add(argument);
                }
                using var process = Process.Start(start);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                    Assert.Fail("Invalid deployment metadata did not fail before SDK invocation.");
                }
                var output = await stdout + await stderr;
                Assert.That(process.ExitCode, Is.Not.EqualTo(0));
                Assert.That(output, Does.Contain(reason));
                Assert.That(output, Does.Not.Contain("SDK="));
                Assert.That(File.Exists(Path.Combine(root, "Temp", "YLFramework.Analyzers", "build-info.json")), Is.False);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
