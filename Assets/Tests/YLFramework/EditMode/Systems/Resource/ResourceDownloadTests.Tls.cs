using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using UnityEngine.TestTools;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceDownloadTests
    {
        internal static X509Certificate2 Certificate()
        {
            var path = Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PFX");
            Assert.That(path, Is.Not.Null.And.Not.Empty, "Supply an isolated loopback PFX through YL_RESOURCE_TEST_PFX; no system trust is modified.");
            return new X509Certificate2(path, Environment.GetEnvironmentVariable("YL_RESOURCE_TEST_PASSWORD"), X509KeyStorageFlags.EphemeralKeySet);
        }

        internal static ResourceRemoteSource Source(ResourceTlsServer server, X509Certificate2 certificate, bool trust = true, int attempts = 3, int timeout = 5)
        {
            using var rsa = certificate.GetRSAPublicKey();
            var key = rsa.ExportParameters(false);
            var source = new ResourceRemoteSource(server.BaseUri.AbsoluteUri, "test", new[] { new ResourceSigningKey("isolated-test", Convert.ToBase64String(key.Modulus), Convert.ToBase64String(key.Exponent)) },
                timeoutSeconds: timeout, idleTimeoutSeconds: Math.Min(2, timeout), attempts: attempts);
            if (trust)
            {
                source.TrustLoopbackCertificate(certificate.RawData);
            }

            return source;
        }

        private static IEnumerator Transfer(ResourceTransfer transfer, Action<Exception> failure = null)
        {
            using (transfer)
            {
                while (true)
                {
                    bool more;
                    try
                    {
                        more = transfer.MoveNext();
                    }
                    catch (Exception error)
                    {
                        if (failure == null)
                        {
                            throw;
                        }
                        failure(error);
                        yield break;
                    }
                    if (!more)
                    {
                        yield break;
                    }

                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator RealTlsRejectsUnknownCertificateAndExactLoopbackTrustDownloadsCompleteBytes()
        {
            using var certificate = Certificate();
            var body = System.Text.Encoding.UTF8.GetBytes("isolated TLS content");
            using var server = new ResourceTlsServer(certificate, _ => new ResourceTlsServer.Response { Body = body });
            var untrusted = Source(server, certificate, false, attempts: 1);
            Exception rejection = null;
            yield return Transfer(new ResourceTransfer(untrusted, untrusted.Resolve("release.json"), body.Length, ResourceCatalogCodec.Digest(body)), error => rejection = error);
            Assert.That(rejection, Is.TypeOf<IOException>(), "Default production validation must reject an unknown self-signed identity.");
            Assert.That(server.RequestCount, Is.Zero, "Certificate rejection must precede any HTTP content request.");
            var trusted = Source(server, certificate);
            using var download = new ResourceTransfer(trusted, trusted.Resolve("release.json"), body.Length, ResourceCatalogCodec.Digest(body));
            yield return Transfer(download);
            CollectionAssert.AreEqual(body, download.Bytes);
            Assert.That(server.RequestCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RealTlsRetriesOnlyFiniteTransientFailuresAndDoesNotFollowRedirects()
        {
            using var certificate = Certificate();
            using var server = new ResourceTlsServer(certificate);
            var source = Source(server, certificate);
            var body = new byte[] { 1, 2, 3, 4 };
            server.Responder = r => new ResourceTlsServer.Response { StatusCode = r.Number < 3 ? 503 : 200, Body = r.Number < 3 ? Array.Empty<byte>() : body };
            using (var download = new ResourceTransfer(source, source.Resolve("release.json"), body.Length, ResourceCatalogCodec.Digest(body)))
            {
                yield return Transfer(download);
                CollectionAssert.AreEqual(body, download.Bytes);
            }
            Assert.That(server.RequestCount, Is.EqualTo(3));
            server.Responder = _ => new ResourceTlsServer.Response { StatusCode = 302, Headers = { ["Location"] = "https://localhost:1/escaped" } };
            Exception rejection = null;
            yield return Transfer(new ResourceTransfer(source, source.Resolve("release.json")), e => rejection = e);
            Assert.That(rejection, Is.Not.Null);
            Assert.That(server.RequestCount, Is.EqualTo(4), "Redirects are neither followed nor retried.");
            server.Responder = _ => new ResourceTlsServer.Response { StatusCode = 429, Headers = { ["Retry-After"] = "0" } };
            rejection = null;
            yield return Transfer(new ResourceTransfer(source, source.Resolve("release.json")), e => rejection = e);
            Assert.That(rejection, Is.Not.Null);
            Assert.That(server.RequestCount, Is.EqualTo(7), "Attempts must be bounded even when Retry-After requests more time.");
        }

        [UnityTest]
        public IEnumerator RealTlsRejectsOversizeEncodedTruncatedAndWrongDigestBodies()
        {
            using var certificate = Certificate();
            using var server = new ResourceTlsServer(certificate);
            var source = Source(server, certificate, attempts: 1);
            foreach (var mode in new[] { "oversize", "encoded", "truncated", "digest", "404" })
            {
                server.Responder = _ => new ResourceTlsServer.Response
                {
                    Body = Enumerable.Repeat((byte)5, mode == "oversize" ? 4096 : 1024).ToArray(),
                    StatusCode = mode == "404" ? 404 : 200,
                    DisconnectAfterBytes = mode == "truncated" ? 512 : null,
                    Headers = mode == "encoded" ? new System.Collections.Generic.Dictionary<string, string> { ["Content-Encoding"] = "gzip" } : new System.Collections.Generic.Dictionary<string, string>()
                };
                Exception rejection = null;
                yield return Transfer(new ResourceTransfer(source, source.Resolve("release.json"), 1024, ResourceCatalogCodec.Digest(new byte[1024])), e => rejection = e);
                Assert.That(rejection, Is.Not.Null, mode);
            }
            Assert.That(server.RequestCount, Is.EqualTo(5));
        }

        [UnityTest]
        public IEnumerator RealTlsStallTimesOutAndExhaustsOnlyTheConfiguredAttempts()
        {
            using var certificate = Certificate();
            using var server = new ResourceTlsServer(certificate, _ => new ResourceTlsServer.Response { Body = new byte[1024], StallAfterBytes = 0 });
            var source = Source(server, certificate, attempts: 2, timeout: 1);
            Exception failure = null;
            yield return Transfer(new ResourceTransfer(source, source.Resolve("release.json"), 1024, ResourceCatalogCodec.Digest(new byte[1024])), error => failure = error);
            Assert.That(failure, Is.TypeOf<IOException>());
            Assert.That(server.RequestCount, Is.EqualTo(2), "Stalls must not produce unbounded retries.");
        }

        [Test]
        public void EndpointAndPathsCannotSelectHttpCredentialsEscapesOrAnotherOrigin()
        {
            using var certificate = Certificate();
            using var server = new ResourceTlsServer(certificate);
            var source = Source(server, certificate);
            foreach (var path in new[] { "../evil", "a/../evil", "https://evil.test/file", "//evil.test/file", "a\\b", "a%2fb", "/root", "a?token=x", "a#fragment" })
            {
                Assert.Throws<InvalidDataException>(() => source.Resolve(path), path);
            }

            using var rsa = certificate.GetRSAPublicKey();
            var key = rsa.ExportParameters(false);
            foreach (var url in new[] { "http://localhost/", "https://user:password@localhost/", "https://localhost/a/../", "https://localhost/%2e/", "https://localhost/?secret=1" })
            {
                Assert.Throws<ArgumentException>(() => new ResourceRemoteSource(url, "test", new[] { new ResourceSigningKey("test", Convert.ToBase64String(key.Modulus), Convert.ToBase64String(key.Exponent)) }), url);
            }
        }
    }
}
