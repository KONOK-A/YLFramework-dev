using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using YLFramework.Resource;

namespace YLFramework.Integrations.HybridCLR
{
    /// <summary>一个热更 DLL 或 AOT metadata 文件的身份、字节校验和引用描述。</summary>
    [JsonObject(ItemRequired = Required.Always)]
    public sealed class HybridCLRPayload
    {
        /// <summary>程序集的精确简单名称，不含文件扩展名。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 0)]
        public string Name
        {
            get; set;
        }
        /// <summary>包含版本、culture 和公钥标记的完整程序集身份。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 1)]
        public string Identity
        {
            get; set;
        }
        /// <summary>PE Module 表中的非空 MVID，使用 D 格式字符串。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 2)]
        public string Mvid
        {
            get; set;
        }
        /// <summary>项目内规范资源路径，不含 ProjectId。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 3)]
        public string Address
        {
            get; set;
        }
        /// <summary>原始 DLL 字节长度，不是资源包或压缩后的长度。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 4)]
        public long Length
        {
            get; set;
        }
        /// <summary>原始 DLL 字节的小写十六进制 SHA256。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 5)]
        public string Sha256
        {
            get; set;
        }
        /// <summary>本清单内的直接热更依赖名称；AOT metadata 必须为空。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 6)]
        public string[] Dependencies { get; set; } = Array.Empty<string>();
        /// <summary>DLL 声明的完整程序集引用身份，封口后按序排列。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 7)]
        public string[] References { get; set; } = Array.Empty<string>();
    }

    /// <summary>同一 Resource Project 内的代码描述；所有检查均可在不可逆 native 加载前执行。</summary>
    [JsonObject(ItemRequired = Required.Always)]
    public sealed class HybridCLRManifest
    {
        /// <summary>UTF-8 清单大小上限，单位为字节。</summary>
        public const int MaximumManifestBytes = 1024 * 1024;
        /// <summary>热更集合与 metadata 集合各自允许的程序集数量上限。</summary>
        public const int MaximumAssemblyCount = 128;
        /// <summary>单个 DLL 的字节预算上限。</summary>
        public const long MaximumPayloadBytes = 64L * 1024 * 1024;
        /// <summary>清单内全部 DLL 的累计字节预算上限。</summary>
        public const long MaximumTotalBytes = 256L * 1024 * 1024;
        /// <summary>每个 Resource 项目内固定的代码清单路径。</summary>
        public const string ManifestPath = "hybridclr/manifest.json";

        /// <summary>描述格式版本，当前只接受版本 1。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 0)]
        public int SchemaVersion { get; set; } = 1;
        /// <summary>拥有清单及全部代码资源的 Resource 项目身份。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 1)]
        public string ProjectId
        {
            get; set;
        }
        /// <summary>描述目标平台，当前格式仅接受 StandaloneWindows64。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 2)]
        public string Platform { get; set; } = "StandaloneWindows64";
        /// <summary>生成代码包的 Unity 版本，运行时要求与当前版本精确一致。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 3)]
        public string UnityVersion
        {
            get; set;
        }
        /// <summary>HybridCLR 包版本，当前只接受 9.0.3。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 4)]
        public string HybridCLRVersion { get; set; } = "9.0.3";
        /// <summary>生成代码包时的 Development 标记，运行时须匹配。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 5)]
        public bool Development
        {
            get; set;
        }
        /// <summary>应用基座的内容兼容身份，不由代码包自行推断。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 6)]
        public string AppContentCompatibilityId
        {
            get; set;
        }
        // Seal 计算摘要时尚无 ID；解析后的完整性仍由 ValidatePackageId 强制检查。
        /// <summary>去掉自身字段后对描述计算的 SHA256，用于区分项目代码版本。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 7, Required = Required.AllowNull)]
        public string CodePackageId
        {
            get; set;
        }
        /// <summary>依赖优先、同等条件按名称稳定排列的热更程序集集合。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 8)]
        public HybridCLRPayload[] HotUpdateAssemblies { get; set; } = Array.Empty<HybridCLRPayload>();
        /// <summary>按名称排列、须先于热更程序集补充的 AOT metadata 集合。</summary>
        [UnityEngine.Scripting.Preserve, JsonProperty(Order = 9)]
        public HybridCLRPayload[] AotMetadata { get; set; } = Array.Empty<HybridCLRPayload>();

        // 限定清单的 JSON 契约，拒绝未知字段及类型名反序列化。
        private static JsonSerializerSettings Settings() => new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            MissingMemberHandling = MissingMemberHandling.Error,
            TypeNameHandling = TypeNameHandling.None,
            DateParseHandling = DateParseHandling.None,
            MaxDepth = 32
        };

        /// <summary>严格读取有界清单，拒绝重复字段、尾随数据及非规范描述，并核对代码包摘要。</summary>
        /// <param name="json">UTF-8 字节预算内的完整 JSON 清单文本。</param>
        /// <returns>已通过结构、顺序与摘要检查的清单，不代表 DLL 字节已验证。</returns>
        public static HybridCLRManifest Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            {
                throw new InvalidDataException("Missing or oversized HybridCLR manifest.");
            }

            try
            {
                using var text = new StringReader(json);
                using var reader = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
                var token = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read())
                {
                    throw new InvalidDataException("Trailing data after the HybridCLR manifest.");
                }

                var manifest = token.ToObject<HybridCLRManifest>(JsonSerializer.Create(Settings()));
                manifest.ValidateStructure(true);
                manifest.ValidatePackageId();
                return manifest;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Invalid HybridCLR manifest JSON.", exception);
            }
        }

        /// <summary>按清单契约序列化当前内容，不自动封口或补做校验。</summary>
        /// <returns>不带缩进的 JSON 文本。</returns>
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.None, Settings());

        /// <summary>仅构建端使用；排序后计算不包含自身 ID 的规范化描述摘要。</summary>
        public void Seal()
        {
            ValidateStructure(false);
            foreach (var payload in HotUpdateAssemblies.Concat(AotMetadata))
            {
                payload.Dependencies = payload.Dependencies.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                payload.References = payload.References.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            }
            HotUpdateAssemblies = DependencyOrder();
            AotMetadata = AotMetadata.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
            CodePackageId = ComputePackageId();
        }

        /// <summary>校验规范描述、代码包摘要及调用方要求的项目、基座和构建身份。</summary>
        /// <param name="projectId">预期 Resource 项目身份。</param>
        /// <param name="appContentCompatibilityId">应用提供的基座兼容身份。</param>
        /// <param name="unityVersion">当前 Unity 版本字符串。</param>
        /// <param name="development">当前运行环境的 Development 标记。</param>
        public void Validate(string projectId, string appContentCompatibilityId, string unityVersion, bool development)
        {
            ValidateStructure(true);
            if (ProjectId != projectId || AppContentCompatibilityId != appContentCompatibilityId ||
                UnityVersion != unityVersion || Development != development)
            {
                throw new InvalidDataException("HybridCLR package does not match the requested project, base, Unity version or Development profile.");
            }

            ValidatePackageId();
        }

        /// <summary>校验 DLL 的非空长度、单文件预算及 SHA256，不解析程序集身份或 MVID。</summary>
        /// <param name="payload">已声明的文件描述。</param>
        /// <param name="bytes">从资源读取的原始 DLL 字节。</param>
        public static void ValidatePayload(HybridCLRPayload payload, byte[] bytes)
        {
            if (payload == null || bytes == null || bytes.LongLength != payload.Length ||
                bytes.LongLength == 0 || bytes.LongLength > MaximumPayloadBytes || Hash(bytes) != payload.Sha256)
            {
                throw new InvalidDataException("HybridCLR payload length or SHA256 mismatch: " + payload?.Name);
            }
        }

        /// <summary>计算字节内容的小写十六进制 SHA256。</summary>
        /// <param name="bytes">非空引用的输入字节数组，可为零长度。</param>
        /// <returns>64 个十六进制字符组成的摘要。</returns>
        public static string Hash(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>拼接项目内规范 DLL 资源路径，不单独校验程序集名称。</summary>
        /// <param name="name">不含扩展名的程序集名称。</param>
        /// <param name="aot">是否使用 AOT metadata 子目录。</param>
        /// <returns>以 hybridclr 开头、以 .dll.bytes 结尾的相对路径。</returns>
        public static string PayloadAddress(string name, bool aot) => "hybridclr/" + (aot ? "metadata/" : "assemblies/") + name + ".dll.bytes";

        // 摘要排除自身 ID，避免代码包身份递归依赖自身。
        private string ComputePackageId()
        {
            var description = JObject.FromObject(this, JsonSerializer.Create(Settings()));
            description.Remove("codePackageId");
            return Hash(Encoding.UTF8.GetBytes(description.ToString(Formatting.None)));
        }

        // 拒绝缺失、格式错误或与当前描述不符的代码包身份。
        private void ValidatePackageId()
        {
            if (!IsHash(CodePackageId) || CodePackageId != ComputePackageId())
            {
                throw new InvalidDataException("HybridCLR code package identity does not match its descriptor and payload hashes.");
            }
        }

        // 校验版本、资源边界、完整身份、预算和依赖图，并按需要求规范顺序。
        private void ValidateStructure(bool ordered)
        {
            if (SchemaVersion != 1 || HybridCLRVersion != "9.0.3" || Platform != "StandaloneWindows64" ||
                string.IsNullOrWhiteSpace(UnityVersion) || UnityVersion.Length > 64)
            {
                throw new InvalidDataException("Unsupported HybridCLR schema, package version, platform or Unity version: schema=" +
                    SchemaVersion + ", package=" + HybridCLRVersion + ", platform=" + Platform + ", unity=" + UnityVersion);
            }

            try
            {
                _ = new ResourceAddress(ProjectId, ManifestPath);
                _ = new ResourceAddress(AppContentCompatibilityId, ManifestPath);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Invalid HybridCLR project or base identity.", exception);
            }
            if (HotUpdateAssemblies == null || AotMetadata == null || HotUpdateAssemblies.Length == 0 ||
                HotUpdateAssemblies.Length > MaximumAssemblyCount || AotMetadata.Length > MaximumAssemblyCount)
            {
                throw new InvalidDataException("Invalid HybridCLR assembly count.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var payload in HotUpdateAssemblies.Concat(AotMetadata))
            {
                if (payload == null || string.IsNullOrEmpty(payload.Name) || !names.Add(payload.Name))
                {
                    throw new InvalidDataException("Missing, duplicate or case-colliding HybridCLR assembly.");
                }

                try
                {
                    _ = new ResourceAddress(payload.Name, ManifestPath);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("Invalid assembly name.", exception);
                }
                var identity = ReadIdentity(payload.Identity);
                var hasMvid = Guid.TryParseExact(payload.Mvid, "D", out var mvid) && mvid != Guid.Empty;
                if (identity.Name != payload.Name || identity.Version == null || identity.FullName != payload.Identity ||
                    !hasMvid)
                {
                    throw new InvalidDataException("Invalid HybridCLR assembly identity or MVID: " + payload.Name +
                        "; declared=" + payload.Identity + "; parsed=" + identity.FullName + "; parsedName=" + identity.Name +
                        "; parsedVersion=" + identity.Version + "; mvid=" + payload.Mvid + "; validMvid=" + hasMvid);
                }

                if (payload.Length <= 0 || payload.Length > MaximumPayloadBytes || !IsHash(payload.Sha256))
                {
                    throw new InvalidDataException("Invalid HybridCLR payload size or hash: " + payload.Name);
                }

                total += payload.Length;
                ValidateUniqueStrings(payload.Dependencies, payload.Name);
                ValidateUniqueStrings(payload.References, payload.Name);
                foreach (var reference in payload.References)
                {
                    ReadIdentity(reference);
                }

                if (ordered && (!payload.Dependencies.SequenceEqual(payload.Dependencies.OrderBy(n => n, StringComparer.Ordinal)) ||
                    !payload.References.SequenceEqual(payload.References.OrderBy(n => n, StringComparer.Ordinal))))
                {
                    throw new InvalidDataException("HybridCLR dependency descriptions must be canonical.");
                }
            }
            if (total > MaximumTotalBytes)
            {
                throw new InvalidDataException("HybridCLR payload budget exceeded.");
            }

            var hot = HotUpdateAssemblies.ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (var payload in HotUpdateAssemblies)
            {
                if (payload.Address != PayloadAddress(payload.Name, false))
                {
                    throw new InvalidDataException("HybridCLR assembly address escaped its owned project path: " + payload.Name);
                }

                foreach (var dependency in payload.Dependencies)
                {
                    if (!hot.ContainsKey(dependency))
                    {
                        throw new InvalidDataException("Missing hot-update dependency: " + dependency);
                    }
                }

                var expected = payload.References.Select(ReadIdentity).Where(r => hot.ContainsKey(r.Name)).ToArray();
                if (expected.Any(r => hot[r.Name].Identity != r.FullName) ||
                    !expected.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal)
                        .SequenceEqual(payload.Dependencies.OrderBy(n => n, StringComparer.Ordinal)))
                {
                    throw new InvalidDataException("Hot-update dependency identity mismatch: " + payload.Name);
                }
            }
            foreach (var payload in AotMetadata)
            {
                if (payload.Address != PayloadAddress(payload.Name, true) || payload.Dependencies.Length != 0 ||
                    payload.References.Any(r => hot.ContainsKey(ReadIdentity(r).Name)))
                {
                    throw new InvalidDataException("Invalid AOT address or AOT-to-hot-update reference: " + payload.Name);
                }
            }

            var dependencyOrder = DependencyOrder();
            if (ordered && (!HotUpdateAssemblies.Select(p => p.Name).SequenceEqual(dependencyOrder.Select(p => p.Name)) ||
                !AotMetadata.Select(p => p.Name).SequenceEqual(AotMetadata.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))))
            {
                throw new InvalidDataException("HybridCLR assemblies are not in stable dependency-first order.");
            }
        }

        // 对热更依赖做稳定拓扑排序，循环依赖不能进入原生加载阶段。
        private HybridCLRPayload[] DependencyOrder()
        {
            var remaining = HotUpdateAssemblies.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<HybridCLRPayload>();
            while (remaining.Count != 0)
            {
                var next = remaining.FirstOrDefault(p => p.Dependencies.All(loaded.Contains));
                if (next == null)
                {
                    throw new InvalidDataException("Cyclic hot-update assembly dependencies.");
                }

                remaining.Remove(next);
                loaded.Add(next.Name);
                ordered.Add(next);
            }
            return ordered.ToArray();
        }

        // 识别清单要求的小写 SHA256 文本。
        private static bool IsHash(string value) => value != null && value.Length == 64 &&
            value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        // 限制引用列表大小并拒绝空值及大小写碰撞。
        private static void ValidateUniqueStrings(string[] values, string assembly)
        {
            if (values == null || values.Length > 512 || values.Any(string.IsNullOrWhiteSpace) ||
                values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            {
                throw new InvalidDataException("Missing, duplicate or oversized dependencies: " + assembly);
            }
        }

        // 只归一化缺失 culture，不放宽完整程序集身份的其他部分。
        internal static string CanonicalIdentity(AssemblyName identity)
        {
            if (identity.CultureInfo == null)
            {
                identity.CultureInfo = CultureInfo.InvariantCulture;
            }

            return identity.FullName;
        }

        // 读取有界程序集身份并处理 IL2CPP 的显式 neutral culture 差异。
        private static AssemblyName ReadIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            {
                throw new InvalidDataException("Missing or oversized assembly identity.");
            }

            try
            {
                var identity = new AssemblyName(value);
                // Unity IL2CPP 将显式 neutral 转为 null；只还原这条精确语义，不放宽身份比较。
                if (value.IndexOf(", Culture=neutral, ", StringComparison.Ordinal) >= 0 && identity.CultureInfo == null)
                {
                    CanonicalIdentity(identity);
                }

                return identity;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is System.IO.FileLoadException)
            {
                throw new InvalidDataException("Invalid assembly identity: " + value, exception);
            }
        }
    }
}
