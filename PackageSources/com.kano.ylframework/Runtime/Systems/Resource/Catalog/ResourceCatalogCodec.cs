using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <summary>固定 Schema v1 的有界编解码器；验证目录结构与身份关系，不提供旧格式回退或文件内容加载。</summary>
    /// <remarks>集合和对象字段按约定排序后生成规范 UTF-8；BuildId 标识不含自身的有效载荷，CatalogDigest 标识最终字节。</remarks>
    public static class ResourceCatalogCodec
    {
        /// <summary>输入 JSON 与规范输出各自允许的最大字节数。</summary>
        public const int MaximumBytes = 16 * 1024 * 1024;
        private const int Limit = 16384;
        private static readonly UTF8Encoding _utf8 = new(false, true);
        private const string Header = "SchemaVersion,ProjectId,Platform,UnityCompatibility,AppContentCompatibilityId,BuildSettingsFingerprint,ManifestFile,Bundles,Assets,Groups,SupportFiles";

        /// <summary>验证无 BuildId 的固定有效载荷，排序并计算身份，再写入最终规范字节。</summary>
        /// <remarks>只验证描述间的约束，不读取 Bundle、重算文件摘要或验证发布签名。</remarks>
        public static byte[] Seal(byte[] payload)
        {
            var data = ParseObject(payload);
            Validate(data, false);
            SortCollections(data);
            data["BuildId"] = Digest(CanonicalBytes(data));
            return CanonicalBytes(data);
        }

        /// <summary>严格读回，精确匹配调用方显式兼容值；不能省略兼容检查或把 digest 当签名。</summary>
        public static ResourceCatalogDocument Read(byte[] bytes, string projectId, string platform,
            string unityCompatibility, string appContentCompatibilityId)
        {
            var data = ParseObject(bytes);
            Validate(data, true);
            if ((string)data["ProjectId"] != projectId || (string)data["Platform"] != platform ||
                (string)data["UnityCompatibility"] != unityCompatibility || (string)data["AppContentCompatibilityId"] != appContentCompatibilityId)
            {
                throw new InvalidDataException("Catalog project/platform/Unity/app compatibility mismatch.");
            }

            SortCollections(data);
            var buildId = (string)data["BuildId"];
            // BuildId 的输入排除自身；重新加入后再比对整份字节，拒绝空白、字段顺序等非规范表示。
            data.Remove("BuildId");
            if (Digest(CanonicalBytes(data)) != buildId)
            {
                throw new InvalidDataException("Catalog BuildId does not match its payload.");
            }

            data["BuildId"] = buildId;
            var canonical = CanonicalBytes(data);
            if (!bytes.SequenceEqual(canonical))
            {
                throw new InvalidDataException("Catalog bytes are not canonical v1 UTF-8.");
            }

            return new ResourceCatalogDocument(data, canonical);
        }

        /// <summary>复制已读取文档保存的规范字节，不重新解释类型或重排数据。</summary>
        public static byte[] Write(ResourceCatalogDocument document) =>
            (document ?? throw new ArgumentNullException(nameof(document))).CopyBytes();

        /// <summary>计算给定字节的 SHA-256 小写十六进制摘要；不证明来源可信或签名有效。</summary>
        public static string Digest(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        // 不做 CLR 类型反序列化：所有字段逐项读取，$type 等未知键被拒绝，等价于 TypeNameHandling.None。
        /// <summary>在字节数与 JSON 深度限制内读取单个对象，拒绝重复字段、非法 UTF-8 和额外内容。</summary>
        internal static JObject ParseObject(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes)
            {
                throw new InvalidDataException("JSON byte limit exceeded or empty input.");
            }

            try
            {
                using var text = new StringReader(_utf8.GetString(bytes));
                using var reader = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
                var data = JObject.Load(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    CommentHandling = CommentHandling.Load
                });
                if (reader.Read())
                {
                    throw new InvalidDataException("Additional JSON content.");
                }

                return data;
            }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException)
            {
                throw new InvalidDataException("Invalid bounded JSON.", error);
            }
        }

        /// <summary>递归按精确字段名排序对象，保留数组当前顺序，并输出固定转义和数值文化的紧凑 UTF-8。</summary>
        internal static byte[] CanonicalBytes(JToken data)
        {
            // 数组顺序由各 Schema 的集合排序负责；这里只统一对象字段，不改变一般数组的语义。
            JToken Ordered(JToken token) => token switch
            {
                JObject obj => new JObject(obj.Properties().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new JProperty(x.Name, Ordered(x.Value)))),
                JArray array => new JArray(array.Select(Ordered)),
                _ => token.DeepClone()
            };
            var builder = new StringBuilder();
            using (var writer = new JsonTextWriter(new StringWriter(builder, CultureInfo.InvariantCulture))
            {
                Formatting = Formatting.None,
                StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
                Culture = CultureInfo.InvariantCulture
            })
            {
                Ordered(data).WriteTo(writer);
            }

            var bytes = _utf8.GetBytes(builder.ToString());
            if (bytes.Length > MaximumBytes)
            {
                throw new InvalidDataException("Canonical JSON byte limit exceeded.");
            }

            return bytes;
        }

        /// <summary>要求对象字段集合与固定 Schema 完全一致，不接受缺项或扩展键。</summary>
        internal static void Fields(JObject data, string names)
        {
            if (data == null || !data.Properties().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(names.Split(',').OrderBy(x => x, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("Missing or unknown JSON fields.");
            }
        }
        /// <summary>读取有长度、空白和控制字符约束的精确字符串；只有显式允许时接纳 JSON null。</summary>
        internal static string Text(JObject data, string key, bool nullable = false)
        {
            var token = data[key];
            if (nullable && token?.Type == JTokenType.Null)
            {
                return null;
            }

            if (token?.Type != JTokenType.String)
            {
                throw new InvalidDataException("Expected string: " + key);
            }

            var value = (string)token;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value != value.Trim() || value.Any(char.IsControl))
            {
                throw new InvalidDataException("Invalid bounded string: " + key);
            }

            return value;
        }
        /// <summary>严格读取可表示为 Int64 的 JSON 整数，不把浮点或数字字符串隐式转换。</summary>
        internal static long Integer(JObject data, string key)
        {
            try
            {
                if (data[key]?.Type != JTokenType.Integer)
                {
                    throw new InvalidDataException("Expected integer: " + key);
                }

                return (long)data[key];
            }
            catch (Exception error) when (error is OverflowException || error is InvalidCastException)
            {
                throw new InvalidDataException("Invalid integer: " + key, error);
            }
        }
        /// <summary>严格读取 JSON 布尔值，不接纳字符串或数字替代。</summary>
        internal static bool Boolean(JObject data, string key)
        {
            if (data[key]?.Type != JTokenType.Boolean)
            {
                throw new InvalidDataException("Expected boolean: " + key);
            }

            return (bool)data[key];
        }
        /// <summary>读取数量有界的数组，并按字段契约决定是否允许空集合。</summary>
        private static JArray Items(JObject data, string key, bool nonempty = false)
        {
            if (data[key] is not JArray array || array.Count > Limit || nonempty && array.Count == 0)
            {
                throw new InvalidDataException("Invalid bounded collection: " + key);
            }

            return array;
        }
        /// <summary>要求集合成员为 JSON 对象，供逐项 Schema 校验使用。</summary>
        private static JObject Object(JToken token) => token as JObject ?? throw new InvalidDataException("Expected object entry.");
        /// <summary>验证固定长度的小写十六进制文本，不校验其对应内容。</summary>
        internal static void Hex(string value, int length)
        {
            if (value == null || value.Length != length || value.Any(x => !(x >= '0' && x <= '9' || x >= 'a' && x <= 'f')))
            {
                throw new InvalidDataException("Expected canonical lowercase hex identity.");
            }
        }
        /// <summary>检查有长度上限的稳定 ASCII 标识，并统一报告为目录数据错误。</summary>
        internal static void Id(string value)
        {
            if (value?.Length > 128)
            {
                throw new InvalidDataException("Identifier length exceeded.");
            }

            try
            {
                ResourceAddress.ValidateId(value, nameof(value));
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("Invalid stable identifier.", error);
            }
        }
        /// <summary>复用逻辑路径校验，拒绝非精确路径而不是规范化输入。</summary>
        private static void PathValue(string value)
        {
            try
            {
                ResourceAddress.ValidatePath(value, nameof(value));
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("Invalid catalog address/path.", error);
            }
        }
        /// <summary>要求原生 Bundle 身份位于项目摘要前缀内且只使用约定字符。</summary>
        private static void Native(string value, string prefix)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length <= prefix.Length ||
                value.Any(x => !(x >= 'a' && x <= 'z' || x >= 'A' && x <= 'Z' || x >= '0' && x <= '9' || x == '_')))
            {
                throw new InvalidDataException("Bundle file/native identity is outside its YL project namespace.");
            }
        }

        /// <summary>校验固定字段、兼容描述、闭合文件集及 Bundle、资产、子资产和组之间的引用约束。</summary>
        private static void Validate(JObject data, bool sealedCatalog)
        {
            Fields(data, Header + (sealedCatalog ? ",BuildId" : ""));
            if (Integer(data, "SchemaVersion") != 1)
            {
                throw new InvalidDataException("Unsupported Catalog SchemaVersion.");
            }

            var project = Text(data, "ProjectId");
            Id(project);
            var platform = Text(data, "Platform");
            if (!new[] { "StandaloneWindows64", "StandaloneOSX", "StandaloneLinux64" }.Contains(platform))
            {
                throw new InvalidDataException("Unsupported catalog platform.");
            }

            Text(data, "UnityCompatibility");
            Id(Text(data, "AppContentCompatibilityId"));
            Hex(Text(data, "BuildSettingsFingerprint"), 64);
            if (sealedCatalog)
            {
                Hex(Text(data, "BuildId"), 64);
            }

            var prefix = "ylr_" + Digest(_utf8.GetBytes(project)).Substring(0, 32) + "_";
            var manifest = Text(data, "ManifestFile");
            if (manifest != "manifest")
            {
                throw new InvalidDataException("Invalid manifest file locator.");
            }

            var bundles = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items(data, "Bundles", true))
            {
                var bundle = Object(item);
                Fields(bundle, "LogicalId,NativeName,RelativeFile,Length,Sha256,UnityHash,IsScene,Dependencies");
                var id = Text(bundle, "LogicalId");
                Native(id, prefix);
                if (Text(bundle, "NativeName") != id || Text(bundle, "RelativeFile") != "b_" + Digest(_utf8.GetBytes(id)).Substring(0, 32) || bundles.ContainsKey(id))
                {
                    throw new InvalidDataException("Bundle logical/native/file identity conflict.");
                }

                bundles.Add(id, bundle);
                FileRecord(bundle, files);
                Hex(Text(bundle, "UnityHash"), 32);
                Boolean(bundle, "IsScene");
                foreach (var edge in Items(bundle, "Dependencies"))
                {
                    if (edge.Type != JTokenType.String)
                    {
                        throw new InvalidDataException("Expected dependency identifier.");
                    }
                }
            }
            try
            {
                _ = new ResourceCatalog(project, Array.Empty<ResourceCatalogEntry>(), bundles.Select(x =>
                    new ResourceDependencyNode(x.Key, ((JArray)x.Value["Dependencies"]).Select(a => (string)a))));
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("Invalid Catalog Bundle DAG.", error);
            }
            var expectedSupport = new HashSet<string>(bundles.Values.Select(x => (string)x["RelativeFile"] + ".manifest"), StringComparer.Ordinal)
                { manifest, manifest + ".manifest" };
            foreach (var item in Items(data, "SupportFiles", true))
            {
                var file = Object(item);
                Fields(file, "RelativeFile,Length,Sha256");
                FileRecord(file, files);
                if (!expectedSupport.Remove(Text(file, "RelativeFile")))
                {
                    throw new InvalidDataException("Unknown support file.");
                }
            }
            if (expectedSupport.Count != 0)
            {
                throw new InvalidDataException("Missing manifest/support files.");
            }

            var assets = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var addresses = new HashSet<(string, string)>();
            var mainFiles = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var mainGuids = new HashSet<string>(StringComparer.Ordinal);
            var subIdentities = new HashSet<(string, string, string, string, string)>();
            foreach (var item in Items(data, "Assets", true))
            {
                var asset = Object(item);
                Fields(asset, "AssetId,Guid,LocalId,Address,Aliases,SubAssetKey,BundleId,LoadName,AssetType,AssetAssembly,Kind,SubAssetName");
                var guid = Text(asset, "Guid");
                Hex(guid, 32);
                var local = Integer(asset, "LocalId");
                var id = Text(asset, "AssetId");
                if (local == 0 || id != guid + "_" + local.ToString(CultureInfo.InvariantCulture) || assets.ContainsKey(id))
                {
                    throw new InvalidDataException("Invalid or duplicate canonical Unity asset identity.");
                }

                assets.Add(id, asset);
                var address = Text(asset, "Address");
                PathValue(address);
                var sub = Text(asset, "SubAssetKey", true);
                if (sub != null)
                {
                    PathValue(sub);
                }

                if (addresses.Count >= Limit || !addresses.Add((address.ToUpperInvariant(), sub?.ToUpperInvariant())))
                {
                    throw new InvalidDataException("Catalog address limit or case-folded address conflict.");
                }

                foreach (var alias in Items(asset, "Aliases"))
                {
                    if (alias.Type != JTokenType.String)
                    {
                        throw new InvalidDataException("Expected alias address.");
                    }

                    var key = Text(new JObject { ["Alias"] = alias.DeepClone() }, "Alias");
                    PathValue(key);
                    if (addresses.Count >= Limit || !addresses.Add((key.ToUpperInvariant(), sub?.ToUpperInvariant())))
                    {
                        throw new InvalidDataException("Duplicate/case-folded alias address conflict.");
                    }
                }
                var bundle = Text(asset, "BundleId");
                if (!bundles.TryGetValue(bundle, out var owner))
                {
                    throw new InvalidDataException("Unknown asset Bundle.");
                }

                var kind = Text(asset, "Kind");
                var load = Text(asset, "LoadName");
                PathValue(load);
                var type = Text(asset, "AssetType");
                var assembly = Text(asset, "AssetAssembly", true);
                if (kind == "Scene" ? type != "Scene" || assembly != null : type == "Scene" || assembly == null || assembly.Length > 128)
                {
                    throw new InvalidDataException("Scene/type/assembly identity mismatch.");
                }

                if (type.Contains(",") || type.Contains("[") || type.Contains(" "))
                {
                    throw new InvalidDataException("AssetType must be a descriptive full name, not an AQN.");
                }

                var subName = Text(asset, "SubAssetName", true);
                if (kind == "SubAsset")
                {
                    if ((bool)owner["IsScene"] || sub != local.ToString(CultureInfo.InvariantCulture) || subName == null ||
                        !subIdentities.Add((bundle, load, type, assembly, subName)))
                    {
                        throw new InvalidDataException("Ambiguous or invalid subasset identity.");
                    }
                }
                else if (kind == "Asset" || kind == "Scene")
                {
                    if (sub != null || subName != null || (kind == "Scene") != (bool)owner["IsScene"] || mainFiles.ContainsKey(load) || !mainGuids.Add(guid))
                    {
                        throw new InvalidDataException("Invalid main asset/Scene descriptor.");
                    }

                    mainFiles.Add(load, asset);
                }
                else
                {
                    throw new InvalidDataException("Unknown asset kind.");
                }

                if (kind == "Scene" ? !load.StartsWith("Assets/", StringComparison.Ordinal) || !load.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) :
                    !load.StartsWith("assets/", StringComparison.Ordinal) || load != load.ToLowerInvariant())
                {
                    throw new InvalidDataException("Invalid native load path casing/kind.");
                }
            }
            foreach (var asset in assets.Values.Where(x => (string)x["Kind"] == "SubAsset"))
            {
                // localId 区分逻辑子资产，但原生加载仍依赖主文件和名称；两套描述必须绑定到同一主资产。
                if (!mainFiles.TryGetValue((string)asset["LoadName"], out var main) || (string)main["Guid"] != (string)asset["Guid"] ||
                    (string)main["Address"] != (string)asset["Address"] || (string)main["BundleId"] != (string)asset["BundleId"] ||
                    !((JArray)main["Aliases"]).Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal)
                        .SequenceEqual(((JArray)asset["Aliases"]).Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal)))
                {
                    throw new InvalidDataException("Subasset is not bound to its canonical main asset.");
                }
            }

            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items(data, "Groups", true))
            {
                var group = Object(item);
                Fields(group, "Id,Members");
                var id = Text(group, "Id");
                Id(id);
                if (!groups.Add(id))
                {
                    throw new InvalidDataException("Case-folded Group conflict.");
                }

                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in Items(group, "Members", true))
                {
                    var entry = Object(member);
                    Fields(entry, "AssetId,Required");
                    Boolean(entry, "Required");
                    var asset = Text(entry, "AssetId");
                    if (!assets.ContainsKey(asset) || !members.Add(asset))
                    {
                        throw new InvalidDataException("Unknown or duplicate Group member.");
                    }
                }
            }
        }

        /// <summary>校验单层文件名、跨文件唯一性、正长度和摘要格式，不访问文件系统。</summary>
        private static void FileRecord(JObject data, HashSet<string> files)
        {
            var name = Text(data, "RelativeFile");
            if (name.IndexOfAny(new[] { '/', '\\', ':', '%', '?' }) >= 0 || name == "." || name == ".." || !files.Add(name))
            {
                throw new InvalidDataException("Unsafe or conflicting catalog file.");
            }

            if (Integer(data, "Length") <= 0)
            {
                throw new InvalidDataException("Empty/invalid Bundle/support file length.");
            }

            Hex(Text(data, "Sha256"), 64);
        }

        /// <summary>按 Schema 身份键排序各集合，使相同有效载荷不因输入枚举顺序改变字节身份。</summary>
        private static void SortCollections(JObject data)
        {
            void Sort(JObject owner, string key, string field) => owner[key] = new JArray(((JArray)owner[key]).OrderBy(x => (string)x[field], StringComparer.Ordinal));
            Sort(data, "Bundles", "LogicalId");
            Sort(data, "Assets", "AssetId");
            Sort(data, "Groups", "Id");
            Sort(data, "SupportFiles", "RelativeFile");
            foreach (var asset in ((JArray)data["Assets"]).Cast<JObject>())
            {
                asset["Aliases"] = new JArray(((JArray)asset["Aliases"]).OrderBy(x => (string)x, StringComparer.Ordinal));
            }

            foreach (var bundle in ((JArray)data["Bundles"]).Cast<JObject>())
            {
                bundle["Dependencies"] = new JArray(((JArray)bundle["Dependencies"]).OrderBy(x => (string)x, StringComparer.Ordinal));
            }

            foreach (var group in ((JArray)data["Groups"]).Cast<JObject>())
            {
                Sort(group, "Members", "AssetId");
            }
        }
    }
}
