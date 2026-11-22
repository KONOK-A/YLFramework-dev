using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YLFramework.Resource.Encryption
{
    internal static class OwnedWindowsFiles
    {
        private const uint ReadAccess = 0x80000000;
        private const uint WriteAccess = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint ListDirectory = 1;
        private const uint ReadShare = 1;
        private const uint DirectoryShare = ReadShare | 2; // Allow creation of children, not rename/deletion of the directory itself.
        private const uint OpenExisting = 3;
        private const uint CreateNew = 1;
        private const uint OpenAlways = 4;
        private const uint OpenLinkItself = 0x00200000;
        private const uint BackupSemantics = 0x02000000;
        private const uint ReparsePoint = 0x400;
        private const uint DirectoryBit = 0x10;
        private const uint NormalizedVolumeGuid = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInfoByHandle
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
                VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle Open(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoByHandle info);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] value, uint size);

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectory(string path, IntPtr security);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetFinalPath(SafeFileHandle handle, StringBuilder name, uint size, uint flags);

        internal static string LocalAbsolute(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8)
            {
                throw new PlatformNotSupportedException("Encrypted Bundle files require local Win64 storage.");
            }
            if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
            {
                throw new ArgumentException("Expected an absolute local Windows path.", nameof(path));
            }
            foreach (string part in path.Substring(3).Replace('/', '\\').TrimEnd('\\').Split('\\'))
            {
                if (part.Length != 0 && (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                {
                    throw new IOException("Unexpected Windows path component.");
                }
            }
            return Path.GetFullPath(path);
        }

        internal static IDisposable PinDirectory(string path, bool create)
            => WalkDirectory(path, create, false, out _);

        // Can describe a not-yet-created target without touching disk. Its existing ancestors remain pinned.
        internal static IDisposable InspectDirectory(string path, out string physicalPath)
            => WalkDirectory(path, false, true, out physicalPath);

        private static IDisposable WalkDirectory(string path, bool create, bool inspect, out string physicalPath)
        {
            path = LocalAbsolute(path);
            string volume = Path.GetPathRoot(path);
            var pins = new Pins();
            try
            {
                pins.Handles.Add(OpenChecked(volume, ListDirectory, DirectoryShare, OpenExisting, directory: true));
                physicalPath = inspect ? VolumePath(pins.Handles[0]) : null;
                bool missing = false;
                string current = volume;
                foreach (string part in path.Substring(volume.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    if (missing)
                    {
                        physicalPath = Path.Combine(physicalPath, part);
                        continue;
                    }
                    if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                    try
                    {
                        var handle = OpenChecked(current, ListDirectory, DirectoryShare, OpenExisting, directory: true);
                        pins.Handles.Add(handle);
                        if (inspect) physicalPath = VolumePath(handle);
                    }
                    catch (FileNotFoundException) when (inspect)
                    {
                        physicalPath = Path.Combine(physicalPath, part);
                        missing = true;
                    }
                }
                return pins;
            }
            catch
            {
                pins.Dispose();
                throw;
            }
        }

        private static string VolumePath(SafeFileHandle handle)
        {
            var name = new StringBuilder(512);
            uint count = GetFinalPath(handle, name, (uint)name.Capacity, NormalizedVolumeGuid);
            if (count >= name.Capacity)
            {
                name.EnsureCapacity(checked((int)count + 1));
                count = GetFinalPath(handle, name, (uint)name.Capacity, NormalizedVolumeGuid);
            }
            if (count == 0 || count >= name.Capacity || !name.ToString().StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
                throw Failure("Resolve the physical encrypted Bundle storage volume");
            return name.ToString();
        }

        internal static FileStream OpenRead(string path)
        {
            path = LocalAbsolute(path);
            using var pins = PinDirectory(Path.GetDirectoryName(path), create: false);
            return ReadPinned(path);
        }

        internal static FileStream ReadPinned(string path) => AsStream(OpenChecked(path, ReadAccess, ReadShare, OpenExisting), FileAccess.Read);
        internal static SafeFileHandle LockPinned(string path) => OpenChecked(path, ReadAccess | WriteAccess, 0, OpenAlways);
        internal static FileStream CreateTemporaryPinned(string path) => AsStream(OpenChecked(path, ReadAccess | WriteAccess | DeleteAccess, 0, CreateNew), FileAccess.ReadWrite);

        internal static SafeFileHandle CreatePublicationDirectory(string path)
        {
            if (!CreateDirectory(path, IntPtr.Zero)) throw Failure("Create fresh encrypted delivery directory");
            return OpenChecked(path, ListDirectory | DeleteAccess, DirectoryShare, OpenExisting, directory: true);
        }

        internal static void Publish(FileStream temporary, string destination) => Publish(temporary.SafeFileHandle, destination);

        internal static void Publish(SafeFileHandle temporary, string destination)
        {
            // FILE_RENAME_INFO (Win64). A handle-based rename binds publication to the verified file, not a path lookup.
            var name = Encoding.Unicode.GetBytes(destination);
            var record = new byte[24 + name.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(name.Length), 0, record, 16, sizeof(int));
            Buffer.BlockCopy(name, 0, record, 20, name.Length);
            if (!SetFileInformationByHandle(temporary, 3, record, (uint)record.Length))
            {
                throw Failure("Atomic encrypted Bundle commit");
            }
        }

        internal static void DeleteOpen(FileStream file)
        {
            if (!SetFileInformationByHandle(file.SafeFileHandle, 4, new byte[] { 1 }, 1))
            {
                throw Failure("Encrypted Bundle cache cleanup");
            }
        }

        internal static FileStream OpenRemovalPinned(string path) => AsStream(OpenChecked(path, ReadAccess | DeleteAccess, 0, OpenExisting), FileAccess.Read);

        private static FileStream AsStream(SafeFileHandle handle, FileAccess access)
        {
            try { return new OwnedFileStream(handle, access); }
            catch { handle.Dispose(); throw; }
        }

        private sealed class OwnedFileStream : FileStream
        {
            private readonly SafeFileHandle _owned;

            internal OwnedFileStream(SafeFileHandle handle, FileAccess access) : base(handle, access, 4096, false) => _owned = handle;

            protected override void Dispose(bool disposing)
            {
                try { base.Dispose(disposing); }
                finally { if (disposing) _owned?.Dispose(); }
            }
        }

        private static SafeFileHandle OpenChecked(string path, uint access, uint share, uint disposition, bool directory = false)
        {
            var handle = Open(path, access, share, IntPtr.Zero, disposition, OpenLinkItself | (directory ? BackupSemantics : 0), IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (code == 2 || code == 3) throw new FileNotFoundException("Encrypted Bundle file or directory not found.");
                throw new IOException("Encrypted Bundle storage open failed for " + Path.GetFileName(path) + " (Win32 " + code + ").");
            }
            try
            {
                if (!GetFileInformationByHandle(handle, out var info)) throw Failure("Encrypted Bundle storage identity");
                if ((info.Attributes & ReparsePoint) != 0 || ((info.Attributes & DirectoryBit) != 0) != directory || (!directory && info.Links != 1))
                {
                    throw new EncryptedBundleIntegrityException("Encrypted Bundle storage refused a directory mismatch or link.");
                }
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private static IOException Failure(string operation)
        {
            int code = Marshal.GetLastWin32Error();
            return new IOException(operation + " failed (Win32 " + code + ").", new Win32Exception(code));
        }

        private sealed class Pins : IDisposable
        {
            internal readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();
            public void Dispose()
            {
                for (int index = Handles.Count - 1; index >= 0; index--) Handles[index].Dispose();
                Handles.Clear();
            }
        }
    }
}
