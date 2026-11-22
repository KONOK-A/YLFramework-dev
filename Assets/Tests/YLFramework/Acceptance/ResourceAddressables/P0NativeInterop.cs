using System;
using System.IO;
using System.Runtime.InteropServices;

namespace YLFramework.Testing.NativeP0
{
    public sealed class P0NativeReadStream : Stream
    {
        private const string Library = "ylresource_p0";
        private const int Limit = 1024 * 1024;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ylp0_encrypt(string source, string destination, [In] byte[] key);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr ylp0_open(string path, [In] byte[] key, out long length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int ylp0_read(IntPtr handle, long position, IntPtr output, int length);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void ylp0_close(IntPtr handle);

        private readonly object _sync = new object();
        private readonly long _length;
        private IntPtr _handle;
        private long _position;

        public static void Encrypt(string source, string destination, byte[] key)
        {
            RequireKey(key);
            int result = ylp0_encrypt(source, destination, key);
            if (result != 0) throw new IOException("Fixture Tink export failed: " + result);
        }

        private static void RequireKey(byte[] key)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("Expected the random 256-bit fixture key.", nameof(key));
        }

        public P0NativeReadStream(string path, byte[] key)
        {
            RequireKey(key);
            _handle = ylp0_open(path, key, out _length);
            if (_handle == IntPtr.Zero) throw new IOException("Fixture Tink reader refused the sealed file.");
        }

        public override long Length => _length;
        public override long Position { get { lock (_sync) return _position; } set => Seek(value, SeekOrigin.Begin); }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_sync)
            {
                EnsureOpen();
                long basis;
                switch (origin)
                {
                    case SeekOrigin.Begin: basis = 0; break;
                    case SeekOrigin.Current: basis = _position; break;
                    case SeekOrigin.End: basis = _length; break;
                    default: throw new ArgumentOutOfRangeException(nameof(origin));
                }
                long next = checked(basis + offset);
                if (next < 0) throw new IOException("Cannot seek before the fixture stream.");
                return _position = next;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || count > buffer.Length - offset) throw new ArgumentOutOfRangeException(nameof(count));
            lock (_sync)
            {
                EnsureOpen();
                int goal = (int)Math.Min(count, Math.Max(0, _length - _position));
                if (goal == 0) return 0;
                var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    int remaining = goal;
                    while (remaining > 0)
                    {
                        int size = Math.Min(remaining, Limit);
                        int read = ylp0_read(_handle, _position, IntPtr.Add(pin.AddrOfPinnedObject(), offset), size);
                        if (read != size) throw new IOException("Fixture Tink authentication or exact read failed: " + read);
                        _position += read;
                        offset += read;
                        remaining -= read;
                    }
                    return goal;
                }
                finally { pin.Free(); }
            }
        }

        private void EnsureOpen()
        {
            if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(P0NativeReadStream));
        }

        protected override void Dispose(bool disposing)
        {
            lock (_sync)
            {
                var old = _handle;
                _handle = IntPtr.Zero;
                if (old != IntPtr.Zero) ylp0_close(old);
            }
            base.Dispose(disposing);
        }
    }
}
