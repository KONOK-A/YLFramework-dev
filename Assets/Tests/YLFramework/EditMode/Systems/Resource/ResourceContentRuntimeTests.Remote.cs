using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceContentRuntimeTests
    {
        [UnityTest]
        public IEnumerator RealLoopbackRemotePrepareValidatesEncryptedDeliveryWithoutKeyFallbackOrRedownload()
        {
            yield return new EnterPlayMode();
            yield return RemoteEncryptedBody();
        }

        private static IEnumerator RemoteEncryptedBody()
        {
            var oldPath = Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PFX");
            var oldPassword = Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD");
            try
            {
                var ownedOverride = SessionState.GetString(Key + "tlsOverride", "");
                if (!string.IsNullOrEmpty(ownedOverride))
                {
                    var parent = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Path.GetTempPath(), "kilo"))) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(ownedOverride)).StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(ownedOverride) != "local-only-tls.pfx")
                    {
                        throw new InvalidOperationException("The optional local test certificate must stay inside its owned temporary scope.");
                    }

                    ResourceLocalSnapshot.RejectLinks(ownedOverride);
                    Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PFX", ownedOverride);
                    Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD", "e04-local-test-only");
                }
                // 常规运行复用既有测试凭据合同；本轮覆盖仅在该枚举器内生效。
                yield return RemoteEncryptedTransfer();
            }
            finally
            {
                Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PFX", oldPath);
                Environment.SetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD", oldPassword);
                Assert.That(string.Equals(Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PFX"), oldPath, StringComparison.Ordinal), Is.True,
                    "Restore the pre-existing test certificate path without logging its value.");
                Assert.That(string.Equals(Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD"), oldPassword, StringComparison.Ordinal), Is.True,
                    "Restore the pre-existing test credential without logging its value.");
            }
        }

        private static IEnumerator RemoteEncryptedTransfer()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var signing = certificate.GetRSAPrivateKey();
            var current = Snapshot();
            var candidate = Snapshot(true);
            var envelope = ResourceReleaseSigner.Create(new ResourceRelease(Path.GetDirectoryName(candidate.DirectoryPath), candidate.Document),
                "test", 7, "isolated-test", signing, DateTimeOffset.UtcNow.AddDays(1), channelSupportsContentEncryption: true);
            var files = candidate.Document.Bundles.Cast<ResourceCatalogFile>().Concat(candidate.Document.SupportFiles).Select(x => x.RelativeFile)
                .Concat(new[] { ".ylr-complete.json", "resource-catalog.json" }).ToDictionary(
                    x => "/" + candidate.Document.CatalogDigest + "/" + x,
                    x => File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(candidate.DirectoryPath, x))), StringComparer.Ordinal);
            server.Responder = request => request.Path == "/release.json" ? new ResourceTlsServer.Response { Body = envelope } :
                files.TryGetValue(request.Path, out var body) ? new ResourceTlsServer.Response { Body = body, Headers = { ["ETag"] = "\"e04-stable\"" } } :
                new ResourceTlsServer.Response { StatusCode = 404 };
            var remote = ResourceDownloadTests.Source(server, certificate);
            var mount = new ResourceProjectMount(current.Document.ProjectId, "StandaloneWindows64", "app-v1", current, remote: remote);
            var store = new ArtifactStore(Path.Combine(SessionState.GetString(Key + "cache", ""), "r"), new[] { mount });
            using var keys = new TestKeys();
            using var initialize = ResourceSystem.InitializeAsync(new ResourceConfiguration(store, decryption: new ResourceDecryptionOptions(keys)));
            yield return initialize;
            var system = initialize.TakeResult();
            var copies = keys.Copies;
            using var checking = system.CheckRemoteUpdatesAsync();
            yield return checking;
            var plan = checking.TakeResult();
            Assert.That(keys.Copies, Is.EqualTo(copies), "Remote Check authenticates release metadata but must not request content keys.");
            keys.Missing = true;
            using (var missing = system.PrepareUpdateAsync(plan))
            {
                yield return missing;
                Assert.That(missing.Error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            }
            Assert.That(store.GetPreparedUpdates(), Is.Empty);
            Assert.That(system.GetActiveCatalogDigest(current.Document.ProjectId), Is.EqualTo(current.Document.CatalogDigest));
            var downloaded = server.RequestCount;
            keys.Missing = false;
            using var preparing = system.PrepareUpdateAsync(plan);
            yield return preparing;
            var prepared = preparing.TakeResult();
            Assert.That(server.RequestCount, Is.EqualTo(downloaded), "Missing key is not corruption and cannot trigger implicit redownload.");
            using var activate = system.ActivatePreparedUpdateAsync(prepared);
            yield return activate;
            Assert.That(activate.TakeResult(), Is.True);
            using (var text = system.Acquire<UnityEngine.TextAsset>(Address("entry.txt")))
            {
                Assert.That(text.Asset.text, Is.EqualTo("second formal encrypted version"));
            }

            using var rollback = system.RollbackAsync();
            yield return rollback;
            Assert.That(rollback.TakeResult(), Is.True);
            using (var text = system.Acquire<UnityEngine.TextAsset>(Address("entry.txt")))
            {
                Assert.That(text.Asset.text, Does.Contain("first release"));
            }

            Assert.That(system.GetContentDiagnosticsSnapshot().ReservedBufferBytes, Is.Zero);
        }
    }
}
