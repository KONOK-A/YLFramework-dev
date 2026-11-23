using System;
using System.IO;

namespace YLFramework.Resource.Encryption
{
    /// <summary>Supplies a local, already prepared key for the exact requested identifier.</summary>
    public interface IEncryptedBundleKeySource
    {
        /// <summary>Called on a worker thread. Return null for an unavailable identifier; do not fetch a network key here.</summary>
        EncryptedBundleKeyLease OpenReadKey(string keyId);
    }

    /// <summary>Owns an independent copy of the key until the reader and its native Bundle have been released.</summary>
    public sealed class EncryptedBundleKeyLease : IDisposable
    {
        private readonly object _sync = new object();
        private byte[] _material;
        private Action _onReturn;

        /// <summary>Copies a 32-byte key. The optional release callback can run on a worker or the Unity thread.</summary>
        public EncryptedBundleKeyLease(byte[] key, Action onReturn = null)
        {
            if (key == null || key.Length != 32)
            {
                throw new ArgumentException("The streaming AEAD format requires a 32-byte key.", nameof(key));
            }
            _material = (byte[])key.Clone();
            _onReturn = onReturn;
        }

        internal T Use<T>(Func<byte[], T> copyToNative)
        {
            lock (_sync)
            {
                if (_material == null)
                {
                    throw new ObjectDisposedException(nameof(EncryptedBundleKeyLease));
                }
                return copyToNative(_material);
            }
        }

        internal static EncryptedBundleKeyLease Rent(IEncryptedBundleKeySource source, string keyId)
        {
            try
            {
                return source.OpenReadKey(keyId);
            }
            catch (Exception) { throw new EncryptedBundleKeySourceException("Encrypted Bundle key source could not open key " + keyId + "."); }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Action onReturn;
            lock (_sync)
            {
                if (_material == null)
                {
                    return;
                }
                Array.Clear(_material, 0, _material.Length);
                _material = null;
                onReturn = _onReturn;
                _onReturn = null;
            }
            try
            {
                onReturn?.Invoke();
            }
            catch (Exception) { throw new EncryptedBundleKeySourceException("Encrypted Bundle key return callback failed after clearing the lease."); }
        }
    }

    internal sealed class EncryptedBundleKeySourceException : IOException
    {
        internal EncryptedBundleKeySourceException(string message) : base(message) { }
    }

    /// <summary>No key for the exact identifier was provided by the application.</summary>
    public sealed class EncryptedBundleKeyUnavailableException : IOException
    {
        internal EncryptedBundleKeyUnavailableException(string keyId) : base("No encrypted Bundle key is available for: " + keyId) { }
    }

    /// <summary>The selected key or bound content did not authenticate.</summary>
    public sealed class EncryptedBundleAuthenticationException : IOException
    {
        internal EncryptedBundleAuthenticationException() : base("Encrypted Bundle authentication failed.") { }
    }

    /// <summary>The local or received bytes do not match the frozen ciphertext identity.</summary>
    public sealed class EncryptedBundleIntegrityException : IOException
    {
        internal EncryptedBundleIntegrityException(string message, Exception inner = null) : base(message, inner) { }
    }
}
