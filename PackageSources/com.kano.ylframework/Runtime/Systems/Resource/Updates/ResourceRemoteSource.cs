using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Networking;

namespace YLFramework.Resource
{
    /// <summary>应用显式信任的 RSA 发布验签公钥；只保存公钥参数，不含私钥。</summary>
    public sealed class ResourceSigningKey
    {
        /// <summary>用于在发布信封中选择此可信公钥的标识。</summary>
        public string KeyId
        {
            get;
        }
        private readonly byte[] _modulus;
        private readonly byte[] _exponent;
        /// <summary>提供公钥参数副本，避免验签调用修改内部数组。</summary>
        internal RSAParameters Parameters => new() { Modulus = (byte[])_modulus.Clone(), Exponent = (byte[])_exponent.Clone() };

        /// <summary>解码并验证公钥参数，要求 2048 至 8192 位模数及规范的奇数指数。</summary>
        /// <param name="keyId">应用配置的公钥标识。</param>
        /// <param name="modulusBase64">RSA 公钥模数的 Base64 编码。</param>
        /// <param name="exponentBase64">RSA 公钥指数的 Base64 编码。</param>
        public ResourceSigningKey(string keyId, string modulusBase64, string exponentBase64)
        {
            ResourceCatalogCodec.Id(keyId);
            try
            {
                _modulus = Convert.FromBase64String(modulusBase64);
                _exponent = Convert.FromBase64String(exponentBase64);
            }
            catch (FormatException error)
            {
                throw new ArgumentException("Invalid RSA public key encoding.", error);
            }
            if (_modulus.Length < 256 || _modulus.Length > 1024 || _modulus[0] < 128 || _exponent.Length == 0 || _exponent.Length > 8 ||
                _exponent[0] == 0 || (_exponent[_exponent.Length - 1] & 1) == 0 || _exponent.Length == 1 && _exponent[0] < 3)
            {
                throw new ArgumentException("RSA public keys require 2048-8192 bits and a canonical odd exponent of at least 3.");
            }

            using var rsa = RSA.Create();
            rsa.ImportParameters(Parameters);
            KeyId = keyId;
        }
    }

    /// <summary>应用显式绑定的HTTPS发布根与channel。没有默认公钥、不接受远端公钥或任意URL。</summary>
    public sealed class ResourceRemoteSource
    {
        /// <summary>规范化且以斜杠结尾的 HTTPS 发布目录，不含凭据、查询或片段。</summary>
        public Uri Endpoint
        {
            get;
        }
        /// <summary>此来源唯一接受的发布频道。</summary>
        public string Channel
        {
            get;
        }
        /// <summary>应用配置的最低发布序号；Store 的已确认历史还可进一步抬高门槛。</summary>
        public long MinimumReleaseSequence
        {
            get;
        }
        /// <summary>Catalog 中每个 Bundle 或支持文件允许的最大交付字节数。</summary>
        public long MaximumFileBytes
        {
            get;
        }
        /// <summary>单次发布完整文件集合的字节上限，含 Catalog 与 Complete 文件。</summary>
        public long MaximumDownloadBytes
        {
            get;
        }
        /// <summary>每次 GET 尝试允许的总时长，单位为秒。</summary>
        public int TimeoutSeconds
        {
            get;
        }
        /// <summary>每次 GET 尝试连续未收到数据的超时时长，单位为秒。</summary>
        public int IdleTimeoutSeconds
        {
            get;
        }
        /// <summary>单个 GET 的最多尝试次数，包含首次请求。</summary>
        public int Attempts
        {
            get;
        }
        /// <summary>应用显式提供且标识互异的可信验签公钥集合。</summary>
        internal IReadOnlyList<ResourceSigningKey> Keys
        {
            get;
        }
#if UNITY_EDITOR || (UNITY_STANDALONE_WIN && (ENABLE_MONO || ENABLE_IL2CPP) && DEVELOPMENT_BUILD && YL_RESOURCE_ACCEPTANCE_TESTS)
        private byte[] _loopbackCertificate;
#endif

        /// <summary>绑定发布根、可信公钥与有界下载策略，不从远端扩充信任集合。</summary>
        /// <param name="endpoint">规范的 HTTPS 目录地址，须以斜杠结尾。</param>
        /// <param name="channel">要求的发布频道标识。</param>
        /// <param name="trustedKeys">应用配置的可信公钥，数量为 1 至 16。</param>
        /// <param name="minimumReleaseSequence">最低发布序号，至少为 1。</param>
        /// <param name="maximumFileBytes">单个内容文件的交付字节上限。</param>
        /// <param name="maximumDownloadBytes">完整发布文件集合的交付字节上限。</param>
        /// <param name="timeoutSeconds">每次 GET 尝试的总超时秒数，范围为 1 至 600。</param>
        /// <param name="idleTimeoutSeconds">无数据超时秒数，不得超过总超时。</param>
        /// <param name="attempts">包含首次 GET 的尝试次数，范围为 1 至 5。</param>
        public ResourceRemoteSource(string endpoint, string channel, IEnumerable<ResourceSigningKey> trustedKeys,
            long minimumReleaseSequence = 1, long maximumFileBytes = 1024L * 1024 * 1024, long maximumDownloadBytes = 4L * 1024 * 1024 * 1024,
            int timeoutSeconds = 60, int idleTimeoutSeconds = 15, int attempts = 3)
        {
            if (string.IsNullOrEmpty(endpoint) || endpoint.Length > 2048 || endpoint.IndexOfAny(new[] { '\\', '%', '?', '#' }) >= 0 ||
                !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) ||
                !endpoint.EndsWith("/", StringComparison.Ordinal) || endpoint != uri.AbsoluteUri || uri.AbsolutePath.Split('/').Any(x => x == "." || x == ".."))
            {
                throw new ArgumentException("A canonical HTTPS directory endpoint without credentials, query or escapes is required.");
            }

            ResourceCatalogCodec.Id(channel);
            Keys = ResourceCatalog.Copy(trustedKeys ?? throw new ArgumentNullException(nameof(trustedKeys)));
            if (Keys.Count == 0 || Keys.Count > 16 || Keys.Any(x => x == null) || Keys.Select(x => x.KeyId).Distinct(StringComparer.Ordinal).Count() != Keys.Count)
            {
                throw new ArgumentException("Explicit distinct trusted signing keys are required.");
            }

            if (minimumReleaseSequence < 1 || maximumFileBytes < 1 || maximumDownloadBytes < maximumFileBytes ||
                timeoutSeconds < 1 || timeoutSeconds > 600 || idleTimeoutSeconds < 1 || idleTimeoutSeconds > timeoutSeconds || attempts < 1 || attempts > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumDownloadBytes), "Invalid bounded download policy.");
            }

            Endpoint = uri;
            Channel = channel;
            MinimumReleaseSequence = minimumReleaseSequence;
            MaximumFileBytes = maximumFileBytes;
            MaximumDownloadBytes = maximumDownloadBytes;
            TimeoutSeconds = timeoutSeconds;
            IdleTimeoutSeconds = idleTimeoutSeconds;
            Attempts = attempts;
        }

        /// <summary>将受限的相对发布路径解析到同一 HTTPS 来源根内。</summary>
        internal Uri Resolve(string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.Split('/').Any(x => x.Length == 0 || x == "." || x == "..") ||
                relative.Any(x => !(x >= 'a' && x <= 'z' || x >= 'A' && x <= 'Z' || x >= '0' && x <= '9' || x == '-' || x == '_' || x == '.' || x == '/')))
            {
                throw new InvalidDataException("Remote path is outside the release root.");
            }

            var uri = new Uri(Endpoint, relative);
            if (!Endpoint.IsBaseOf(uri) || uri.Scheme != "https" || uri.Authority != Endpoint.Authority)
            {
                throw new InvalidDataException("Remote path escaped its origin.");
            }

            return uri;
        }

        internal static bool IsAcceptedPlayerVersion(string version) => version is "2022.3.62f3c1" or "6000.3.27f1";

        /// <summary>拒绝尚未开放的传输与密码运行时组合。</summary>
        internal static void RequireSupportedRuntime()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                return;
            }
#if UNITY_STANDALONE_WIN && (ENABLE_MONO || ENABLE_IL2CPP) && !UNITY_EDITOR
            // 固定已验/候选引擎、架构及脚本后端；候选须完成对应原生链后才作为发行支持。
            if (Application.platform == RuntimePlatform.WindowsPlayer && IntPtr.Size == 8 && IsAcceptedPlayerVersion(Application.unityVersion)) return;
#endif
            throw new ResourceException(ResourceError.Unsupported, "Release schema v1 transport/crypto requires Windows Editor or the fixed Unity 2022.3.62f3c1/6000.3.27f1 Windows64 Mono/IL2CPP Player candidate backend.");
        }

        /// <summary>仅在隔离验收配置中创建回环证书处理器，普通路径交由平台验证。</summary>
        internal CertificateHandler CreateCertificateHandler()
        {
#if UNITY_EDITOR || (UNITY_STANDALONE_WIN && (ENABLE_MONO || ENABLE_IL2CPP) && DEVELOPMENT_BUILD && YL_RESOURCE_ACCEPTANCE_TESTS)
            if (_loopbackCertificate != null)
            {
                if (!Endpoint.IsLoopback)
                {
                    throw new InvalidOperationException("Isolated certificate trust is restricted to loopback.");
                }

                return new LoopbackCertificate(_loopbackCertificate);
            }
#endif
            return null; // Unity/平台正常证书链与主机名校验；生产没有允许未知证书的开关。
        }

#if UNITY_EDITOR || (UNITY_STANDALONE_WIN && (ENABLE_MONO || ENABLE_IL2CPP) && DEVELOPMENT_BUILD && YL_RESOURCE_ACCEPTANCE_TESTS)
        /// <summary>仅Editor或显式Development验收构建的隔离TLS夹具信任；不进入普通Player，不修改系统信任。</summary>
        internal void TrustLoopbackCertificate(byte[] certificate)
        {
            if (!Endpoint.IsLoopback || _loopbackCertificate != null || certificate == null || certificate.Length == 0 || certificate.Length > 16384)
            {
                throw new InvalidOperationException("Explicit isolated loopback trust is required.");
            }

            _loopbackCertificate = (byte[])certificate.Clone();
        }
        /// <summary>隔离回环夹具的证书字节匹配器，不作为生产信任策略。</summary>
        private sealed class LoopbackCertificate : CertificateHandler
        {
            private readonly byte[] _certificate;
            /// <summary>保存已由来源配置限定的夹具证书。</summary>
            internal LoopbackCertificate(byte[] certificate) => this._certificate = certificate;
            /// <summary>仅接受与显式夹具证书逐字节一致的证书。</summary>
            /// <param name="received">Unity 交付的服务端证书字节。</param>
            protected override bool ValidateCertificate(byte[] received) => received != null && received.SequenceEqual(_certificate);
        }
#endif
    }
}
