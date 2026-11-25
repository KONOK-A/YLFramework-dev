using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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

        [Test]
        public void CacheInspectionOnMissingRootAndCancellationDoesNotCreateStorage()
        {
            using var fixture = new CacheFixture(Trust());
            var report = fixture.Inspect();
            Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.Exact));
            Assert.That(report.Owned.Bytes, Is.Zero);
            Assert.That(report.CleanupCandidates.Bytes, Is.Zero);
            Assert.That(Directory.Exists(fixture.Root), Is.False);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<System.Threading.Tasks.TaskCanceledException>(() =>
                fixture.Updates.InspectCacheAsync(cancellation.Token).GetAwaiter().GetResult());
            Assert.That(Directory.Exists(fixture.Root), Is.False);
        }

        [Test]
        public void CacheInspectionCountsSharedFilesOnceAndExplainsOptionalAndOpenFiles()
        {
            var trust = Trust(key: _signingKey.ExportParameters(false));
            using var fixture = new CacheFixture(trust);
            string shared = fixture.Artifact(8 * 1024 * 1024);
            string orphan = fixture.Artifact(3 * 1024 * 1024);
            var active = MakeVersion(shared, 12, trust);
            var previous = MakeVersion(shared, 11, trust);
            Assert.That(fixture.Store.TryAcquireWriter(out var writer), Is.True);
            using (writer)
            {
                Assert.That(writer.TryCommit(0, new ResourceContentStore.State(1, previous.Version, null, previous.Version), default), Is.True);
                Assert.That(writer.TryCommit(1, new ResourceContentStore.State(2, active.Version, previous.Version, active.Version), default), Is.True);
                Directory.CreateDirectory(fixture.Store.VersionsRoot);
                foreach (var entry in new[] { active, previous })
                    File.WriteAllText(Path.Combine(fixture.Store.VersionsRoot, entry.Version.Digest + ".json"), entry.Announcement);
            }

            string unrelated = Path.Combine(fixture.Objects, "external.bundle");
            File.WriteAllText(unrelated, "untouched");
            string[] inventory = Snapshot(fixture.Root);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var report = fixture.Inspect();
                Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.Exact), string.Join("\n", report.Issues));
                Assert.That(report.Files.Where(file => file.Kind == ResourceCacheFileKind.Artifact).Sum(file => file.Bytes),
                    Is.EqualTo(11 * 1024 * 1024));
                var retained = report.Files.Single(file => file.RelativePath.EndsWith(Path.GetFileName(shared)));
                Assert.That(retained.Retention, Is.EqualTo(ResourceCacheRetention.ActiveVersion | ResourceCacheRetention.PreviousVersion));
                Assert.That(report.CleanupCandidates.Bytes, Is.EqualTo(3 * 1024 * 1024));
                Assert.That(report.Files.Any(file => file.RelativePath.Contains("external")), Is.False);
                Assert.That(Snapshot(fixture.Root), Is.EqualTo(inventory));
            }

            using var pinned = ResourceContentFileIO.OpenRead(orphan);
            var inUse = fixture.Inspect();
            Assert.That(inUse.CleanupCandidates.Bytes, Is.Zero);
            Assert.That(inUse.Files.Single(file => file.RelativePath.EndsWith(Path.GetFileName(orphan))).Retention,
                Is.EqualTo(ResourceCacheRetention.OpenHandle));
        }

        [Test]
        public void CacheInspectionDoesNotClaimEqualLengthReplacementOfSignedObject()
        {
            var trust = Trust(key: _signingKey.ExportParameters(false));
            using var fixture = new CacheFixture(trust);
            string path = fixture.Artifact(128);
            var active = MakeVersion(path, 12, trust);
            Assert.That(fixture.Store.TryAcquireWriter(out var writer), Is.True);
            using (writer)
                Assert.That(writer.TryCommit(0, new ResourceContentStore.State(1, active.Version, null, active.Version), default), Is.True);
            Directory.CreateDirectory(fixture.Store.VersionsRoot);
            File.WriteAllText(Path.Combine(fixture.Store.VersionsRoot, active.Version.Digest + ".json"), active.Announcement);
            byte[] replacement = new byte[128];
            replacement[0] = 1;
            File.WriteAllBytes(path, replacement);

            var report = fixture.Inspect();
            var file = report.Files.Single(item => item.Kind == ResourceCacheFileKind.Artifact);
            Assert.That(file.Bytes, Is.EqualTo(128));
            Assert.That(file.OwnershipConfirmed, Is.False);
            Assert.That(file.Disposition, Is.EqualTo(ResourceCacheDisposition.Undetermined));
            Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
        }

        [Test]
        public void PublishedObjectDuringWriterAndCorruptStateAreNeverCleanupCandidates()
        {
            using var fixture = new CacheFixture(Trust());
            string published = fixture.Artifact(128 * 1024);
            string temporary = Path.Combine(fixture.Objects, ".stage-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(temporary, "abandoned");
            Assert.That(fixture.Store.TryAcquireWriter(out var writer), Is.True);
            using (writer)
            {
                var busy = fixture.Inspect();
                Assert.That(busy.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
                Assert.That(busy.Files.Single(file => file.RelativePath.EndsWith(Path.GetFileName(published))).Disposition,
                    Is.EqualTo(ResourceCacheDisposition.Undetermined));
                Assert.That(busy.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.Unknown));
                Assert.That(busy.CleanupCandidates.Bytes, Is.Null);
            }
            File.WriteAllText(Path.Combine(fixture.Store.Root, ResourceContentStore.StateFile), "not a record");
            string[] inventory = Snapshot(fixture.Root);
            var corrupt = fixture.Inspect();
            Assert.That(corrupt.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
            Assert.That(corrupt.CleanupCandidates.Bytes, Is.Null);
            Assert.That(corrupt.Issues, Is.Not.Empty);
            Assert.That(Snapshot(fixture.Root), Is.EqualTo(inventory));
        }

        [Test]
        public void ExclusiveExternalWriterLeavesOnlyAnObservedLowerBound()
        {
            using var fixture = new CacheFixture(Trust());
            string readable = fixture.Artifact(117);
            string inaccessible = fixture.Artifact(243);
            using var writer = new FileStream(inaccessible, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var report = fixture.Inspect();
            Assert.That(report.Owned.Bytes, Is.EqualTo(new FileInfo(readable).Length));
            Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
            Assert.That(report.CleanupCandidates.Bytes, Is.EqualTo(117));
            Assert.That(report.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
            var blocked = report.Files.Single(file => file.RelativePath.EndsWith(Path.GetFileName(inaccessible)));
            Assert.That(blocked.OwnershipConfirmed, Is.False);
            Assert.That(blocked.Disposition, Is.EqualTo(ResourceCacheDisposition.Undetermined));
        }

        [Test]
        public void InspectsReadOnlyRootWithoutCreatingWriterOrChangingInventory()
        {
            using var fixture = new CacheFixture(Trust());
            fixture.Artifact(117);
            string stage = Path.Combine(fixture.Objects, ".stage-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(stage, "abandoned");
            string external = Path.Combine(fixture.Store.Root, "user-sentinel");
            File.WriteAllText(external, "outside the cache inventory");
            string[] before = Snapshot(fixture.Root);

            ChangeFixtureAccess(fixture.Store.Root, "/deny *S-1-1-0:(OI)(CI)(WD,AD,WA,WEA,DC,DE)");
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(fixture.Store.Root, "no-write"), "probe"));
                for (int observation = 0; observation < 2; observation++)
                {
                    var report = fixture.Inspect();
                    var unowned = report.Files.Single(file => file.RelativePath.EndsWith(Path.GetFileName(stage)));
                    Assert.That(unowned.OwnershipConfirmed, Is.False);
                    Assert.That(unowned.Disposition, Is.EqualTo(ResourceCacheDisposition.Undetermined));
                    Assert.That(unowned.Bytes, Is.EqualTo(new FileInfo(stage).Length));
                    Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
                    Assert.That(report.Owned.Bytes, Is.EqualTo(117));
                    Assert.That(report.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
                    Assert.That(report.CleanupCandidates.Bytes, Is.EqualTo(117));
                    Assert.That(Snapshot(fixture.Root), Is.EqualTo(before));
                }
            }
            finally
            {
                ChangeFixtureAccess(fixture.Store.Root, "/remove:d *S-1-1-0");
            }
        }

        [Test]
        public void OnlySameStoreTemporaryControlRecordsPassInspectionAndCleanup()
        {
            var trust = Trust(key: _signingKey.ExportParameters(false));
            using var fixture = new CacheFixture(trust);
            var active = MakeVersion(fixture.Artifact(117), 12, trust);
            var state = new ResourceContentStore.State(1, active.Version, null, active.Version);
            Assert.That(fixture.Store.TryAcquireWriter(out var writer), Is.True);
            using (writer)
                Assert.That(writer.TryCommit(0, state, default), Is.True);
            Directory.CreateDirectory(fixture.Store.VersionsRoot);
            string manifest = Path.Combine(fixture.Store.VersionsRoot, active.Version.Digest + ".json");
            File.WriteAllText(manifest, active.Announcement);

            var otherStore = new ResourceContentStore(fixture.Root, trust.Application, "other-channel");
            Assert.That(otherStore.TryAcquireWriter(out var otherWriter), Is.True);
            using (otherWriter)
                Assert.That(otherWriter.TryCommit(0, state, default), Is.True);
            string valid = TemporaryRecordPath(ResourceContentStore.StateFile);
            string foreign = TemporaryRecordPath(ResourceContentStore.StateFile);
            string damaged = TemporaryRecordPath(ResourceContentStore.JournalFile);
            File.Copy(Path.Combine(fixture.Store.Root, ResourceContentStore.StateFile), valid);
            File.Copy(Path.Combine(otherStore.Root, ResourceContentStore.StateFile), foreign);
            File.WriteAllText(damaged, "not a complete journal");
            string[] before = Snapshot(fixture.Root);

            var report = fixture.Inspect();
            var owned = report.Files.Single(file => file.RelativePath == Path.GetFileName(valid));
            Assert.That(owned.OwnershipConfirmed, Is.True);
            Assert.That(owned.Disposition, Is.EqualTo(ResourceCacheDisposition.CleanupCandidate));
            Assert.That(report.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
            Assert.That(report.CleanupCandidates.Bytes, Is.EqualTo(new FileInfo(valid).Length));
            foreach (string unproved in new[] { foreign, damaged })
            {
                var row = report.Files.Single(file => file.RelativePath == Path.GetFileName(unproved));
                Assert.That(row.OwnershipConfirmed, Is.False);
                Assert.That(row.Disposition, Is.EqualTo(ResourceCacheDisposition.Undetermined));
                Assert.That(row.Bytes, Is.EqualTo(new FileInfo(unproved).Length));
            }
            Assert.That(Snapshot(fixture.Root), Is.EqualTo(before));

            Assert.That(fixture.Store.TryAcquireWriter(out var cleanup), Is.True);
            using (cleanup)
                Assert.That(cleanup.CleanupTemporaryRecords(default), Is.EqualTo(1));
            Assert.That(Snapshot(fixture.Root), Is.EqualTo(before.Where(row =>
                !row.StartsWith(valid + ":", StringComparison.Ordinal))));

            string TemporaryRecordPath(string record) =>
                Path.Combine(fixture.Store.Root, $".{record}-{Guid.NewGuid():N}.tmp");
        }

        [Test]
        public void DeniedReadOfCandidateLeavesItsLengthOutsideTheOwnedTally()
        {
            using var fixture = new CacheFixture(Trust());
            fixture.Artifact(117);
            string denied = fixture.Artifact(243);
            string[] before = Snapshot(fixture.Root);
            ChangeFixtureAccess(denied, "/deny *S-1-1-0:(RD)");
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(denied));
                for (int observation = 0; observation < 2; observation++)
                {
                    var report = fixture.Inspect();
                    var file = report.Files.Single(item => item.RelativePath.EndsWith(Path.GetFileName(denied)));
                    Assert.That(file.Bytes, Is.EqualTo(243));
                    Assert.That(file.OwnershipConfirmed, Is.False);
                    Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
                    Assert.That(report.Owned.Bytes, Is.EqualTo(117));
                    Assert.That(report.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
                    Assert.That(report.CleanupCandidates.Bytes, Is.EqualTo(117));
                    Assert.That(report.Issues, Is.Not.Empty);
                }
            }
            finally
            {
                ChangeFixtureAccess(denied, "/remove:d *S-1-1-0");
            }
            Assert.That(Snapshot(fixture.Root), Is.EqualTo(before));
        }

        [Test]
        public void DoesNotFollowReparsePointIntoAnotherDirectory()
        {
            using var fixture = new CacheFixture(Trust());
            Directory.CreateDirectory(fixture.Store.Root);
            string external = Path.Combine(fixture.Root, "external");
            Directory.CreateDirectory(external);
            string sentinel = Path.Combine(external, new string('a', 64) + ".bundle");
            File.WriteAllText(sentinel, "leave the linked target untouched");
            RunFixtureCommand("cmd.exe", "/d /c mklink /J \"" + fixture.Objects + "\" \"" + external + "\"");
            try
            {
                Assert.That(File.GetAttributes(fixture.Objects).HasFlag(FileAttributes.ReparsePoint), Is.True);
                var report = fixture.Inspect();
                Assert.That(report.Files, Is.Empty);
                Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.Unknown));
                Assert.That(report.Owned.Bytes, Is.Null);
                Assert.That(report.CleanupCandidates.Bytes, Is.Null);
                Assert.That(File.ReadAllText(sentinel), Is.EqualTo("leave the linked target untouched"));
            }
            finally
            {
                Directory.Delete(fixture.Objects);
            }
        }

        [Test]
        public void CorruptJournalKeepsKnownActiveAndByteEvidence()
        {
            var trust = Trust(key: _signingKey.ExportParameters(false));
            using var fixture = new CacheFixture(trust);
            string shared = fixture.Artifact(100);
            fixture.Artifact(200);
            var active = MakeVersion(shared, 12, trust);
            Assert.That(fixture.Store.TryAcquireWriter(out var writer), Is.True);
            using (writer)
                Assert.That(writer.TryCommit(0, new ResourceContentStore.State(1, active.Version, null, active.Version), default), Is.True);
            Directory.CreateDirectory(fixture.Store.VersionsRoot);
            File.WriteAllText(Path.Combine(fixture.Store.VersionsRoot, active.Version.Digest + ".json"), active.Announcement);
            File.WriteAllText(Path.Combine(fixture.Store.Root, ResourceContentStore.JournalFile), "corrupt");

            var report = fixture.Inspect();
            Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.Exact));
            Assert.That(report.Files.Single(file => file.RelativePath.EndsWith(Path.GetFileName(shared))).Retention,
                Is.EqualTo(ResourceCacheRetention.ActiveVersion));
            Assert.That(report.Retained.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
            Assert.That(report.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.Unknown));
            Assert.That(report.CleanupCandidates.Bytes, Is.Null);
            Assert.That(report.Undetermined.Certainty, Is.EqualTo(ResourceCacheCertainty.Exact));
            Assert.That(report.Undetermined.Bytes, Is.EqualTo(200));
        }

        [Test]
        public void InvalidUtf8InReferencedManifestKeepsKnownActiveAndByteEvidence()
        {
            var trust = Trust(key: _signingKey.ExportParameters(false));
            using var fixture = new CacheFixture(trust);
            string activeArtifact = fixture.Artifact(100);
            string previousArtifact = fixture.Artifact(200);
            var active = MakeVersion(activeArtifact, 12, trust);
            var previous = MakeVersion(previousArtifact, 11, trust);
            Assert.That(fixture.Store.TryAcquireWriter(out var writer), Is.True);
            using (writer)
            {
                var installed = new ResourceContentStore.State(1, previous.Version, null, previous.Version);
                var updated = new ResourceContentStore.State(2, active.Version, previous.Version, active.Version);
                Assert.That(writer.TryCommit(0, installed, default), Is.True);
                Assert.That(writer.TryCommit(1, updated, default), Is.True);
            }
            Directory.CreateDirectory(fixture.Store.VersionsRoot);
            File.WriteAllText(Path.Combine(fixture.Store.VersionsRoot, active.Version.Digest + ".json"), active.Announcement);
            string malformed = previous.Version.Digest + ".json";
            File.WriteAllBytes(Path.Combine(fixture.Store.VersionsRoot, malformed), new byte[] { 0xe2, 0x20 });
            string[] before = Snapshot(fixture.Root);

            var report = fixture.Inspect();
            Assert.That(report.Issues.Any(issue => issue.Contains(malformed, StringComparison.Ordinal)), Is.True);
            Assert.That(report.Owned.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
            Assert.That(report.Owned.Bytes, Is.GreaterThanOrEqualTo(300));
            Assert.That(report.Retained.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
            Assert.That(report.Files.Single(file => file.RelativePath.EndsWith(Path.GetFileName(activeArtifact))).Retention,
                Is.EqualTo(ResourceCacheRetention.ActiveVersion));
            var retainedUnknown = report.Files.Single(file => file.RelativePath.EndsWith(Path.GetFileName(previousArtifact)));
            Assert.That(retainedUnknown.OwnershipConfirmed, Is.True);
            Assert.That(retainedUnknown.Disposition, Is.EqualTo(ResourceCacheDisposition.Undetermined));
            Assert.That(report.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.Unknown));
            Assert.That(report.CleanupCandidates.Bytes, Is.Null);
            Assert.That(Snapshot(fixture.Root), Is.EqualTo(before));
        }

        [Test]
        public void ReportsDurableResumePairAndMalformedRecordDifferently()
        {
            using var fixture = new CacheFixture(Trust());
            Directory.CreateDirectory(fixture.Objects);
            string complete = fixture.Artifact(117);
            string digest = Path.GetFileNameWithoutExtension(complete);
            string stem = Path.Combine(fixture.Objects, ".resume-" + digest + ".bundle");
            File.Move(complete, stem + ".part");
            WriteResumeFixture(stem, digest, version: 1);
            var partial = fixture.Inspect();
            Assert.That(partial.Files.Single(file => file.RelativePath.EndsWith(".part", StringComparison.Ordinal))
                .OwnershipConfirmed, Is.False);

            WriteResumeFixture(stem, digest, version: 1, length: 117);
            string[] before = Snapshot(fixture.Root);
            var resumed = fixture.Inspect();
            Assert.That(resumed.Files.Count, Is.EqualTo(2));
            Assert.That(resumed.Files.All(file => file.Retention == ResourceCacheRetention.Resumable), Is.True);
            Assert.That(resumed.Retained.Bytes, Is.EqualTo(resumed.Owned.Bytes));
            Assert.That(resumed.CleanupCandidates.Bytes, Is.Zero);
            Assert.That(Snapshot(fixture.Root), Is.EqualTo(before));

            File.AppendAllText(stem + ".mark", "trailing");
            string orphan = Path.Combine(fixture.Objects, ".resume-" + new string('d', 64) + ".bundle.part");
            File.WriteAllBytes(orphan, new byte[29]);
            var unknown = fixture.Inspect();
            Assert.That(unknown.Files.Count, Is.EqualTo(3));
            Assert.That(unknown.Files.All(file => !file.OwnershipConfirmed &&
                file.Disposition == ResourceCacheDisposition.Undetermined), Is.True);
            Assert.That(unknown.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.Unknown));
        }

        [Test]
        public void CacheInspectionKeepsReplacedCheckpointDataOutsideCleanupCandidates()
        {
            using var fixture = new CacheFixture(Trust());
            Directory.CreateDirectory(fixture.Objects);
            string digest = new string('c', 64);
            string stem = Path.Combine(fixture.Objects, ".resume-" + digest + ".bundle");
            string part = stem + ".part";
            File.WriteAllBytes(part, new byte[117]);
            WriteResumeFixture(stem, digest, version: 2);

            Assert.That(fixture.Inspect().Files.All(file => file.Retention == ResourceCacheRetention.Resumable), Is.True);
            File.AppendAllText(part, "data ahead of record");
            var stale = fixture.Inspect();
            Assert.That(stale.Files.All(file => file.Disposition == ResourceCacheDisposition.CleanupCandidate), Is.True);
            Assert.That(stale.CleanupCandidates.Bytes, Is.EqualTo(stale.Owned.Bytes));

            // 旧实体保留在同一卷，确保新建文件无法复用它的文件 ID。
            File.Move(part, Path.Combine(fixture.Root, "original-prefix"));
            File.WriteAllBytes(part, new byte[117]);
            var replaced = fixture.Inspect();
            var data = replaced.Files.Single(file => file.RelativePath.EndsWith(".part", StringComparison.Ordinal));
            Assert.That(data.Bytes, Is.EqualTo(117));
            Assert.That(data.OwnershipConfirmed, Is.False);
            Assert.That(data.Disposition, Is.EqualTo(ResourceCacheDisposition.Undetermined));
            Assert.That(replaced.CleanupCandidates.Bytes,
                Is.EqualTo(replaced.Files.Single(file => file.RelativePath.EndsWith(".mark", StringComparison.Ordinal)).Bytes));
            Assert.That(replaced.CleanupCandidates.Certainty, Is.EqualTo(ResourceCacheCertainty.ObservedLowerBound));
        }

        private static void WriteResumeFixture(string stem, string digest, int version, long length = 243)
        {
            using var marker = new BinaryWriter(File.Create(stem + ".mark"), Encoding.UTF8);
            marker.Write(version);
            marker.Write("https://cdn.example.org/content/optional.bundle");
            marker.Write(digest);
            marker.Write(length);
            marker.Write(117L);
            marker.Write("\"fixture-tag\"");
            if (version == 2)
            {
                var part = ResourceContentFileIO.ReadFileStamp(stem + ".part");
                marker.Write(part.File);
                marker.Write(part.Volume);
                marker.Write(part.Created);
            }
        }

        private static void ChangeFixtureAccess(string path, string operation) =>
            RunFixtureCommand("icacls.exe", "\"" + path + "\" " + operation);

        [Test]
        public void VerifiedStagePublicationKeepsOldFileOnFailureAndMovesVerifiedIdentity()
        {
            using var fixture = new CacheFixture(Trust());
            string stage = fixture.Artifact(117);
            string destination = fixture.Artifact(243);
            string digest = Path.GetFileNameWithoutExtension(stage);
            var staged = ResourceContentFileIO.ReadFileStamp(stage);

            Assert.Throws<InvalidDataException>(() =>
                ResourceContentFileIO.VerifyAndPublishStage(stage, destination, 118, digest, default));
            Assert.That(File.Exists(stage), Is.True);
            Assert.That(new FileInfo(destination).Length, Is.EqualTo(243));
            using (ResourceContentFileIO.OpenRead(destination))
            {
                Assert.Throws<IOException>(() =>
                    ResourceContentFileIO.VerifyAndPublishStage(stage, destination, 117, digest, default));
                Assert.That(new FileInfo(destination).Length, Is.EqualTo(243));
            }

            ResourceContentFileIO.VerifyAndPublishStage(stage, destination, 117, digest, default);
            Assert.That(File.Exists(stage), Is.False);
            Assert.That(ResourceContentFileIO.ReadFileStamp(destination).File, Is.EqualTo(staged.File));
            Assert.That(new FileInfo(destination).Length, Is.EqualTo(117));
        }

        [Test]
        public void NewStagePublicationDoesNotReplaceAnotherMarker()
        {
            using var fixture = new CacheFixture(Trust());
            string stage = fixture.Artifact(117);
            string marker = fixture.Artifact(243);
            var owner = ResourceContentFileIO.ReadFileIdentity(stage);
            var original = ResourceContentFileIO.ReadFileStamp(marker);
            Assert.Throws<IOException>(() => ResourceContentFileIO.PublishNewStage(stage, marker, owner));
            Assert.That(ResourceContentFileIO.ReadFileStamp(marker), Is.EqualTo(original));
            Assert.That(ResourceContentFileIO.ReadFileIdentity(stage), Is.EqualTo(owner));

            string newMarker = marker + ".new";
            var published = ResourceContentFileIO.PublishNewStage(stage, newMarker, owner);
            Assert.That(published, Is.EqualTo(ResourceContentFileIO.ReadFileStamp(newMarker)));
            Assert.That(ResourceContentFileIO.ReadFileIdentity(newMarker), Is.EqualTo(owner));
        }

        private static void RunFixtureCommand(string application, string arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, application), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var child = System.Diagnostics.Process.Start(start);
            string output = child.StandardOutput.ReadToEnd();
            string error = child.StandardError.ReadToEnd();
            child.WaitForExit();
            Assert.That(child.ExitCode, Is.EqualTo(0), output + error);
        }

        private (ResourceContentStore.Version Version, string Announcement) MakeVersion(string shared, long sequence, ResourceContentManifestTrust trust)
        {
            string release = "test-" + sequence;
            var artifacts = new[]
            {
                new ResourceContentArtifact(CatalogRoute, release, "install:catalog.bin",
                    ResourceContentArtifactKind.Catalog, 40, CatalogSha256),
                new ResourceContentArtifact("optional.bundle", release, "https://cdn.example.org/content/optional.bundle",
                    ResourceContentArtifactKind.Bundle, new FileInfo(shared).Length, Path.GetFileNameWithoutExtension(shared))
            };
            var payload = ResourceContentManifestWriter.Serialize("app", "stable", "StandaloneWindows64", "base-a",
                release, sequence, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), SettingsSha256,
                CatalogRoute, null, artifacts, Array.Empty<string>());
            byte[] signature = ResourceContentManifestWriter.Sign(payload, "release", _signingKey);
            var manifest = ResourceContentManifest.Verify(payload, signature, trust);
            return (new ResourceContentStore.Version(sequence, manifest.Digest),
                "{\"payload\":\"" + Convert.ToBase64String(payload) + "\",\"signature\":\"" + Convert.ToBase64String(signature) + "\"}");
        }

        private static string[] Snapshot(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var stamp = ResourceContentFileIO.ReadFileStamp(path);
                return $"{path}:{stamp.File}:{stamp.Length}:{ResourceContentFileIO.Hash(file)}";
            }).ToArray();

        private sealed class CacheFixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "yl-cache-usage-" + Guid.NewGuid().ToString("N"));
            internal readonly ResourceContentStore Store;
            internal readonly ResourceContentUpdates Updates;
            internal string Objects => Path.Combine(Store.Root, "objects");

            internal CacheFixture(ResourceContentManifestTrust trust)
            {
                Store = new ResourceContentStore(Root, trust.Application, trust.Channel);
                Updates = new ResourceContentUpdates(trust, new Uri("https://cdn.example.org/content/announcement.json"), Root);
            }

            internal string Artifact(int length)
            {
                Directory.CreateDirectory(Objects);
                byte[] bytes = new byte[length];
                using var sha = SHA256.Create();
                string digest = ResourceContentFileIO.Hex(sha.ComputeHash(bytes));
                string path = Path.Combine(Objects, digest + ".bundle");
                File.WriteAllBytes(path, bytes);
                return path;
            }

            internal ResourceCacheUsageReport Inspect() => Updates.InspectCacheAsync().GetAwaiter().GetResult();

            public void Dispose()
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
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
