using System;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceSignatureTests
    {
        private static byte[] Envelope(RSA key, string keyId = "isolated-test")
        {
            var data = new JObject
            {
                ["SchemaVersion"] = 1,
                ["Algorithm"] = "RSA-SHA256-PKCS1",
                ["KeyId"] = keyId,
                ["Payload"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"Channel\":\"test\",\"ReleaseSequence\":7}"))
            };
            data["Signature"] = Convert.ToBase64String(key.SignData(ResourceCatalogCodec.CanonicalBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            return ResourceCatalogCodec.CanonicalBytes(data);
        }

        private static JObject Verify(byte[] bytes, RSAParameters trusted)
            => ResourceReleaseSignature.Verify(bytes, "isolated-test", trusted);

        [Test]
        public void ExplicitTrustedKeyAuthenticatesExactPayloadBytes()
        {
            using var key = new RSACryptoServiceProvider(2048);
            Assert.That(key.ExportParameters(false).Modulus.Length, Is.EqualTo(256));
            var payload = Verify(Envelope(key), key.ExportParameters(false));
            Assert.That((string)payload["Channel"], Is.EqualTo("test"));
            Assert.That((long)payload["ReleaseSequence"], Is.EqualTo(7));
        }

        [Test]
        public void WrongKeyTamperingUnknownKeyAndEnvelopeAmbiguityAreRejected()
        {
            using var key = new RSACryptoServiceProvider(2048);
            using var wrong = new RSACryptoServiceProvider(2048);
            var original = Envelope(key);
            Assert.Throws<InvalidDataException>(() => Verify(original, wrong.ExportParameters(false)));
            foreach (var field in new[] { "Payload", "KeyId", "Algorithm", "SchemaVersion", "Signature" })
            {
                var data = ResourceCatalogCodec.ParseObject(original);
                data[field] = field == "SchemaVersion" ? new JValue(2) : new JValue("tampered");
                Assert.Throws<InvalidDataException>(() => Verify(ResourceCatalogCodec.CanonicalBytes(data), key.ExportParameters(false)), field);
            }
            Assert.Throws<InvalidDataException>(() => Verify(Envelope(key, "unknown-key"), key.ExportParameters(false)));
            Assert.Throws<InvalidDataException>(() => Verify(System.Text.Encoding.UTF8.GetBytes(" " + System.Text.Encoding.UTF8.GetString(original)), key.ExportParameters(false)));
            Assert.Throws<InvalidDataException>(() => Verify(System.Text.Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"SchemaVersion\":2}"), key.ExportParameters(false)));
        }

        [Test]
        public void TrustAnchorRejectsInvalidOrDegeneratePublicExponents()
        {
            using var key = new RSACryptoServiceProvider(2048);
            var modulus = Convert.ToBase64String(key.ExportParameters(false).Modulus);
            foreach (var exponent in new[] { new byte[] { 1 }, new byte[] { 0 }, new byte[] { 2 }, new byte[] { 0, 1, 0, 1 }, new byte[] { 1, 0, 0 } })
            {
                Assert.Throws<ArgumentException>(() => new ResourceSigningKey("isolated-test", modulus, Convert.ToBase64String(exponent)));
            }
        }
    }
}
