using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceDownloadTests
    {
        private static long Response(long status, long offset, long length, string validator, Dictionary<string, string> headers)
            => ResourceDownloadResponse.Offset(status, offset, length, validator, name => headers.TryGetValue(name, out var value) ? value : null);

        [Test]
        public void PartialResponsesMustBindRangeTotalLengthAndStrongValidatorBeforeAppending()
        {
            var headers = new Dictionary<string, string> { ["Content-Range"] = "bytes 4-9/10", ["Content-Length"] = "6", ["ETag"] = "\"v1\"" };
            Assert.That(Response(206, 4, 10, "\"v1\"", headers), Is.EqualTo(4));
            foreach (var change in new[] { ("Content-Range", "bytes 0-5/10"), ("Content-Range", "bytes 4-9/11"),
                ("Content-Range", "bytes 4-8/10"), ("Content-Length", "5"), ("ETag", "\"v2\""), ("ETag", "W/\"v1\""), ("Content-Encoding", "gzip") })
            {
                var invalid = new Dictionary<string, string>(headers) { [change.Item1] = change.Item2 };
                if (change.Item1 == "Content-Length" || change.Item1 == "Content-Encoding")
                {
                    Assert.Throws<InvalidDataException>(() => Response(206, 4, 10, "\"v1\"", invalid), change.ToString());
                }
                else
                {
                    Assert.Throws<ResourceRangeException>(() => Response(206, 4, 10, "\"v1\"", invalid), change.ToString());
                }
            }
            Assert.Catch<IOException>(() => Response(206, 4, 10, null, headers));
            Assert.Catch<IOException>(() => Response(206, 0, 10, null, headers));
        }

        [Test]
        public void FullResponseRestartsAnd416NeverMeansSuccess()
        {
            Assert.That(Response(200, 4, 10, "\"v1\"", new Dictionary<string, string> { ["Content-Length"] = "10" }), Is.Zero);
            Assert.Throws<InvalidDataException>(() => Response(200, 0, 10, null, new Dictionary<string, string> { ["Content-Length"] = "11" }));
            Assert.Catch<IOException>(() => Response(416, 10, 10, "\"v1\"", new Dictionary<string, string> { ["Content-Range"] = "bytes */10" }));
            Assert.Catch<IOException>(() => Response(302, 0, 10, null, new Dictionary<string, string>()));
        }
    }
}
