using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceUpdateTests
    {
        private static byte[] Signed(int version, long sequence, RSA key)
        {
            var snapshot = Snapshot("a", version);
            return ResourceReleaseSigner.Create(new ResourceRelease(Path.GetDirectoryName(snapshot.DirectoryPath), snapshot.Document),
                "test", sequence, "isolated-test", key, DateTimeOffset.UtcNow.AddDays(1));
        }

        private static byte[] Resign(byte[] envelope, RSA key, Action<JObject> change)
        {
            var data = ResourceCatalogCodec.ParseObject(envelope);
            var payload = ResourceCatalogCodec.ParseObject(Convert.FromBase64String((string)data["Payload"]));
            change(payload);
            data["Payload"] = Convert.ToBase64String(ResourceCatalogCodec.CanonicalBytes(payload));
            data.Remove("Signature");
            data["Signature"] = Convert.ToBase64String(key.SignData(ResourceCatalogCodec.CanonicalBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            return ResourceCatalogCodec.CanonicalBytes(data);
        }

        [Test]
        public void RemotePreparationMustHaveOneResourceSystemEntryRatherThanANewUpdaterAuthority()
        {
            Assert.That(typeof(ResourceSystem).GetMethods().Any(x => x.Name == "CheckRemoteUpdatesAsync"), Is.True,
                "8.8 must authenticate remote metadata and produce the existing UpdatePlan/PreparedUpdate pipeline.");
        }

        [Test]
        public void EditorSignedActualReleaseRoundTripsAndRejectsWrongScopeExpiryAndCompatibility()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            using var key = certificate.GetRSAPrivateKey();
            var source = ResourceDownloadTests.Source(server, certificate);
            var mount = new ResourceProjectMount(Snapshot().Document.ProjectId, "StandaloneWindows64", "fixture-v1", Snapshot(), remote: source);
            var before = ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(Snapshot("a", 2).DirectoryPath));
            var envelope = Signed(2, 7, key);
            var release = new ResourceRemoteRelease(envelope, mount, Application.unityVersion, DateTimeOffset.UtcNow);
            release.ReadCatalog(ResourceCatalogCodec.Write(Snapshot("a", 2).Document), mount, Application.unityVersion);
            Assert.That(release.Sequence, Is.EqualTo(7));
            Assert.That(release.Catalog.CatalogDigest, Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
            foreach (var field in new[] { "ProjectId", "Platform", "Channel", "UnityCompatibility", "AppContentCompatibilityId" })
            {
                Assert.Throws<InvalidDataException>(() => new ResourceRemoteRelease(Resign(envelope, key, data => data[field] = "unauthorized"), mount, Application.unityVersion, DateTimeOffset.UtcNow), field);
            }

            Assert.Throws<InvalidDataException>(() => new ResourceRemoteRelease(Resign(envelope, key, data =>
            {
                data["IssuedUnixSeconds"] = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();
                data["ExpiresUnixSeconds"] = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            }), mount, Application.unityVersion, DateTimeOffset.UtcNow));
            Assert.Throws<InvalidDataException>(() => release.ReadCatalog(ResourceCatalogCodec.Write(Snapshot().Document), mount, Application.unityVersion));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(Path.GetDirectoryName(Snapshot("a", 2).DirectoryPath)), "Signing must not mutate publisher output or include a private key.");
            Assert.That(System.Text.Encoding.UTF8.GetString(envelope), Does.Not.Contain("PRIVATE").And.Not.Contain("Modulus").And.Not.Contain("Exponent"));
        }

        [Test]
        public void EditorExternalCredentialAndExportStayOutsideRepositoryAndNeverOverwrite()
        {
            using var certificate = ResourceDownloadTests.Certificate();
            using var server = new ResourceTlsServer(certificate);
            var source = ResourceDownloadTests.Source(server, certificate);
            var mount = new ResourceProjectMount(Snapshot().Document.ProjectId, "StandaloneWindows64", "fixture-v1", Snapshot(), remote: source);
            var snapshot = Snapshot("a", 2);
            var release = new ResourceRelease(Path.GetDirectoryName(snapshot.DirectoryPath), snapshot.Document);
            var oldPath = Environment.GetEnvironmentVariable("YL_RESOURCE_SIGNING_PFX");
            var oldPassword = Environment.GetEnvironmentVariable("YL_RESOURCE_SIGNING_PASSWORD");
            var directory = Path.Combine(Path.GetTempPath(), "kilo", "r88-signing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
            try
            {
                Environment.SetEnvironmentVariable("YL_RESOURCE_SIGNING_PFX", Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PFX"));
                Environment.SetEnvironmentVariable("YL_RESOURCE_SIGNING_PASSWORD", Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD"));
                var metadata = ResourceReleaseSigner.CreateFromEnvironment(release, "test", 7, "isolated-test", DateTimeOffset.UtcNow.AddDays(1));
                var verified = new ResourceRemoteRelease(metadata, mount, Application.unityVersion, DateTimeOffset.UtcNow);
                verified.ReadCatalog(ResourceCatalogCodec.Write(snapshot.Document), mount, Application.unityVersion);
                var output = Path.Combine(directory, "release.json");
                ResourceReleaseSigner.Export(output, metadata);
                CollectionAssert.AreEqual(metadata, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(output)));
                Assert.Throws<IOException>(() => ResourceReleaseSigner.Export(output, metadata));
                CollectionAssert.AreEqual(metadata, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(output)));
                Environment.SetEnvironmentVariable("YL_RESOURCE_SIGNING_PFX", Path.Combine(Application.dataPath, "not-a-private-key.pfx"));
                Assert.Throws<InvalidOperationException>(() => ResourceReleaseSigner.CreateFromEnvironment(release, "test", 7, "isolated-test", DateTimeOffset.UtcNow.AddDays(1)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("YL_RESOURCE_SIGNING_PFX", oldPath);
                Environment.SetEnvironmentVariable("YL_RESOURCE_SIGNING_PASSWORD", oldPassword);
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(directory), true);
            }
        }
    }
}
