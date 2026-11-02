using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceContentTests
    {
        private static readonly byte[] _testKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        [Test]
        public void InspectAcceptsOnlyUnityFsAsNoneAndRestoresPosition()
        {
            using var raw = new MemoryStream(Encoding.ASCII.GetBytes("UnityFS\0fixture"));
            raw.Position = 3;
            Assert.That(Invoke("Inspect", raw, Context()), Is.Null);
            Assert.That(raw.Position, Is.EqualTo(3));
            foreach (var value in new[] { "", "UnityFS", "UnityFS!", "YLRENC0", "YLRENC02", "garbage!" })
            {
                Assert.Throws<InvalidDataException>(() => Invoke("Inspect", new MemoryStream(Encoding.ASCII.GetBytes(value)), Context()));
            }
        }

        [Test]
        public void InspectRejectsNoncanonicalUnknownDuplicateAndOverflowHeaders()
        {
            var canonical = HeaderJson();
            using var valid = EnvelopeBytes(canonical);
            var header = Invoke("Inspect", valid, Context());
            Assert.That(Get(header, "PlainLength"), Is.EqualTo(0L));
            Assert.That(Get(header, "KeyId"), Is.EqualTo("test-key"));
            foreach (var text in new[] {
                         " " + canonical,
                         canonical.Replace("\"Version\":1", "\"Version\":1,\"Extra\":0"),
                         canonical.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"),
                         canonical.Replace("\"Version\":1", "\"Version\":1.0"),
                         canonical.Replace("\"Version\":1", "\"Version\":2"),
                         canonical.Replace("65536", "1048576"),
                         canonical.Replace("\"PlainLength\":0", "\"PlainLength\":-1"),
                         canonical.Replace("\"PlainLength\":0", "\"PlainLength\":9223372036854775808"),
                         canonical.Replace("\"PlainLength\":0", "\"PlainLength\":9223372036854775807"),
                         canonical.Replace("test-key", "bad key"),
                         canonical.Replace("Bundle", "Unknown"),
                         canonical.Replace("\"PlainSha256\":\"e3", "\"PlainSha256\":\"E3"),
                         canonical.Replace("\"ProjectId\":\"fixture\"", "\"ProjectId\":\"other\""),
                         canonical.Replace("\"Version\":1", "\"Version\":1/*comment*/"),
                         canonical + "{}" })
            {
                Assert.Throws<InvalidDataException>(() => Invoke("Inspect", EnvelopeBytes(text), Context()), text);
            }

            var badLength = EnvelopeBytes(canonical).ToArray();
            badLength[8] = 1;
            badLength[9] = 16;
            Assert.Throws<InvalidDataException>(() => Invoke("Inspect", new MemoryStream(badLength), Context()));
            var invalidUtf8 = EnvelopeBytes(canonical).ToArray();
            invalidUtf8[12] = 0xff;
            Assert.Throws<InvalidDataException>(() => Invoke("Inspect", new MemoryStream(invalidUtf8), Context()));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(65479)]
        [TestCase(65480)]
        [TestCase(65481)]
        [TestCase(131000)]
        [TestCase(131001)]
        [TestCase(262123)]
        public void EncryptedViewAuthenticatesBoundariesAndRandomSeeks(int count)
        {
            RequireBackend();
            var raw = Data(count);
            var cipher = Encrypt(raw);
            using var input = new MemoryStream(cipher);
            using var stream = Open(input);
            Assert.That(stream.Length, Is.EqualTo(count));
            Assert.That(Get(stream, "CngKeyObjectBytes"), Is.GreaterThan(0));
            var random = new Random(831);
            var buffer = new byte[4001];
            for (int i = 0; i < 100; ++i)
            {
                int position = random.Next(count + 2);
                stream.Position = position;
                int got = stream.Read(buffer, 0, buffer.Length);
                int expected = Math.Min(buffer.Length, Math.Max(0, count - position));
                Assert.That(got, Is.EqualTo(expected));
                CollectionAssert.AreEqual(raw.Skip(position).Take(got), buffer.Take(got));
            }
            Invoke("ValidateAll", input, Context(), _testKey, CancellationToken.None, null, null);
            Assert.That(input.CanRead, Is.True);
        }

        [Test]
        public void DifferentEncryptionsAreRandomizedAndContextIsAuthenticated()
        {
            RequireBackend();
            var raw = Data(200000);
            var first = Encrypt(raw);
            Assert.That(first.SequenceEqual(Encrypt(raw)), Is.False);
            var key = (byte[])_testKey.Clone();
            key[0] ^= 1;
            Assert.Throws<IOException>(() => Open(new MemoryStream(first), key));
            foreach (var context in new[] { Context(project: "other"), Context(logical: "another_bundle"),
                         Context(platform: "StandaloneLinux64"), Context(unity: "other"), Context(role: "Manifest", logical: "manifest") })
            {
                Assert.Throws<InvalidDataException>(() => Invoke("Inspect", new MemoryStream(first), context));
            }
            // A canonical header edit is not merely a parser mismatch: HKDF binds its exact bytes.
            var modified = (byte[])first.Clone();
            var marker = Encoding.ASCII.GetBytes("test-key");
            int start = Find(modified, marker);
            modified[start] = (byte)'b';
            Assert.Throws<IOException>(() => Open(new MemoryStream(modified)));
        }

        [Test]
        public void BadMiddleSegmentNeverCopiesAndFaultCannotBecomeEof()
        {
            RequireBackend();
            var cipher = Encrypt(Data(300000));
            int payload = 12 + HeaderLength(cipher);
            cipher[payload + 65536 + 20] ^= 1;
            using var input = new MemoryStream(cipher);
            using var stream = Open(input);
            stream.Position = 65480;
            var target = Enumerable.Repeat((byte)0xa7, 100).ToArray();
            Assert.Throws<IOException>(() => stream.Read(target, 0, target.Length));
            Assert.That(target.All(value => value == 0xa7), Is.True);
            Assert.That(Get(stream, "Fault"), Is.Not.Null);
            Assert.Throws<IOException>(() => stream.Seek(0, SeekOrigin.End));
            Assert.Throws<IOException>(() => stream.Read(target, 0, 0));
            Assert.Throws<IOException>(() => stream.ReadByte());
            Assert.That(stream.CanRead, Is.False);
        }

        [Test]
        public void CrossBadSegmentOnlyCopiesTheAuthenticatedPrefixBeforeThrowing()
        {
            RequireBackend();
            var raw = Data(300000);
            var cipher = Encrypt(raw);
            cipher[12 + HeaderLength(cipher) + 65536 + 30] ^= 1;
            using var stream = Open(new MemoryStream(cipher));
            stream.Position = 65470;
            var target = Enumerable.Repeat((byte)0xa7, 30).ToArray();
            Assert.Throws<IOException>(() => stream.Read(target, 0, target.Length));
            CollectionAssert.AreEqual(raw.Skip(65470).Take(10), target.Take(10));
            Assert.That(target.Skip(10).All(value => value == 0xa7), Is.True);
        }

        [Test]
        public void FirstLastAndEmptyAuthenticationCannotBeSkipped()
        {
            RequireBackend();
            foreach (var size in new[] { 0, 1, 200000 })
            {
                var cipher = Encrypt(Data(size));
                var corrupted = (byte[])cipher.Clone();
                corrupted[corrupted.Length - 1] ^= 1;
                using var input = new MemoryStream(corrupted);
                Assert.Throws<IOException>(() => Open(input));
                Assert.That(input.CanRead, Is.True, "A failed open must not take the caller's stream.");
                var extra = new byte[cipher.Length + 1];
                Buffer.BlockCopy(cipher, 0, extra, 0, cipher.Length);
                Assert.Throws<InvalidDataException>(() => Open(new MemoryStream(extra)));
                Assert.Throws<InvalidDataException>(() => Open(new MemoryStream(cipher, 0, cipher.Length - 1)));
            }
        }

        [Test]
        public void ValidateAllChecksAuthenticatedPlainDigestAndIndependentBuildIdentity()
        {
            RequireBackend();
            var cipher = Encrypt(Data(200000));
            using var input = new MemoryStream(cipher);
            Assert.Throws<InvalidDataException>(() => Invoke("ValidateAll", input, Context(), _testKey,
                CancellationToken.None, new string('0', 64), 200000L));
            Assert.Throws<InvalidDataException>(() => Invoke("ValidateAll", input, Context(), _testKey,
                CancellationToken.None, null, 200001L));
            cipher[12 + HeaderLength(cipher) + 65536 + 20] ^= 1;
            Assert.Throws<IOException>(() => Invoke("ValidateAll", new MemoryStream(cipher), Context(), _testKey,
                CancellationToken.None, null, null));
        }

        [Test]
        public void SeekOverflowArgumentErrorsAndDisposeDoNotCorruptHealthyState()
        {
            RequireBackend();
            var input = new MemoryStream(Encrypt(Data(100)));
            var stream = Open(input, leaveOpen: true);
            Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
            Assert.Throws<ArgumentException>(() => stream.Seek(0, (SeekOrigin)9));
            stream.Position = long.MaxValue;
            Assert.Throws<IOException>(() => stream.Seek(1, SeekOrigin.Current));
            Assert.That(stream.Read(new byte[1], 0, 1), Is.Zero);
            stream.Position = 99;
            Assert.That(stream.Read(new byte[20], 0, 20), Is.EqualTo(1));
            Assert.That(stream.ReadByte(), Is.EqualTo(-1));
            Assert.Throws<ArgumentException>(() => stream.Read(new byte[1], 1, int.MaxValue));
            stream.Position = 0;
            Assert.That(stream.ReadByte(), Is.EqualTo(Data(1)[0]));
            stream.Dispose();
            Assert.That(input.CanRead, Is.True);
            Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
            Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
            stream.Dispose();
        }

        [Test]
        public void IndependentInputsCanReadConcurrentlyAndDisposeSerializesWithRead()
        {
            RequireBackend();
            var raw = Data(150000);
            var cipher = Encrypt(raw);
            Parallel.For(0, 8, _ =>
            {
                for (int iteration = 0; iteration < 8; ++iteration)
                {
                    using var stream = Open(new MemoryStream(cipher));
                    var output = new byte[raw.Length];
                    Assert.That(stream.Read(output, 0, output.Length), Is.EqualTo(output.Length));
                    CollectionAssert.AreEqual(raw, output);
                }
            });
            using var concurrent = Open(new MemoryStream(cipher));
            var reader = Task.Run(() =>
            {
                for (int i = 0; i < 100; ++i)
                {
                    try
                    {
                        concurrent.ReadByte();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            });
            concurrent.Dispose();
            reader.GetAwaiter().GetResult();
        }

        [Test]
        public void CancellationDoesNotTakeCallerOwnershipOrEmitPayload()
        {
            RequireBackend();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            using var raw = new MemoryStream(Data(100));
            using var output = new MemoryStream();
            Assert.Throws<OperationCanceledException>(() => Invoke("Encrypt", raw, output, Context(), "test-key", _testKey, canceled.Token));
            Assert.That(output.Length, Is.Zero);
            using var input = new MemoryStream(Encrypt(Data(100)));
            Assert.Throws<OperationCanceledException>(() => Invoke("OpenDecrypt", input, Context(), _testKey, false, canceled.Token));
            Assert.That(input.CanRead, Is.True);
        }

        private static Type ContentType(string name)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly =>
                assembly.GetType("YLFramework.Resource." + name)).FirstOrDefault(value => value != null);
            Assert.That(type, Is.Not.Null, "Missing formal content contract: " + name);
            return type;
        }

        private static object Invoke(string method, params object[] arguments)
        {
            try
            {
                return ContentType("ResourceBundleEnvelope").GetMethod(method, Members).Invoke(null, arguments);
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException;
            }
        }

        private static object Get(object instance, string property) => instance.GetType().GetProperty(property, Members).GetValue(instance);

        private static object Context(string project = "fixture", string platform = "StandaloneWindows64", string unity = "2022.3",
            string role = "Bundle", string logical = "ylr_fixture_bundle") => Activator.CreateInstance(ContentType("ResourceBundleContext"),
            Members, null, new object[] { project, platform, unity, role, logical }, null);

        private static byte[] Encrypt(byte[] raw)
        {
            using var input = new MemoryStream(raw);
            using var output = new MemoryStream();
            Invoke("Encrypt", input, output, Context(), "test-key", _testKey, CancellationToken.None);
            return output.ToArray();
        }

        private static Stream Open(Stream input, byte[] key = null, bool leaveOpen = false) =>
            (Stream)Invoke("OpenDecrypt", input, Context(), key ?? _testKey, leaveOpen, CancellationToken.None);

        private static void RequireBackend()
        {
            if (!(bool)ContentType("ResourceBundleEnvelope").GetProperty("IsSupported", Members).GetValue(null))
            {
                Assert.Ignore("Encryption is intentionally limited to Windows x64 Editor/Mono.");
            }
        }

        private static byte[] Data(int length)
        {
            var bytes = new byte[length];
            new Random(19).NextBytes(bytes);
            return bytes;
        }

        private static int Find(byte[] bytes, byte[] pattern)
        {
            for (int offset = 0; offset <= bytes.Length - pattern.Length; ++offset)
            {
                if (pattern.Select((value, index) => bytes[offset + index] == value).All(value => value))
                {
                    return offset;
                }
            }

            throw new InvalidOperationException("Fixture marker missing.");
        }

        private static int HeaderLength(byte[] cipher) => cipher[8] | cipher[9] << 8 | cipher[10] << 16 | cipher[11] << 24;

        private static string HeaderJson() => "{\"Algorithm\":\"AES256-GCM-HKDF-SEGMENTED\",\"CiphertextSegmentSize\":65536," +
            "\"FileRole\":\"Bundle\",\"KeyId\":\"test-key\",\"LogicalId\":\"ylr_fixture_bundle\",\"PlainLength\":0," +
            "\"PlainSha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\"," +
            "\"Platform\":\"StandaloneWindows64\",\"ProjectId\":\"fixture\",\"UnityCompatibility\":\"2022.3\",\"Version\":1}";

        private static MemoryStream EnvelopeBytes(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes("YLRENC01"), 0, 8);
            output.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            output.Write(bytes, 0, bytes.Length);
            output.Write(new byte[56], 0, 56);
            output.Position = 0;
            return output;
        }
    }
}
