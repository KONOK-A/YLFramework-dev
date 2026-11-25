using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using YLFramework.Editor.Resource;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Special.ResourceUpdates")]
    public class ResourceContentManifestTests
    {
        private const string SettingsSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string CatalogSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string CatalogRoute = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/catalog.bin";
        private const string EntryRoute = "https://catalog.example.org/levels/entry.bundle";
        private const string DependencyRoute = "shared/base.bundle";
        private const string PublicModulus = "tuaRWRh0eVpntV8dFutK5xFMr8DFcuIbmb4F5lDUDryl4HWxFAP25dTlXDhWt8sXJk05mbNaxm6EXWB8LHTFytNJfmAVAEpGlPRd5RJb9xYxzj+QYWx6jasWYWcqeJnPHab/sXVLntobViuGOlUhyHi8wwbXKjzOevPEMgp79+os8B35xcp7mAz08R+xLnI7+PomZJb6d4BcFDxsRmwnTyqzDrHQ7sG/WfwWYVRXrt/vdEuO6jANMo7k6z/0pp+FYyp8ZNkO0zktLuZF85tnpykl9DTsD8T9/brudYCRRi3V2mXZmj8R2QD2JtsalLNXGe/cDtT3meZPMZNHhJ+EC6DELG2BIAWyQ4+Hp/993R23/fNXHIV6KI0CGTuPL5kzCI+Kqq7IJZiA58OtNcLAuv6PGZIpDvfA6meXxHzKKyNRXgrA4ieP/yKSf38GJlZByY250nutNlq7Z//iLCUJsWTbbSet5Cb937v5jv1fbzxGXRLWkqnKd8pw7YaLl1PJ";
        private const string Signature = "Yvpd46SyesktxKLHsfW6FAc9qfDHyhbXzVVYHk1vg4X+dQXTC2n5F/F4lKVpcD0XaA8OUKUIFPtlwyjIrcSV0B2W9J2G1Aq4EozFPK3Gfkl+qChgOhDGjeF7DNhK2JseQC08/Aiuweu1C5hWySRDhtuCUdiDLtXkZBm3CeqLBabQG7ZSPkUiFjH6ky/9pSGmVV2+aqymck+aYH34PvQdsBwUujjsLe4vbgMDFyserUWVQyoEyyAdlJtZjbfbGv6hjqw0xXt0sqwdQOcV5WzRj3ScvqkKr7EUT3xx77hrly4BNUXY54Qkm7ODX55BCRbDZtCcKCbZDW8RDXFLNafzTX0mFyjNorxtiBrEbCfT9NP5kKYvTa0gTXP0C46EihJGkES8N5s25D2oV7Ajg1oolqaYZa65AADMNTayTMEklmmvnW3h7Izqx6uuz4AnySFagHl8+mjoNHCL2TBW1DVNWmVFV9ZOMxnemyTC+jpC+98FTso9RpzXJTPIcq36M6Ms";
        private const string PayloadDigest = "d5b19ee38ee9cca1e336066da8d2e92c76184fe005c76442aa2eb8b7c274b881";

        // 由 OpenSSL 独立签名。首尾空白也属于签名和摘要的输入。
        private const string PayloadJson = " {\"protocolVersion\":1,\"application\":\"app\",\"channel\":\"stable\","
            + "\"platform\":\"StandaloneWindows64\",\"compatibility\":\"base-a\",\"releaseId\":\"release-b\","
            + "\"sequence\":11,\"expiresAtUtc\":\"2030-01-01T00:00:00Z\",\"settingsSha256\":\"" + SettingsSha256 + "\","
            + "\"catalogRoute\":\"" + CatalogRoute + "\",\"descriptionRoute\":null,\"artifacts\":["
            + "{\"route\":\"" + CatalogRoute + "\",\"originReleaseId\":\"release-b\",\"source\":\"https://cdn.example.org/content/b/catalog.bin\","
            + "\"kind\":\"Catalog\",\"length\":100,\"sha256\":\"" + CatalogSha256 + "\",\"dependencies\":[]},"
            + "{\"route\":\"" + EntryRoute + "\",\"originReleaseId\":\"release-b\",\"source\":\"https://cdn.example.org/content/b/entry.bundle\","
            + "\"kind\":\"Bundle\",\"length\":200,\"sha256\":\"" + CatalogSha256 + "\",\"dependencies\":[\"" + DependencyRoute + "\"]},"
            + "{\"route\":\"" + DependencyRoute + "\",\"originReleaseId\":\"release-a\",\"source\":\"install:local/base.bundle\","
            + "\"kind\":\"Bundle\",\"length\":300,\"sha256\":\"" + CatalogSha256 + "\",\"dependencies\":[]},"
            + "{\"route\":\"optional.bundle\",\"originReleaseId\":\"release-b\",\"source\":\"https://cdn.example.org/content/b/optional.bundle\","
            + "\"kind\":\"Bundle\",\"length\":400,\"sha256\":\"" + CatalogSha256 + "\",\"dependencies\":[]}],"
            + "\"startupRoutes\":[\"" + EntryRoute + "\"]}\n";

        private RSA _signingKey;

        [OneTimeSetUp]
        public void CreateAuthorKey()
        {
            // Unity Mono 的默认 RSA 实现会保留构造时的长度，即使后续设置 KeySize。
            _signingKey = new RSACryptoServiceProvider(3072) { PersistKeyInCsp = false };
        }

        [OneTimeTearDown]
        public void ReleaseAuthorKey()
        {
            _signingKey?.Dispose();
        }

        [Test]
        public void OpenSslVectorBindsRawBytesAndResolvesStartupDependencies()
        {
            var manifest = ReadVector();

            Assert.That(manifest.Digest, Is.EqualTo(PayloadDigest));
            Assert.That(manifest.StartupRoutes, Is.EquivalentTo(new[] { CatalogRoute, EntryRoute, DependencyRoute }));
            Assert.That(manifest.Artifacts[DependencyRoute].OriginReleaseId, Is.EqualTo("release-a"));
            Assert.That(manifest.Artifacts[DependencyRoute].Source, Is.EqualTo("install:local/base.bundle"));
        }

        [Test]
        public void FileDigestAndPayloadTamperingInvalidatesSignature()
        {
            byte[] alteredPayload = Encoding.UTF8.GetBytes(PayloadJson.Replace(CatalogSha256, SettingsSha256));
            Assert.Throws<InvalidDataException>(() => ResourceContentManifest.Verify(alteredPayload, VectorEnvelope(), Trust()));
        }

        [Test]
        public void SignatureEnvelopeSelectsOnlyPretrustedKeys()
        {
            byte[] envelope = VectorEnvelope();
            envelope[5] = (byte)'x';
            Assert.Throws<InvalidDataException>(() => ResourceContentManifest.Verify(Encoding.UTF8.GetBytes(PayloadJson), envelope, Trust()));
        }

        [Test]
        public void SignatureFromAnotherKeyWithTheSameKeyIdIsRejected()
        {
            byte[] payload = Encoding.UTF8.GetBytes(PayloadJson);
            byte[] envelope = LocalEnvelope(payload);
            Assert.Throws<InvalidDataException>(() => ResourceContentManifest.Verify(payload, envelope, Trust()));
        }

        [TestCase("some-app", "stable", "StandaloneWindows64", "base-a", SettingsSha256)]
        [TestCase("app", "beta", "StandaloneWindows64", "base-a", SettingsSha256)]
        [TestCase("app", "stable", "Android", "base-a", SettingsSha256)]
        [TestCase("app", "stable", "StandaloneWindows64", "base-b", SettingsSha256)]
        [TestCase("app", "stable", "StandaloneWindows64", "base-a", CatalogSha256)]
        public void SignedManifestMustMatchTheInstalledPlayer(string app, string channel, string platform,
            string compatibility, string settingsSha256)
        {
            var trust = Trust(app, channel, platform, compatibility, settingsSha256);
            Assert.Throws<InvalidDataException>(() => ReadVector(trust));
        }

        [Test]
        public void ExpiryAppliesToNewAnnouncementsAndSavedManifestCanBeReopened()
        {
            var deadline = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var trust = Trust();
            byte[] payload = Encoding.UTF8.GetBytes(PayloadJson);
            byte[] envelope = VectorEnvelope();

            Assert.That(ResourceContentManifest.Verify(payload, envelope, trust, deadline.AddTicks(-1)).Sequence,
                Is.EqualTo(11));
            Assert.Throws<InvalidDataException>(() => ResourceContentManifest.Verify(payload, envelope, trust, deadline));
            Assert.That(ResourceContentManifest.Verify(payload, envelope, trust).Sequence, Is.EqualTo(11));
        }

        [TestCase(12, PayloadDigest, ResourceContentSequenceComparison.Lower)]
        [TestCase(11, CatalogSha256, ResourceContentSequenceComparison.Conflict)]
        [TestCase(11, PayloadDigest, ResourceContentSequenceComparison.Same)]
        [TestCase(10, CatalogSha256, ResourceContentSequenceComparison.New)]
        [TestCase(0, null, ResourceContentSequenceComparison.New)]
        public void SequenceComparesWithTheHighestSuccessfulActivation(long highest, string digest,
            ResourceContentSequenceComparison expected)
        {
            Assert.That(ReadVector().CompareSequence(highest, digest), Is.EqualTo(expected));
        }

        [TestCase("\"sequence\":11", "\"sequence\":11,\"sequen\\u0063e\":12")]
        [TestCase("\"sequence\":11", "\"sequence\":11,\"Sequence\":12")]
        [TestCase("\"sequence\":11", "\"sequence\":011")]
        [TestCase("\"sequence\":11", "\"sequence\":0xB")]
        [TestCase("\"sequence\":11", "\"sequence\":\"11\"")]
        [TestCase("\"sequence\":11", "\"sequence\":9223372036854775808")]
        [TestCase("\"sequence\":11", "\"sequence\":11 /* 12 */")]
        [TestCase("\"sequence\":11", "'sequence':11")]
        [TestCase("\"sequence\":11,", "")]
        [TestCase("\"descriptionRoute\":null", "\"descriptionRoute\":null,\"algorithm\":\"RSA\"")]
        [TestCase("\"dependencies\":[]", "\"dependencies\":[\"missing.bundle\"]")]
        [TestCase("\"dependencies\":[]", "\"dependencies\":[\"optional.bundle\"]")]
        [TestCase("\"route\":\"optional.bundle\"", "\"route\":\"SHARED/Base.bundle\"")]
        [TestCase("install:local/base.bundle", "install:../base.bundle")]
        [TestCase("install:local/base.bundle", "install:local/NUL.bundle")]
        [TestCase("https://cdn.example.org/content/b/entry.bundle", "https://cdn.example.org/content-other/entry.bundle")]
        [TestCase("https://cdn.example.org/content/b/entry.bundle", "https://cdn.example.org/content/b/%65ntry.bundle")]
        [TestCase("https://cdn.example.org/content/b/entry.bundle", "https://unknown.example.org/content/b/entry.bundle")]
        [TestCase("https://cdn.example.org/content/b/entry.bundle", "https://cdn.example.org/content/b/entry.bundle?mutable")]
        [TestCase("optional.bundle", "optional.dll")]
        public void RejectsSignedMalformedOrAmbiguousManifests(string oldText, string newText)
        {
            string changedJson = PayloadJson.Replace(oldText, newText);
            Assert.That(changedJson, Is.Not.EqualTo(PayloadJson));
            byte[] payload = Encoding.UTF8.GetBytes(changedJson);
            var trust = Trust(key: _signingKey.ExportParameters(false));
            Assert.Throws<InvalidDataException>(() => ResourceContentManifest.Verify(payload, LocalEnvelope(payload), trust));
        }

        [Test]
        public void EditorWriterProducesAnEncryptedManifestAcceptedByRuntime()
        {
            const string descriptionRoute = "encrypted-release.json";
            const string cipherRoute = "https://cdn.example.org/content/a/protected.bundle";
            var deadline = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var artifacts = new[]
            {
                new ResourceContentArtifact(CatalogRoute, "release-b", "install:catalog.bin",
                    ResourceContentArtifactKind.Catalog, 40, CatalogSha256),
                new ResourceContentArtifact(descriptionRoute, "release-b", "install:encrypted-release.json",
                    ResourceContentArtifactKind.EncryptedDescription, 60, CatalogSha256),
                new ResourceContentArtifact(cipherRoute, "release-a", cipherRoute,
                    ResourceContentArtifactKind.Ciphertext, 80, CatalogSha256)
            };
            byte[] payload = ResourceContentManifestWriter.Serialize("app", "stable", "StandaloneWindows64", "base-a",
                "release-b", 11, deadline, SettingsSha256, CatalogRoute, descriptionRoute, artifacts, new[] { cipherRoute });
            byte[] envelope = ResourceContentManifestWriter.Sign(payload, "release", _signingKey);

            var manifest = ResourceContentManifest.Verify(payload, envelope, Trust(key: _signingKey.ExportParameters(false)));
            Assert.That(manifest.DescriptionRoute, Is.EqualTo(descriptionRoute));
            Assert.That(manifest.Artifacts[cipherRoute].OriginReleaseId, Is.EqualTo("release-a"));
            Assert.That(manifest.StartupRoutes, Is.EquivalentTo(new[] { CatalogRoute, descriptionRoute, cipherRoute }));
        }

        [TestCase(CatalogRoute, "install:Chapter One/catalog.bin")]
        [TestCase("https://cdn.example.org/chapter%20one/catalog.bin", "install:catalog.bin")]
        [TestCase(CatalogRoute, "https://cdn.example.org/chapter%20one/catalog.bin")]
        public void EditorWriterRejectsPathsOutsideTheManifestProtocol(string route, string source)
        {
            var artifacts = new[]
            {
                new ResourceContentArtifact(route, "release-a", source, ResourceContentArtifactKind.Catalog, 40, CatalogSha256)
            };

            Assert.Throws<InvalidDataException>(() => ResourceContentManifestWriter.Serialize(
                "app", "stable", "StandaloneWindows64", "base-a", "release-a", 10,
                new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), SettingsSha256, route, null, artifacts,
                Array.Empty<string>()));
        }

        [Test]
        public void LoopbackSourcesRequireAnExplicitTrustOption()
        {
            var root = new Uri("http://127.0.0.1:8800/content/");
            var key = _signingKey.ExportParameters(false);
            Assert.Throws<ArgumentException>(() => Trust(key: key, root: root));

            byte[] payload = Encoding.UTF8.GetBytes(PayloadJson.Replace("https://cdn.example.org/content/", root.AbsoluteUri));
            byte[] envelope = LocalEnvelope(payload);
            Assert.Throws<InvalidDataException>(() => ResourceContentManifest.Verify(payload, envelope, Trust(key: key)));
            Assert.That(ResourceContentManifest.Verify(payload, envelope, Trust(key: key, root: root, allowLoopback: true))
                .Artifacts[EntryRoute].Source, Is.EqualTo("http://127.0.0.1:8800/content/b/entry.bundle"));
        }

        private static ResourceContentManifest ReadVector(ResourceContentManifestTrust trust = null)
        {
            return ResourceContentManifest.Verify(Encoding.UTF8.GetBytes(PayloadJson), VectorEnvelope(), trust ?? Trust());
        }

        private static ResourceContentManifestTrust Trust(string app = "app", string channel = "stable",
            string platform = "StandaloneWindows64", string compatibility = "base-a", string settingsSha256 = SettingsSha256,
            RSAParameters? key = null, Uri root = null, bool allowLoopback = false)
        {
            var publicKey = key ?? new RSAParameters
            {
                Modulus = Convert.FromBase64String(PublicModulus),
                Exponent = new byte[] { 1, 0, 1 }
            };
            var keys = new Dictionary<string, RSAParameters> { ["release"] = publicKey };
            return new ResourceContentManifestTrust(app, channel, platform, compatibility, settingsSha256, keys,
                new[] { root ?? new Uri("https://cdn.example.org/content/") }, allowLoopback);
        }

        private byte[] LocalEnvelope(byte[] payload)
        {
            return Envelope(_signingKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        private static byte[] VectorEnvelope()
        {
            return Envelope(Convert.FromBase64String(Signature));
        }

        private static byte[] Envelope(byte[] signature)
        {
            byte[] envelope = new byte[12 + signature.Length];
            Encoding.ASCII.GetBytes("YLR1\u0007release").CopyTo(envelope, 0);
            signature.CopyTo(envelope, 12);
            return envelope;
        }
    }
}
