using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    [Category("YL.Module.Resource")]
    public class ResourceCatalogCodecTests
    {
        private Type Codec => typeof(ResourceCatalog).Assembly.GetType("YLFramework.Resource.ResourceCatalogCodec");

        [Test]
        public void CanonicalBytesAndBuildIdentityAreIndependentOfInputOrdering()
        {
            var input = Payload();
            var first = Seal(input);
            var shuffled = new JObject(input.Properties().Reverse().Select(x => new JProperty(x.Name, x.Value.DeepClone())));
            shuffled["SupportFiles"] = new JArray(((JArray)input["SupportFiles"]).Reverse());
            CollectionAssert.AreEqual(first, Seal(shuffled));
            var doc = Read(first);
            CollectionAssert.AreEqual(first, (byte[])Codec.GetMethod("Write").Invoke(null, new[] { doc }));
            var json = JObject.Parse(Encoding.UTF8.GetString(first));
            Assert.That((string)json["BuildId"], Has.Length.EqualTo(64));
            Assert.That((string)json["BuildId"], Is.Not.EqualTo(Hash(first)), "BuildId is payload identity, not the final catalog digest.");
            input["AppContentCompatibilityId"] = "app-v2";
            Assert.That(JObject.Parse(Encoding.UTF8.GetString(Seal(input)))["BuildId"].ToString(), Is.Not.EqualTo(json["BuildId"].ToString()));
        }

        [TestCase("SchemaVersion", "2")]
        [TestCase("Platform", "StandaloneLinux64")]
        [TestCase("UnityCompatibility", "2021.3")]
        [TestCase("AppContentCompatibilityId", "other-app")]
        [TestCase("ProjectId", "other-project")]
        public void UnsupportedSchemaOrCompatibilityCannotBeRead(string key, string value)
        {
            var input = Payload();
            input[key] = key == "SchemaVersion" ? (JToken)int.Parse(value) : value;
            if (key == "SchemaVersion" || key == "ProjectId")
            {
                AssertRejected(() => Seal(input));
            }
            else
            {
                AssertRejected(() => Read(Seal(input)));
            }
        }

        [Test]
        public void DuplicateUnknownMetadataMalformedUtf8AndNonCanonicalBytesAreRejected()
        {
            var bytes = Seal(Payload());
            var text = Encoding.UTF8.GetString(bytes);
            AssertRejected(() => Read(Encoding.UTF8.GetBytes(text.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1"))));
            AssertRejected(() => Read(Encoding.UTF8.GetBytes(text.Replace("{", "{\"$type\":\"System.Object\",", StringComparison.Ordinal))));
            AssertRejected(() => Read(Encoding.UTF8.GetBytes(" " + text)));
            AssertRejected(() => Read(new byte[] { 0xff, 0xfe }));
            AssertRejected(() => Read(new byte[16 * 1024 * 1024 + 1]));
            AssertRejected(() => Read(Encoding.UTF8.GetBytes(new string('[', 40) + new string(']', 40))));
        }

        [Test]
        public void IdentityAddressGroupDependencyAndFileConflictsCannotBeSealed()
        {
            void Reject(Action<JObject> mutate)
            {
                var input = Payload();
                mutate(input);
                AssertRejected(() => Seal(input));
            }
            Reject(x => x["Assets"][0]["AssetId"] = "invented");
            Reject(x => ((JArray)x["Assets"]).Add(x["Assets"][0].DeepClone()));
            Reject(x => x["Groups"][0]["Members"][0]["AssetId"] = "missing");
            Reject(x => ((JArray)x["Bundles"][0]["Dependencies"]).Add("missing"));
            Reject(x => ((JArray)x["Bundles"][0]["Dependencies"]).Add((string)x["Bundles"][0]["LogicalId"]));
            Reject(x => x["Bundles"][0]["RelativeFile"] = "../escape");
            Reject(x => x["Bundles"][0]["RelativeFile"] = "C:/escape");
            Reject(x => x["Bundles"][0]["Length"] = -1);
            Reject(x => x["Bundles"][0]["Sha256"] = new string('A', 64));
            Reject(x => x["Bundles"][0]["NativeName"] = "MainModule");
            Reject(x => x["Assets"][0]["LoadName"] = "Assets/Missing.txt");
            Reject(x => x["Assets"][0]["SubAssetKey"] = "unexpected");
            Reject(x => x["SupportFiles"][0]["RelativeFile"] = (string)x["Bundles"][0]["RelativeFile"]);
            Reject(x =>
            {
                var asset = x["Assets"][0].DeepClone();
                asset["Guid"] = "fedcba9876543210fedcba9876543210";
                asset["AssetId"] = "fedcba9876543210fedcba9876543210_11400000";
                asset["Address"] = "ENTRY.TXT";
                asset["LoadName"] = "assets/owned/another.txt";
                ((JArray)x["Assets"]).Add(asset);
            });
        }

        [Test]
        public void CorruptedBuildIdIsNotAcceptedEvenWhenJsonIsWellFormed()
        {
            var bytes = Seal(Payload());
            var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
            json["BuildId"] = new string('0', 64);
            AssertRejected(() => Read(Encoding.UTF8.GetBytes(json.ToString(Formatting.None))));
        }

        [Test]
        public void ReleaseIndexContainsOnlyExactSnapshotIdentityAndRejectsWrongLocators()
        {
            var catalog = (ResourceCatalogDocument)Read(Seal(Payload()));
            var index = ResourceReleaseIndex.Create(catalog);
            var bytes = index.Write();
            var read = ResourceReleaseIndex.Read(bytes, "catalog", "StandaloneWindows64", "2022.3.62f3c1", "app-v1");
            Assert.That(read.CatalogDigest, Is.EqualTo(catalog.CatalogDigest));
            Assert.That(read.BuildId, Is.EqualTo(catalog.BuildId));
            var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
            json["CatalogFile"] = "../" + catalog.CatalogDigest + "/resource-catalog.json";
            Assert.Throws<InvalidDataException>(() => ResourceReleaseIndex.Read(Encoding.UTF8.GetBytes(json.ToString(Formatting.None)),
                "catalog", "StandaloneWindows64", "2022.3.62f3c1", "app-v1"));
            Assert.Throws<InvalidDataException>(() => ResourceReleaseIndex.Read(bytes, "catalog", "StandaloneWindows64", "2022.3.62f3c1", "app-v2"));
        }

        [Test]
        public void ExplicitAliasesAreCanonicalAndCannotIntroduceAmbiguousAddresses()
        {
            var input = Payload();
            input["Assets"][0]["Aliases"] = new JArray("z-alias", "a-alias");
            var first = Seal(input);
            input["Assets"][0]["Aliases"] = new JArray("a-alias", "z-alias");
            CollectionAssert.AreEqual(first, Seal(input));
            var doc = (ResourceCatalogDocument)Read(first);
            var property = typeof(ResourceCatalogAsset).GetProperty("Aliases");
            Assert.That(property, Is.Not.Null);
            CollectionAssert.AreEqual(new[] { "a-alias", "z-alias" }, (System.Collections.IEnumerable)property.GetValue(doc.Assets[0]));
            foreach (var aliases in new[] { new[] { "entry.txt" }, new[] { "alias", "ALIAS" }, new[] { "../escape" } })
            {
                input["Assets"][0]["Aliases"] = new JArray(aliases);
                AssertRejected(() => Seal(input));
            }
        }

        [Test]
        public void SubassetTypeIdentityIncludesAssemblyAndStillRejectsExactDuplicateNativeSelectors()
        {
            var input = Payload();
            var first = input["Assets"][0].DeepClone();
            first["AssetId"] = (string)first["Guid"] + "_11400001";
            first["LocalId"] = 11400001;
            first["SubAssetKey"] = "11400001";
            first["Kind"] = "SubAsset";
            first["SubAssetName"] = "Same";
            first["AssetType"] = "Example.SameType";
            first["AssetAssembly"] = "AssemblyOne";
            var second = first.DeepClone();
            second["AssetId"] = (string)second["Guid"] + "_11400002";
            second["LocalId"] = 11400002;
            second["SubAssetKey"] = "11400002";
            second["AssetAssembly"] = "AssemblyTwo";
            ((JArray)input["Assets"]).Add(first);
            ((JArray)input["Assets"]).Add(second);
            Assert.That(((ResourceCatalogDocument)Read(Seal(input))).Assets.Count, Is.EqualTo(3));
            second["AssetAssembly"] = "AssemblyOne";
            AssertRejected(() => Seal(input));
        }

        [Test]
        public void TotalAddressLimitIsSharedByCanonicalAndAliasesIndependentOfAssetOrder()
        {
            var input = Payload();
            var template = input["Assets"][0];
            var entries = new JArray(Enumerable.Range(1, 16384).Select(i =>
            {
                var asset = template.DeepClone();
                var guid = i.ToString("x32");
                asset["Guid"] = guid;
                asset["AssetId"] = guid + "_11400000";
                asset["Address"] = "entry-" + i;
                asset["LoadName"] = "assets/owned/entry-" + i + ".txt";
                if (i == 1)
                {
                    asset["Aliases"] = new JArray("alias");
                }

                return asset;
            }));
            input["Assets"] = entries;
            input["Groups"][0]["Members"] = new JArray(new JObject { ["AssetId"] = (string)entries[0]["AssetId"], ["Required"] = true });
            AssertRejected(() => Seal(input));
            input["Assets"] = new JArray(entries.Reverse());
            AssertRejected(() => Seal(input));
            entries.RemoveAt(entries.Count - 1);
            input["Assets"] = entries;
            Assert.That(((ResourceCatalogDocument)Read(Seal(input))).Assets.Count, Is.EqualTo(16383), "16383 canonical + 1 alias reaches the exact shared limit.");
        }

        [Test]
        public void TypeAssemblyIdentityChangesBuildIdentityAndScenesCannotPretendToBeAssets()
        {
            var input = Payload();
            var first = Seal(input);
            input["Assets"][0]["AssetAssembly"] = "AnotherCompiledAssembly";
            var second = Seal(input);
            Assert.That(JObject.Parse(Encoding.UTF8.GetString(second))["BuildId"].ToString(), Is.Not.EqualTo(JObject.Parse(Encoding.UTF8.GetString(first))["BuildId"].ToString()));
            var property = typeof(ResourceCatalogAsset).GetProperty("AssetAssembly");
            Assert.That(property, Is.Not.Null);
            Assert.That(property.GetValue(((ResourceCatalogDocument)Read(second)).Assets[0]), Is.EqualTo("AnotherCompiledAssembly"));
            input["Assets"][0]["AssetAssembly"] = null;
            AssertRejected(() => Seal(input));
            input["Assets"][0]["AssetAssembly"] = "UnityEngine.CoreModule";
            input["Assets"][0]["AssetType"] = "Scene";
            AssertRejected(() => Seal(input));
        }

        [Test]
        public void TwoDifferentMainFilesCannotClaimTheSameUnityGuid()
        {
            var input = Payload();
            var duplicate = input["Assets"][0].DeepClone();
            duplicate["LocalId"] = 11400001;
            duplicate["AssetId"] = "0123456789abcdef0123456789abcdef_11400001";
            duplicate["Address"] = "other.txt";
            duplicate["LoadName"] = "assets/owned/other.txt";
            ((JArray)input["Assets"]).Add(duplicate);
            AssertRejected(() => Seal(input));
        }

        internal static JObject Payload()
        {
            const string Guid = "0123456789abcdef0123456789abcdef";
            var prefix = "ylr_" + Hash(Encoding.UTF8.GetBytes("catalog"))[..32] + "_";
            var name = prefix + "a_" + Guid;
            var fileName = "b_" + Hash(Encoding.UTF8.GetBytes(name))[..32];
            var manifest = "manifest";
            JObject File(string file) => new JObject { ["RelativeFile"] = file, ["Length"] = 10, ["Sha256"] = new string('a', 64) };
            var bundle = File(fileName);
            bundle["LogicalId"] = name;
            bundle["NativeName"] = name;
            bundle["UnityHash"] = new string('b', 32);
            bundle["IsScene"] = false;
            bundle["Dependencies"] = new JArray();
            return new JObject
            {
                ["SchemaVersion"] = 1,
                ["ProjectId"] = "catalog",
                ["Platform"] = "StandaloneWindows64",
                ["UnityCompatibility"] = "2022.3.62f3c1",
                ["AppContentCompatibilityId"] = "app-v1",
                ["BuildSettingsFingerprint"] = new string('c', 64),
                ["ManifestFile"] = manifest,
                ["Bundles"] = new JArray(bundle),
                ["Assets"] = new JArray(new JObject
                {
                    ["AssetId"] = Guid + "_11400000",
                    ["Guid"] = Guid,
                    ["LocalId"] = 11400000,
                    ["Address"] = "entry.txt",
                    ["Aliases"] = new JArray(),
                    ["SubAssetKey"] = null,
                    ["BundleId"] = name,
                    ["LoadName"] = "assets/owned/entry.txt",
                    ["AssetType"] = "UnityEngine.TextAsset",
                    ["AssetAssembly"] = "UnityEngine.CoreModule",
                    ["Kind"] = "Asset",
                    ["SubAssetName"] = null
                }),
                ["Groups"] = new JArray(new JObject { ["Id"] = "main", ["Members"] = new JArray(new JObject { ["AssetId"] = Guid + "_11400000", ["Required"] = true }) }),
                ["SupportFiles"] = new JArray(File(manifest), File(manifest + ".manifest"), File(fileName + ".manifest"))
            };
        }

        private byte[] Seal(JObject payload)
        {
            Assert.That(Codec, Is.Not.Null, "The versioned catalog codec is not implemented.");
            return (byte[])Codec.GetMethod("Seal").Invoke(null, new object[] { Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)) });
        }
        private object Read(byte[] bytes)
        {
            Assert.That(Codec, Is.Not.Null, "The versioned catalog codec is not implemented.");
            return Codec.GetMethod("Read").Invoke(null, new object[] { bytes, "catalog", "StandaloneWindows64", "2022.3.62f3c1", "app-v1" });
        }
        private static void AssertRejected(Action action)
        {
            var error = Assert.Throws<TargetInvocationException>(() => action());
            Assert.That(error.InnerException, Is.InstanceOf<InvalidDataException>(), error.InnerException?.ToString());
        }
        internal static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
