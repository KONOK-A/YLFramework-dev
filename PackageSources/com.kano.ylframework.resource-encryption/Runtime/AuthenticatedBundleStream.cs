using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace YLFramework.Resource.Encryption
{
    internal sealed class AuthenticatedBundleStream : Stream
    {
        private const string Library = "ylresource_tink";
        private const int ReadLimit = 128 * 1024;
        private readonly object _sync = new object();
        private readonly long _length;
        private readonly FileStream _cipher;
        private readonly EncryptedBundleKeyLease _key;
        private IntPtr _reader;
        private long _position;
        internal long PreAuthenticationCipherBytes
        {
            get; private set;
        }
        internal double CipherShaMs
        {
            get; private set;
        }
        internal double AuthenticationMs
        {
            get; private set;
        }
        private bool _failed;

        [DllImport(Library, EntryPoint = "yl_seal", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SealNative(SafeFileHandle plain, SafeFileHandle cipher, [In] byte[] key, [In] byte[] aad, int aadSize);

        [DllImport(Library, EntryPoint = "yl_open", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr OpenNative(SafeFileHandle cipher, [In] byte[] key, [In] byte[] aad, int aadSize, long expectedLength);

        [DllImport(Library, EntryPoint = "yl_read", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ReadNative(IntPtr reader, long position, IntPtr output, int count);

        [DllImport(Library, EntryPoint = "yl_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern void CloseNative(IntPtr reader);

        [DllImport(Library, EntryPoint = "yl_read_statistics", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ReadStatisticsNative(IntPtr reader, out long served, out long source, out long pages, out long nativePayloadBound);

        internal static Action<int, bool> HashBufferBoundary { get; set; }
        internal static Action<int> DiagnosticPhase { get; set; }

        private AuthenticatedBundleStream(FileStream cipher, EncryptedBundleKeyLease key, EncryptedBundleBinding binding)
        {
            _cipher = cipher;
            _key = key;
            _length = binding.PlainLength;
            byte[] aad = binding.AssociatedData();
            _reader = key.Use(material => OpenNative(cipher.SafeFileHandle, material, aad, aad.Length, _length));
            if (_reader == IntPtr.Zero)
            {
                throw new EncryptedBundleAuthenticationException();
            }
        }

        // Takes ownership of the exact handle used for both hashes, including on failure.
        internal static AuthenticatedBundleStream OpenVerified(FileStream cipher, EncryptedBundleRecord record,
            IEncryptedBundleKeySource keys, CancellationToken cancellationToken, Action<long> onCipherVerified = null,
            CancellationToken? sharedCipherValidation = null)
        {
            EncryptedBundleKeyLease key = null;
            AuthenticatedBundleStream result = null;
            var phase = DiagnosticPhase;
            try
            {
                phase?.Invoke(1);
                long started = Stopwatch.GetTimestamp();
                long checkedBytes = ValidateCipher(cipher, record, sharedCipherValidation ?? cancellationToken);
                double cipherShaMs = ElapsedMs(started);
                onCipherVerified?.Invoke(checkedBytes);
                cancellationToken.ThrowIfCancellationRequested();
                key = EncryptedBundleKeyLease.Rent(keys, record.Binding.KeyId);
                if (key == null)
                {
                    throw new EncryptedBundleKeyUnavailableException(record.Binding.KeyId);
                }
                started = Stopwatch.GetTimestamp();
                result = new AuthenticatedBundleStream(cipher, key, record.Binding);
                if (Hash(result, cancellationToken, clearBuffer: true) != record.Binding.PlainSha256)
                {
                    throw new EncryptedBundleAuthenticationException();
                }
                result.PreAuthenticationCipherBytes = result.ReadStatistics().Physical;
                result.CipherShaMs = cipherShaMs;
                result.AuthenticationMs = ElapsedMs(started);
                result.Position = 0;
                return result;
            }
            catch (Exception error)
            {
                try
                {
                    if (result != null)
                    {
                        result.Dispose();
                    }
                    else
                    {
                        try
                        {
                            cipher.Dispose();
                        }
                        finally { key?.Dispose(); }
                    }
                }
                catch (EncryptedBundleKeySourceException cleanup)
                {
                    throw new AggregateException("Encrypted Bundle preparation and key return both failed.", error, cleanup);
                }
                throw;
            }
            finally { phase?.Invoke(0); }
        }

        internal static long ValidateCipher(FileStream cipher, EncryptedBundleRecord record, CancellationToken token)
        {
            if (cipher.Length != record.CipherLength || HashCore(cipher, token, false, out long bytesRead) != record.CipherSha256 || bytesRead != record.CipherLength)
            {
                throw new EncryptedBundleIntegrityException("Cached Bundle ciphertext length or SHA-256 differs.");
            }
            return bytesRead;
        }

        internal static void Seal(FileStream plain, FileStream destination, EncryptedBundleBinding binding, EncryptedBundleKeyLease key)
        {
            if (destination.Length != 0 || plain.Length != binding.PlainLength || Hash(plain, CancellationToken.None) != binding.PlainSha256)
            {
                throw new InvalidDataException("Official plaintext or empty owned export file changed during authoring.");
            }
            byte[] aad = binding.AssociatedData();
            int result = key.Use(material => SealNative(plain.SafeFileHandle, destination.SafeFileHandle, material, aad, aad.Length));
            if (result != 0 || destination.Length != EncryptedBundleBinding.GetCipherLength(binding.PlainLength))
            {
                throw new IOException("Streaming AEAD export failed or produced the wrong length (status " + result + ").");
            }
        }

        internal static string Hash(Stream input, CancellationToken token, bool clearBuffer = false) => HashCore(input, token, clearBuffer, out _);

        internal static double ElapsedMs(long start) => (Stopwatch.GetTimestamp() - start) * (1000d / Stopwatch.Frequency);

        private static string HashCore(Stream input, CancellationToken token, bool clearBuffer, out long bytesRead)
        {
            input.Position = 0;
            bytesRead = 0;
            token.ThrowIfCancellationRequested();
            var buffer = new byte[ReadLimit];
            var observeBuffer = HashBufferBoundary;
            // Let the Windows primitive provider dispatch SHA-256, independent of Mono's managed implementation.
            IntPtr algorithm = IntPtr.Zero;
            try
            {
                observeBuffer?.Invoke(buffer.Length, true);
                CheckSha(BCryptOpenAlgorithmProvider(out algorithm, "SHA256", "Microsoft Primitive Provider", 0));
                CheckSha(BCryptCreateHash(algorithm, out IntPtr hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0));
                try
                {
                    var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        IntPtr bytes = pinned.AddrOfPinnedObject();
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            int read = input.Read(buffer, 0, buffer.Length);
                            if (read == 0)
                                break;
                            CheckSha(BCryptHashData(hash, bytes, read, 0));
                            bytesRead += read;
                        }
                        var digest = new byte[32];
                        CheckSha(BCryptFinishHash(hash, digest, digest.Length, 0));
                        return Hex(digest);
                    }
                    finally { pinned.Free(); }
                }
                finally { BCryptDestroyHash(hash); }
            }
            finally
            {
                if (algorithm != IntPtr.Zero)
                    BCryptCloseAlgorithmProvider(algorithm, 0);
                if (clearBuffer)
                {
                    Array.Clear(buffer, 0, buffer.Length);
                }
                observeBuffer?.Invoke(buffer.Length, false);
            }
        }

        private static void CheckSha(int status)
        {
            if (status < 0)
                throw new CryptographicException("Windows SHA-256 failed: NTSTATUS 0x" + status.ToString("X8"));
        }

        [DllImport("bcrypt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, uint flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr hashObject, uint objectSize, IntPtr secret, uint secretSize, uint flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptHashData(IntPtr hash, IntPtr input, int byteCount, uint flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptFinishHash(IntPtr hash, [Out] byte[] output, int byteCount, uint flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptDestroyHash(IntPtr hash);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);

        internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        internal (long Logical, long Physical, long Pages, long PayloadBound) ReadStatistics()
        {
            lock (_sync)
            {
                CheckReadable();
                ReadStatisticsNative(_reader, out long logical, out long physical, out long pages, out long nativePayloadBound);
                // Largest managed hash buffer plus the stable FileStream's buffer. Unity's 32 KiB read buffer is separate.
                return (logical, physical, pages, nativePayloadBound + ReadLimit + 4096);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get
            {
                lock (_sync)
                {
                    return _position;
                }
            }
            set => Seek(value, SeekOrigin.Begin);
        }
        public override void Flush()
        {
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_sync)
            {
                CheckReadable();
                long start = origin switch
                {
                    SeekOrigin.Begin => 0,
                    SeekOrigin.Current => _position,
                    SeekOrigin.End => _length,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin))
                };
                long next = checked(start + offset);
                if (next < 0)
                {
                    throw new IOException("Cannot seek before the Bundle stream.");
                }
                return _position = next;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));
            lock (_sync)
            {
                CheckReadable();
                int size = (int)Math.Min(count, Math.Max(0, _length - _position));
                if (size == 0)
                {
                    return 0;
                }
                var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                var phase = PreAuthenticationCipherBytes > 0 ? DiagnosticPhase : null;
                try
                {
                    phase?.Invoke(2);
                    int done = 0;
                    while (done < size)
                    {
                        int page = Math.Min(ReadLimit, size - done);
                        if (ReadNative(_reader, _position + done, IntPtr.Add(pin.AddrOfPinnedObject(), offset + done), page) != page)
                        {
                            _failed = true;
                            Array.Clear(buffer, offset, size);
                            throw new EncryptedBundleAuthenticationException();
                        }
                        done += page;
                    }
                    _position += done;
                    return done;
                }
                finally
                {
                    pin.Free();
                    phase?.Invoke(0);
                }
            }
        }

        private void CheckReadable()
        {
            if (_reader == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(AuthenticatedBundleStream));
            if (_failed)
                throw new EncryptedBundleAuthenticationException();
        }

        protected override void Dispose(bool disposing)
        {
            lock (_sync)
            {
                var reader = _reader;
                if (reader != IntPtr.Zero)
                {
                    _reader = IntPtr.Zero;
                    try
                    {
                        CloseNative(reader);
                    }
                    finally
                    {
                        try
                        {
                            _cipher.Dispose();
                        }
                        finally { _key.Dispose(); }
                    }
                }
            }
            base.Dispose(disposing);
        }
    }
}
