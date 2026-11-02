using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace YLFramework.Resource
{
    /// <summary>发布签名只认证确切字节；信任公钥必须来自应用配置，绝不从远端导入信任根。</summary>
    internal static class ResourceReleaseSignature
    {
        internal const string Algorithm = "RSA-SHA256-PKCS1";
        internal const int MaximumBytes = 64 * 1024;

        /// <summary>检查有界发布信封的字段、算法与规范字节形式；此步骤尚未验签。</summary>
        internal static JObject ReadEnvelope(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaximumBytes)
            {
                throw new InvalidDataException("Release envelope exceeds its bound.");
            }

            var data = ResourceCatalogCodec.ParseObject(bytes);
            ResourceCatalogCodec.Fields(data, "SchemaVersion,Algorithm,KeyId,Payload,Signature");
            if (ResourceCatalogCodec.Integer(data, "SchemaVersion") != 1 || ResourceCatalogCodec.Text(data, "Algorithm") != Algorithm ||
                !bytes.SequenceEqual(ResourceCatalogCodec.CanonicalBytes(data)))
            {
                throw new InvalidDataException("Unsupported or noncanonical release envelope.");
            }

            ResourceCatalogCodec.Id(ResourceCatalogCodec.Text(data, "KeyId"));
            return data;
        }

        /// <summary>解码指定字段并要求非空、有界且编码形式规范的 Base64 数据。</summary>
        internal static byte[] Base64(JObject data, string field, int maximum)
        {
            if (data[field]?.Type != JTokenType.String || ((string)data[field]).Length > (maximum + 2L) / 3 * 4)
            {
                throw new InvalidDataException("Invalid bounded release encoding.");
            }

            try
            {
                var bytes = Convert.FromBase64String((string)data[field]);
                if (bytes.Length == 0 || bytes.Length > maximum || Convert.ToBase64String(bytes) != (string)data[field])
                {
                    throw new InvalidDataException("Noncanonical release encoding.");
                }

                return bytes;
            }
            catch (FormatException error)
            {
                throw new InvalidDataException("Invalid release encoding.", error);
            }
        }

        /// <summary>用指定可信公钥验证移除 Signature 字段后的规范信封，再解析规范 Payload。</summary>
        internal static JObject Verify(byte[] bytes, string trustedKeyId, RSAParameters publicKey)
        {
            var envelope = ReadEnvelope(bytes);
            if ((string)envelope["KeyId"] != trustedKeyId)
            {
                throw new InvalidDataException("Release signing key is not trusted.");
            }

            var signature = Base64(envelope, "Signature", 1024);
            var payload = Base64(envelope, "Payload", 32 * 1024);
            envelope.Remove("Signature");
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = publicKey.Modulus, Exponent = publicKey.Exponent });
                if (rsa.KeySize < 2048 || rsa.KeySize > 8192 || signature.Length != rsa.KeySize / 8 ||
                    !rsa.VerifyData(ResourceCatalogCodec.CanonicalBytes(envelope), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    throw new InvalidDataException("Release signature rejected.");
                }
            }
            catch (CryptographicException error)
            {
                throw new InvalidDataException("Release signature rejected.", error);
            }
            var data = ResourceCatalogCodec.ParseObject(payload);
            if (!payload.SequenceEqual(ResourceCatalogCodec.CanonicalBytes(data)))
            {
                throw new InvalidDataException("Noncanonical signed payload.");
            }

            return data;
        }
    }
}
