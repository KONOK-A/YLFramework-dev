using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace YLFramework.Resource
{
    /// <summary>在主包的已签名事务中验证并装配可选的内容 Provider。</summary>
    public interface IResourceContentExtension
    {
        /// <summary>校验所选清单的描述、目录和必需内容，直到验证结束都不能提供业务资源。</summary>
        Task<IResourceContentExtensionSession> VerifyStartupAsync(ResourceContentManifest manifest,
            ResourceContentFiles files, IResourceLocator locator, CancellationToken cancellation);
    }

    /// <summary>一次通过预检的扩展会话，接入主包的唯一 Catalog 与路由。</summary>
    public interface IResourceContentExtensionSession : IDisposable
    {
        /// <summary>返回扩展节点对应的已签密文传输路由。普通节点返回 null。</summary>
        string GetCiphertextRoute(IResourceLocation location);
        /// <summary>在官方初始化完成后核对 Provider 并开放扩展。启动失败时主包会释放该会话。</summary>
        void Attach(IResourceLocator officialLocator, Action checkUnchanged);
        /// <summary>只转换当前扩展拥有的 Catalog 节点。</summary>
        bool TryTransform(IResourceLocation location, out string internalId);
    }

    /// <summary>所选版本的只读文件视图。主包持有验证时的文件及目录句柄。</summary>
    public sealed class ResourceContentFiles
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (string Path, FileStream Pin)> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IDisposable> _directories = new(StringComparer.OrdinalIgnoreCase);
        private bool _closed;

        internal ResourceContentFiles(string settingsPath)
        {
            SettingsPath = settingsPath;
        }

        /// <summary>当前安装内已绑定的 Addressables 启动配置。</summary>
        public string SettingsPath
        {
            get;
        }

        /// <summary>取得本次会话已验证且在本地可读的制品路径。</summary>
        public string GetPath(string route)
        {
            if (!TryGetPath(route, out var path))
                throw new ResourceException(ResourceError.SourceUnavailable, "The signed route has no verified local file: " + route);
            return path;
        }

        /// <summary>判断当前会话是否已经准备并锁定这个签名制品。</summary>
        public bool TryGetPath(string route, out string path)
        {
            lock (_gate)
            {
                if (!_closed && _files.TryGetValue(route, out var entry))
                {
                    path = entry.Path;
                    return true;
                }
                path = null;
                return false;
            }
        }

        internal HashSet<string> SnapshotVerifiedPaths()
        {
            lock (_gate)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _files.Values)
                    paths.Add(entry.Path);
                return paths;
            }
        }

        /// <summary>取得独立的只读流，由调用方关闭。</summary>
        public FileStream OpenRead(string route)
        {
            lock (_gate)
                return ResourceContentFileIO.OpenRead(GetPath(route));
        }

        internal void Pin(ResourceContentArtifact artifact, string path, CancellationToken cancellation)
        {
            lock (_gate)
            {
                if (_closed)
                    throw new ObjectDisposedException(nameof(ResourceContentFiles));
                if (_files.ContainsKey(artifact.Route))
                    return;
                string directory = Path.GetDirectoryName(path);
                if (!_directories.ContainsKey(directory))
                    _directories.Add(directory, ResourceContentFileIO.PinDirectory(directory, false));
            }

            var stream = ResourceContentFileIO.OpenRead(path);
            try
            {
                ResourceContentFileIO.RequireIdentity(stream, artifact.Length, artifact.Sha256, cancellation);
                lock (_gate)
                {
                    if (_closed)
                        throw new ObjectDisposedException(nameof(ResourceContentFiles));
                    if (_files.ContainsKey(artifact.Route))
                        return;
                    _files.Add(artifact.Route, (path, stream));
                    stream = null;
                }
            }
            finally
            {
                stream?.Dispose();
            }
        }

        internal void Close()
        {
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                foreach (var file in _files.Values)
                    file.Pin.Dispose();
                _files.Clear();
                foreach (var directory in _directories.Values)
                    directory.Dispose();
                _directories.Clear();
            }
        }
    }

    internal static class ResourceContentFileIO
    {
        private const int BufferBytes = 64 * 1024;
        private const uint GenericRead = 0x80000000;
        private const uint ReadAttributes = 0x80;
        private const uint DeleteAccess = 0x00010000;
        private const uint ShareRead = 1;
        private const uint ShareWrite = 2;
        private const uint OpenExisting = 3;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint BackupSemantics = 0x02000000;
        private const uint DirectoryFlag = 0x10;
        private const uint ReparsePoint = 0x400;
        private const int FileDispositionInfo = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte deleteFile, uint size);

        internal static IDisposable PinDirectory(string path, bool create)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Resource content updates require local Win64 file storage.");
            var full = Path.GetFullPath(path);
            var volume = Path.GetPathRoot(full);
            if (volume.Length != 3 || volume[1] != ':')
                throw new InvalidDataException("Resource content storage must use a local drive.");

            var pins = new DirectoryPins();
            try
            {
                string current = volume;
                pins.Handles.Add(OpenChecked(current, true));
                foreach (string part in full.Substring(volume.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    if (create && !Directory.Exists(current))
                        Directory.CreateDirectory(current);
                    pins.Handles.Add(OpenChecked(current, true));
                }
                return pins;
            }
            catch
            {
                pins.Dispose();
                throw;
            }
        }

        internal static FileStream OpenRead(string path)
        {
            var file = OpenChecked(path, false);
            try
            {
                return new PinnedFile(file);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        internal static bool TryRemoveTemporaryFile(string path, CancellationToken cancellation)
        {
            using var file = CreateFile(path, ReadAttributes | DeleteAccess, 0, IntPtr.Zero, OpenExisting,
                OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (file.IsInvalid)
            {
                return HandleTemporaryRemovalFailure(Marshal.GetLastWin32Error());
            }

            if (!GetFileInformationByHandle(file, out var information))
            {
                return HandleTemporaryRemovalFailure(Marshal.GetLastWin32Error());
            }

            if (!HasSafeIdentity(information, directory: false))
            {
                return false;
            }

            // 通过原句柄删除，路径在辨识身份后无需再次解析。
            cancellation.ThrowIfCancellationRequested();
            byte delete = 1;
            return SetFileInformationByHandle(file, FileDispositionInfo, ref delete, sizeof(byte)) ||
                HandleTemporaryRemovalFailure(Marshal.GetLastWin32Error());
        }

        private static bool HandleTemporaryRemovalFailure(int code)
        {
            if (code == 2 || code == 3 || code == 32 || code == 33)
            {
                return false;
            }

            throw new IOException("Could not remove a temporary resource content record.", new Win32Exception(code));
        }

        private static SafeFileHandle OpenChecked(string path, bool directory)
        {
            var file = CreateFile(path, directory ? ReadAttributes : GenericRead,
                directory ? ShareRead | ShareWrite : ShareRead, IntPtr.Zero, OpenExisting,
                OpenReparsePoint | (directory ? BackupSemantics : 0), IntPtr.Zero);
            if (file.IsInvalid)
            {
                var code = Marshal.GetLastWin32Error();
                file.Dispose();
                if (code == 2 || code == 3)
                    throw new FileNotFoundException("A required content path is missing.", path);
                throw new IOException("Could not lock a resource content path.", new Win32Exception(code));
            }
            if (!GetFileInformationByHandle(file, out var information) || !HasSafeIdentity(information, directory))
            {
                file.Dispose();
                throw new IOException("A content path has an unsafe file identity.");
            }
            return file;
        }

        private static bool HasSafeIdentity(FileInformation information, bool directory) =>
            (information.Attributes & ReparsePoint) == 0 &&
            ((information.Attributes & DirectoryFlag) != 0) == directory && (directory || information.Links == 1);

        internal static void RequireIdentity(FileStream stream, long length, string sha256, CancellationToken cancellation)
        {
            if (stream.Length != length || Hash(stream, cancellation) != sha256)
                throw new InvalidDataException("A content file differs from its signed length or SHA-256.");
            stream.Position = 0;
        }

        internal static string Hash(Stream stream, CancellationToken cancellation = default)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
            try
            {
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, count);
                }
                cancellation.ThrowIfCancellationRequested();
                return Hex(hash.GetHashAndReset());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        private sealed class PinnedFile : FileStream
        {
            private readonly SafeFileHandle _owned;

            internal PinnedFile(SafeFileHandle handle) : base(handle, FileAccess.Read, BufferBytes, false)
            {
                _owned = handle;
            }

            protected override void Dispose(bool disposing)
            {
                try
                {
                    base.Dispose(disposing);
                }
                finally
                {
                    if (disposing)
                        _owned.Dispose();
                }
            }
        }

        private sealed class DirectoryPins : IDisposable
        {
            internal List<SafeFileHandle> Handles { get; } = new();

            public void Dispose()
            {
                for (var index = Handles.Count - 1; index >= 0; index--)
                    Handles[index].Dispose();
                Handles.Clear();
            }
        }
    }
}
