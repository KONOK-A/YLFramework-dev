using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace YLFramework.Testing
{
    public sealed class ResourceMigrationDeletion : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly string _path;
        private readonly string _sha256;
        private readonly string _guid;
        private readonly string _openedName;
        private readonly string _identity;
        private bool _marked;
        private bool _closed;

        public FileStream Stream { get; }
        public string Identity => _identity;

        private ResourceMigrationDeletion(string path, string sha256, string guid)
        {
            _path = ExtendedPath(path);
            _sha256 = sha256;
            _guid = guid;
            // DELETE access is required for disposition on this handle. No path-based Delete fallback.
            _handle = CreateFileW(_path, 0xC0010000, 0, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero);
            if (_handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                _handle.Dispose();
                throw new Win32Exception(error, "Cannot exclusively open the deletion target: " + new Win32Exception(error).Message);
            }
            try
            {
                Stream = new FileStream(_handle, FileAccess.Read, 4096, false);
                _identity = ReadIdentity();
                _openedName = ReadName();
                VerifyBytes();
            }
            catch
            {
                try { Stream?.Dispose(); }
                finally { _handle.Dispose(); }
                throw;
            }
        }

        public static ResourceMigrationDeletion OpenVerified(string path, string sha256, string guid)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("Handle-bound migration deletion supports Windows only.");
            if (!Regex.IsMatch(sha256 ?? "", "^[0-9a-f]{64}$") || (!string.IsNullOrEmpty(guid) && !Regex.IsMatch(guid, "^[0-9a-f]{32}$")))
                throw new ArgumentException("Deletion requires an explicit original SHA256 and, for metadata, original GUID.");
            return new ResourceMigrationDeletion(path, sha256, guid);
        }

        public bool IsDeletePending
        {
            get
            {
                EnsureOpen();
                if (!GetFileInformationByHandleEx(_handle, 1, out StandardInfo info, (uint)Marshal.SizeOf<StandardInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
                return info.DeletePending != 0;
            }
        }

        public void MarkForDeletion()
        {
            EnsureOpen();
            if (_marked) throw new InvalidOperationException("The deletion handle is already marked.");
            if (ReadIdentity() != _identity || !string.Equals(ReadName(), _openedName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The verified deletion target identity or opened name changed.");
            VerifyBytes();
            var disposition = new DispositionInfo { DeleteFile = 1 };
            if (!SetFileInformationByHandle(_handle, 4, ref disposition, (uint)Marshal.SizeOf<DispositionInfo>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _marked = true;
            if (!IsDeletePending) throw new IOException("The same deletion handle did not enter DeletePending state.");
        }

        public void VerifyFinalAbsence()
        {
            if (!_closed) throw new InvalidOperationException("Close the deletion handle first; DeletePending is not final absence.");
            if (!_marked) throw new InvalidOperationException("An unmarked deletion handle cannot prove absence.");
            uint attributes = GetFileAttributesW(_path);
            if (attributes != uint.MaxValue) throw new IOException("Deletion target name is still present or occupied by another identity; preserve the conflict.");
            int error = Marshal.GetLastWin32Error();
            if (error != 2 && error != 3) throw new Win32Exception(error, "Final deletion absence could not be verified.");
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { Stream.Dispose(); }
            finally { _handle.Dispose(); }
        }

        private void VerifyBytes()
        {
            EnsureOpen();
            Stream.Position = 0;
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Stream)).Replace("-", "").ToLowerInvariant();
            if (hash != _sha256) throw new IOException("Deletion target SHA changed on the verified handle.");
            Stream.Position = 0;
            if (!string.IsNullOrEmpty(_guid))
            {
                string text;
                using (var reader = new StreamReader(Stream, Encoding.UTF8, true, 1024, true)) text = reader.ReadToEnd();
                var matches = Regex.Matches(text, "(?m)^guid: ([0-9a-f]{32})\\r?$");
                if (matches.Count != 1 || matches[0].Groups[1].Value != _guid) throw new IOException("Deletion metadata GUID differs on the verified handle.");
            }
            Stream.Position = 0;
        }

        private string ReadIdentity()
        {
            if (!GetFileInformationByHandle(_handle, out HandleInfo info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & 0x410) != 0) throw new IOException("Directories and reparse points are not supported deletion targets.");
            return info.VolumeSerial.ToString("x8") + ":" + info.FileIndexHigh.ToString("x8") + info.FileIndexLow.ToString("x8");
        }

        private string ReadName()
        {
            var name = new StringBuilder(512);
            uint length = GetFinalPathNameByHandleW(_handle, name, (uint)name.Capacity, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length >= name.Capacity)
            {
                name = new StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandleW(_handle, name, (uint)name.Capacity, 0);
                if (length == 0 || length >= name.Capacity) throw new IOException("Cannot verify the opened deletion path.");
            }
            return name.ToString();
        }

        private void EnsureOpen()
        {
            if (_closed || _handle.IsClosed) throw new ObjectDisposedException(nameof(ResourceMigrationDeletion), "The deletion handle is closed.");
        }

        private static string ExtendedPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith("\\\\.\\", StringComparison.Ordinal)) throw new ArgumentException("Device paths are not deletion targets.");
            if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) return full;
            return full.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + full.Substring(2) : "\\\\?\\" + full;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DispositionInfo { public int DeleteFile; }

        [StructLayout(LayoutKind.Sequential)]
        private struct StandardInfo
        {
            public long AllocationSize, EndOfFile;
            public uint Links;
            public byte DeletePending, Directory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out StandardInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref DispositionInfo info, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);
    }
}
