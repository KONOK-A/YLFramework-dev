using System;
using System.IO;
using dnlib.DotNet;
using NUnit.Framework;
using YLFramework.Integrations.HybridCLR;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRMetadataTests
    {
        [TestCase(dnlib.PE.Machine.I386)]
        [TestCase(dnlib.PE.Machine.AMD64)]
        public void ReadsTheRealMvidFromManagedPeBytesWithoutClrModuleReflection(dnlib.PE.Machine machine)
        {
            var bytes = Create(machine, out var expected);
            Assert.That(HybridCLRMetadata.ReadMvid(bytes), Is.EqualTo(expected));
        }

        [TestCase("short")]
        [TestCase("dos")]
        [TestCase("pe-offset")]
        [TestCase("truncated")]
        public void InvalidOrOutOfRangeMetadataIsRejectedBeforeNativeCalls(string corruption)
        {
            var bytes = Create(dnlib.PE.Machine.I386, out _);
            switch (corruption)
            {
                case "short":
                    bytes = new byte[] { 1, 2, 3 };
                    break;
                case "dos":
                    bytes[0] = 0;
                    break;
                case "pe-offset":
                    for (var i = 0x3c; i < 0x40; i++)
                    {
                        bytes[i] = 0xff;
                    }
                    break;
                default:
                    Array.Resize(ref bytes, 150);
                    break;
            }
            Assert.Throws<InvalidDataException>(() => HybridCLRMetadata.ReadMvid(bytes));
        }

        [Test]
        public void MutatedAndTruncatedPeHeadersNeverEscapeBoundsAsClrIndexErrors()
        {
            var valid = Create(dnlib.PE.Machine.AMD64, out _);
            var random = new Random(20261021);
            for (var iteration = 0; iteration < 2048; iteration++)
            {
                var bytes = (byte[])valid.Clone();
                if ((iteration & 1) == 0)
                {
                    Array.Resize(ref bytes, random.Next(bytes.Length + 1));
                }
                else
                {
                    for (var change = 0; change < 8; change++)
                    {
                        bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                    }
                }

                try
                {
                    HybridCLRMetadata.ReadMvid(bytes);
                }
                catch (InvalidDataException) { }
                catch (Exception error)
                {
                    Assert.Fail("A bounded parser failure escaped as " + error.GetType().Name + " at case " + iteration + ": " + error);
                }
            }
        }

        private static byte[] Create(dnlib.PE.Machine machine, out Guid mvid)
        {
            mvid = Guid.NewGuid();
            using var module = new ModuleDefUser("MvidProbe.dll") { Kind = ModuleKind.Dll, Machine = machine, Mvid = mvid };
            new AssemblyDefUser("MvidProbe", new Version(1, 0, 0, 0)).Modules.Add(module);
            using var output = new MemoryStream();
            module.Write(output);
            return output.ToArray();
        }
    }
}
