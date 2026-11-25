using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YLFramework.Resource
{
    internal static class ResourceContentManifestSignature
    {
        private const string Magic = "YLR1";
        private const int SignatureLength = 384;
        private const int KeyIdOffset = 5;

        internal static byte[] Verify(byte[] payload, byte[] envelope, ResourceContentManifestTrust trust)
        {
            if (envelope == null || envelope.Length <= KeyIdOffset + SignatureLength ||
                envelope.Length > ResourceContentManifest.MaximumSignatureEnvelopeBytes)
            {
                throw new InvalidDataException("Resource content signature envelope is outside its size bound.");
            }

            byte[] snapshot = (byte[])envelope.Clone();
            int keyIdLength = snapshot[Magic.Length];
            if (snapshot.Length != KeyIdOffset + keyIdLength + SignatureLength ||
                Encoding.ASCII.GetString(snapshot, 0, Magic.Length) != Magic)
            {
                throw new InvalidDataException("Malformed resource content signature envelope.");
            }

            string keyId = Encoding.ASCII.GetString(snapshot, KeyIdOffset, keyIdLength);
            if (!ResourceContentManifestTrust.IsId(keyId) || !trust.TryGetPublicKey(keyId, out RSAParameters publicKey))
            {
                throw new InvalidDataException("Unknown resource content signing key.");
            }

            var signature = new byte[SignatureLength];
            Buffer.BlockCopy(snapshot, KeyIdOffset + keyIdLength, signature, 0, SignatureLength);
            using var sha256 = SHA256.Create();
            byte[] digest = sha256.ComputeHash(payload);
            bool verified;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(publicKey);
                verified = rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException error)
            {
                throw new InvalidDataException("Could not verify resource content signature.", error);
            }

            if (!verified)
            {
                throw new InvalidDataException("Invalid resource content signature.");
            }
            return digest;
        }

        internal static RSAParameters CopyPublicKey(RSAParameters key)
        {
            byte[] modulus = key.Modulus;
            byte[] exponent = key.Exponent;
            if (modulus == null || modulus.Length != SignatureLength || modulus[0] < 0x80 ||
                (modulus[modulus.Length - 1] & 1) == 0 || exponent == null || exponent.Length is < 1 or > 4 ||
                exponent[0] == 0 || (exponent.Length == 1 && exponent[0] < 3) ||
                (exponent[exponent.Length - 1] & 1) == 0)
            {
                throw new ArgumentException("Expected an RSA-3072 public modulus and exponent.", nameof(key));
            }

            return new RSAParameters { Modulus = (byte[])modulus.Clone(), Exponent = (byte[])exponent.Clone() };
        }

        internal static byte[] Envelope(string keyId, byte[] signature)
        {
            if (!ResourceContentManifestTrust.IsId(keyId) || signature.Length != SignatureLength)
            {
                throw new ArgumentException("Expected a signing key identifier and an RSA-3072 signature.");
            }

            var envelope = new byte[KeyIdOffset + keyId.Length + SignatureLength];
            Encoding.ASCII.GetBytes(Magic).CopyTo(envelope, 0);
            envelope[Magic.Length] = (byte)keyId.Length;
            Encoding.ASCII.GetBytes(keyId).CopyTo(envelope, KeyIdOffset);
            signature.CopyTo(envelope, KeyIdOffset + keyId.Length);
            return envelope;
        }
    }
}
