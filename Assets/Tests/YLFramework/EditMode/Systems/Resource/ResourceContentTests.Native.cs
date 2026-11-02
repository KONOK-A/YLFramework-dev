using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using NUnit.Framework;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceContentTests
    {
        [TestCase("2022.3.62f3c1", true)]
        [TestCase("6000.3.27f1", true)]
        [TestCase("2022.3.62f1", false)]
        [TestCase("6000.3.27f2", false)]
        [TestCase("6000.3.28f1", false)]
        [TestCase("6000.3.27f1-beta", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void Il2CppPlayerVersionGateAcceptsOnlyExactCandidateVersions(string version, bool expected)
        {
            var method = ContentType("CngSegmentAead").GetMethod("IsAcceptedPlayerVersion", Members);
            Assert.That(method, Is.Not.Null, "The native gate needs a directly testable exact-version decision.");
            Assert.That((bool)method.Invoke(null, new object[] { version }), Is.EqualTo(expected));
        }

        [TestCase("2022.3.62f3c1", true)]
        [TestCase("6000.3.27f1", true)]
        [TestCase("2022.3.62f1", false)]
        [TestCase("6000.3.27f2", false)]
        [TestCase("6000.3.28f1", false)]
        [TestCase("6000.3.27f1-beta", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void RemotePlayerVersionGateAcceptsOnlyExactCandidateVersions(string version, bool expected)
        {
            var method = typeof(ResourceRemoteSource).GetMethod("IsAcceptedPlayerVersion", Members);
            Assert.That(method, Is.Not.Null);
            Assert.That((bool)method.Invoke(null, new object[] { version }), Is.EqualTo(expected));
        }

        [Test]
        public void OptionsFreezeProviderWithoutCallingOrOwningIt()
        {
            var provider = new TestProvider();
            var options = new ResourceDecryptionOptions(provider);
            Assert.That(options.KeyProvider, Is.SameAs(provider));
            Assert.That(options.MaxBufferedBytes, Is.EqualTo(134217728L));
            Assert.That(provider.Calls, Is.Zero);
            Assert.Throws<ArgumentNullException>(() => new ResourceDecryptionOptions(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceDecryptionOptions(provider, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceDecryptionOptions(provider, -1));
        }

        [TestCase(0, "530f8afbc74536b9a963b4f1c4cb738b")]
        [TestCase(16, "cea7403d4d606b6e074ec5d3baf39d18d0d1c8a799996bf0265b98b5d48ab919")]
        public void CngAuthenticatesIndependentAes256GcmVectors(int length, string expectedHex)
        {
            RequireBackend();
            using var owner = (IDisposable)Activator.CreateInstance(ContentType("CngSegmentAead"), Members, null,
                new object[] { new byte[32] }, null);
            var cipher = new byte[length + 16];
            Assert.That(Call(owner, "Encrypt", new byte[length], length, new byte[12], cipher), Is.EqualTo(cipher.Length));
            Assert.That(BitConverter.ToString(cipher).Replace("-", "").ToLowerInvariant(), Is.EqualTo(expectedHex));
            var plaintext = new byte[Math.Max(1, length)];
            Assert.That(Call(owner, "Decrypt", cipher, cipher.Length, new byte[12], plaintext), Is.EqualTo(length));
            Assert.That(plaintext.All(value => value == 0), Is.True);
            cipher[cipher.Length - 1] ^= 1;
            Assert.Throws<CryptographicException>(() => Call(owner, "Decrypt", cipher, cipher.Length, new byte[12], plaintext));
            Assert.That(plaintext.All(value => value == 0), Is.True);
        }

        [Test]
        public void FirstFaultNotifiesTheOwningGraphOnlyOnce()
        {
            RequireBackend();
            var cipher = Encrypt(Data(300000));
            cipher[12 + HeaderLength(cipher) + 65536 + 20] ^= 1;
            using var stream = Open(new MemoryStream(cipher));
            var install = stream.GetType().GetMethod("SetFaultNotification", Members);
            Assert.That(install, Is.Not.Null, "The native callback must enqueue the first observed fault without polling all inputs.");
            int notifications = 0;
            Exception notified = null;
            install.Invoke(stream, new object[] { new Action<Exception>(error =>
            {
            ++notifications;
            notified = error;
            }) });
            stream.Position = 65480;
            Assert.Throws<IOException>(() => stream.ReadByte());
            Assert.Throws<IOException>(() => stream.ReadByte());
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(notified, Is.SameAs(Get(stream, "Fault")));
        }

        [Test]
        public void ShortReadsWorkAndNonIoFailureOrChangedLengthBecomesSticky()
        {
            RequireBackend();
            var raw = Data(300000);
            using var source = new IrregularInput(Encrypt(raw));
            using var stream = Open(source, leaveOpen: true);
            stream.Position = 65480;
            var buffer = new byte[31];
            Assert.That(stream.Read(buffer, 0, buffer.Length), Is.EqualTo(buffer.Length));
            CollectionAssert.AreEqual(raw.Skip(65480).Take(31), buffer);
            // A different uncached segment forces a physical read after first/last preauthentication.
            stream.Position = 131000;
            source.ThrowOnRead = true;
            var failure = Assert.Throws<IOException>(() => stream.ReadByte());
            Assert.That(failure.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.Throws<IOException>(() => stream.Read(new byte[1], 0, 0));
            using var changed = new IrregularInput(Encrypt(raw));
            using var second = Open(changed, leaveOpen: true);
            changed.LengthDelta = 1;
            Assert.Throws<IOException>(() => second.ReadByte());
            Assert.That(Get(second, "Fault"), Is.Not.Null);
        }

        [Test]
        public void InputKeepsStreamAndTicketOwnerWhenDisposeFailsUntilExplicitRetry()
        {
            RequireBackend();
            string root = Path.Combine(Path.GetTempPath(), "e04-content-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(root));
            string path = Path.Combine(root, "cipher.enc");
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), Encrypt(Data(200000)));
            var file = new FailOnceFileStream(path);
            var ticket = new TestTicket();
            IDisposable input = null;
            try
            {
                input = (IDisposable)ContentType("ResourceBundleInput").GetMethod("Open", Members).Invoke(null,
                    new object[] { file, Context(), _testKey, ticket, CancellationToken.None });
                Assert.Throws<IOException>(() => input.Dispose());
                Assert.That(Get(input, "IsDisposed"), Is.False);
                Assert.That(Get(input, "ReleaseFault"), Is.Not.Null);
                Assert.That(ticket.Disposals, Is.Zero);
                Assert.That(file.CanRead, Is.True);
                input.Dispose();
                Assert.That(Get(input, "IsDisposed"), Is.True);
                Assert.That(ticket.Disposals, Is.EqualTo(1));
                input.Dispose();
                Assert.That(ticket.Disposals, Is.EqualTo(1));
            }
            finally
            {
                input?.Dispose();
                file.Dispose();
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
            }
        }

        [Test]
        public void InputOpenFailureDoesNotTakeTheFileOrReservation()
        {
            RequireBackend();
            string root = Path.Combine(Path.GetTempPath(), "e04-content-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(root));
            string path = Path.Combine(root, "cipher.enc");
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), Encrypt(Data(200000)));
            try
            {
                using var file = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                var ticket = new TestTicket();
                var key = (byte[])_testKey.Clone();
                key[0] ^= 1;
                Assert.That(Assert.Throws<TargetInvocationException>(() => ContentType("ResourceBundleInput").GetMethod("Open", Members)
                    .Invoke(null, new object[] { file, Context(), key, ticket, CancellationToken.None })).InnerException, Is.TypeOf<IOException>());
                Assert.That(file.CanRead, Is.True);
                Assert.That(ticket.Disposals, Is.Zero);
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
            }
        }

        [Test]
        public void RepeatedFailedOpensReleaseEveryNewNativeOwnerWithoutGc()
        {
            RequireBackend();
            var native = ContentType("CngSegmentAead");
            long algorithms = (long)native.GetProperty("ActiveAlgorithms", Members).GetValue(null);
            long keys = (long)native.GetProperty("ActiveKeys", Members).GetValue(null);
            long bytes = (long)native.GetProperty("ActiveKeyObjectBytes", Members).GetValue(null);
            var cipher = Encrypt(Data(200000));
            cipher[cipher.Length - 1] ^= 1;
            for (int i = 0; i < 30; ++i)
            {
                using var input = new MemoryStream(cipher);
                Assert.Throws<IOException>(() => Open(input));
                Assert.That(input.CanRead, Is.True);
            }
            Assert.That(native.GetProperty("ActiveAlgorithms", Members).GetValue(null), Is.EqualTo(algorithms));
            Assert.That(native.GetProperty("ActiveKeys", Members).GetValue(null), Is.EqualTo(keys));
            Assert.That(native.GetProperty("ActiveKeyObjectBytes", Members).GetValue(null), Is.EqualTo(bytes));
        }

        [Test]
        public void UnsupportedBackendDoesNotImportCngOrFallBackToPlaintext()
        {
            if ((bool)ContentType("ResourceBundleEnvelope").GetProperty("IsSupported", Members).GetValue(null))
            {
                Assert.Ignore("This assertion targets an intentionally unsupported compilation configuration.");
            }

            Assert.That(ContentType("CngSegmentAead").GetMethods(Members).Any(method =>
                method.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false).Length != 0), Is.False);
            Assert.Throws<PlatformNotSupportedException>(() => Invoke("OpenDecrypt", EnvelopeBytes(HeaderJson()), Context(),
                _testKey, false, CancellationToken.None));
            Assert.That(Invoke("Inspect", new MemoryStream(System.Text.Encoding.ASCII.GetBytes("UnityFS\0fixture")), Context()), Is.Null);
        }

        // Official Tink Maven 1.24.0 / Java 23.0.1 output, generated with public test key bytes 0..31.
        // Outer canonical bytes are bound as "YL.Resource.BundleEnvelope/v1" || complete prefix.
        [TestCase(0, "WUxSRU5DMDFRAQAAeyJBbGdvcml0aG0iOiJBRVMyNTYtR0NNLUhLREYtU0VHTUVOVEVEIiwiQ2lwaGVydGV4dFNlZ21lbnRTaXplIjo2NTUzNiwiRmlsZVJvbGUiOiJCdW5kbGUiLCJLZXlJZCI6InRlc3Qta2V5IiwiTG9naWNhbElkIjoieWxyX2ZpeHR1cmVfYnVuZGxlIiwiUGxhaW5MZW5ndGgiOjAsIlBsYWluU2hhMjU2IjoiZTNiMGM0NDI5OGZjMWMxNDlhZmJmNGM4OTk2ZmI5MjQyN2FlNDFlNDY0OWI5MzRjYTQ5NTk5MWI3ODUyYjg1NSIsIlBsYXRmb3JtIjoiU3RhbmRhbG9uZVdpbmRvd3M2NCIsIlByb2plY3RJZCI6ImZpeHR1cmUiLCJVbml0eUNvbXBhdGliaWxpdHkiOiIyMDIyLjMiLCJWZXJzaW9uIjoxfSi14uMevR1y0SVevjWCSm3t9BFGOmYh8PgOBJ4WHzdbhQ79wlkb0ZVT91xNNQbeTm+qrKvryQsh")]
        [TestCase(1, "WUxSRU5DMDFRAQAAeyJBbGdvcml0aG0iOiJBRVMyNTYtR0NNLUhLREYtU0VHTUVOVEVEIiwiQ2lwaGVydGV4dFNlZ21lbnRTaXplIjo2NTUzNiwiRmlsZVJvbGUiOiJCdW5kbGUiLCJLZXlJZCI6InRlc3Qta2V5IiwiTG9naWNhbElkIjoieWxyX2ZpeHR1cmVfYnVuZGxlIiwiUGxhaW5MZW5ndGgiOjEsIlBsYWluU2hhMjU2IjoiNjkyMmU5M2UzODI3NjQyY2U0Yjg4M2M3NTZiMzFhYmY4MDAzNjY0OWQzNjE0YmY1ZmNiM2FkZGE0M2I4ZWEzMiIsIlBsYXRmb3JtIjoiU3RhbmRhbG9uZVdpbmRvd3M2NCIsIlByb2plY3RJZCI6ImZpeHR1cmUiLCJVbml0eUNvbXBhdGliaWxpdHkiOiIyMDIyLjMiLCJWZXJzaW9uIjoxfSh4bKfT8xyKQilpKJOGaKNhG0iMHv+fsyzOpk+zqfDAENKXq0K0WIrIoe7YRoYr0kgDuYA9Pd2ZFw==")]
        public void OfficialJavaRawFixtureIsConsumedByTheFormalEnvelope(int length, string base64)
        {
            RequireBackend();
            using var input = new MemoryStream(Convert.FromBase64String(base64));
            Invoke("ValidateAll", input, Context(), _testKey, CancellationToken.None, null, (long)length);
            using var stream = Open(input);
            Assert.That(stream.Length, Is.EqualTo(length));
            if (length == 1)
            {
                Assert.That(stream.ReadByte(), Is.EqualTo(0xa5));
            }

            Assert.That(stream.ReadByte(), Is.EqualTo(-1));
        }

        private static object Call(object target, string method, params object[] arguments)
        {
            try
            {
                return target.GetType().GetMethod(method, Members).Invoke(target, arguments);
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException;
            }
        }

        private sealed class TestProvider : IResourceContentKeyProvider
        {
            internal int Calls;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                ++Calls;
                Array.Copy(_testKey, destination, _testKey.Length);
                return true;
            }
        }

        private sealed class TestTicket : IDisposable
        {
            internal int Disposals;
            public void Dispose() => ++Disposals;
        }

        private sealed class IrregularInput : MemoryStream
        {
            internal bool ThrowOnRead;
            internal long LengthDelta;
            internal IrregularInput(byte[] bytes) : base(bytes) { }
            public override long Length => base.Length + LengthDelta;
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (ThrowOnRead)
                {
                    throw new InvalidOperationException("Owned fixture read failure.");
                }

                return base.Read(buffer, offset, Math.Min(count, 3));
            }
        }

        private sealed class FailOnceFileStream : FileStream
        {
            private bool _failNextWrite = true;
            internal FailOnceFileStream(string path) : base(path, FileMode.Open, FileAccess.Read, FileShare.Read) { }
            protected override void Dispose(bool disposing)
            {
                if (disposing && _failNextWrite)
                {
                    _failNextWrite = false;
                    throw new IOException("Owned fixture dispose failure.");
                }
                base.Dispose(disposing);
            }
        }
    }
}
