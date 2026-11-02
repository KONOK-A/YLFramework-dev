using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace YLFramework.Resource
{
    /// <summary>只封装系统单段 AEAD ABI，不实现 AES/GHASH，也不提供软件回退。</summary>
    /// <remarks>仅接受受编译条件和运行检查约束的 Windows x64 后端；原生释放失败时保留所有者，不盲目重试。</remarks>
    internal sealed class CngSegmentAead : IDisposable
    {
        /// <summary>IL2CPP 仅接收固定版本候选；候选须完成原生验收后才作为发行支持。</summary>
        internal static bool IsAcceptedPlayerVersion(string version) => version is "2022.3.62f3c1" or "6000.3.27f1";

#if UNITY_EDITOR_WIN || (!UNITY_EDITOR && UNITY_STANDALONE_WIN && (ENABLE_MONO || ENABLE_IL2CPP))
        private readonly object _gate = new();
        private IntPtr _algorithm;
        private IntPtr _key;
        private bool _isDisposed;
        private Exception _releaseFault;
        private long _keyObjectBytes;
        private static long _activeAlgorithms;
        private static long _activeKeys;
        private static long _activeKeyObjectBytes;
        private static long _releaseFailures;
        // 无法确认销毁的系统 owner 必须留存；不重试有歧义的 native 释放，也不关闭其 provider。
        private static readonly List<CngSegmentAead> _retainedOwners = new();

        /// <summary>检查当前编译后端、Windows 与进程位数；IL2CPP Player 另要求代码中固定的 Unity 版本。</summary>
        internal static bool IsSupported => Environment.OSVersion.Platform == PlatformID.Win32NT && IntPtr.Size == 8
#if ENABLE_IL2CPP && !UNITY_EDITOR
            // 该getter由Unity明确标记IsThreadSafe，可在认证worker中核对精确受测版本。
            && IsAcceptedPlayerVersion(UnityEngine.Application.unityVersion)
#endif
            ;
        internal static long ActiveAlgorithms => Interlocked.Read(ref _activeAlgorithms);
        internal static long ActiveKeys => Interlocked.Read(ref _activeKeys);
        internal static long ActiveKeyObjectBytes => Interlocked.Read(ref _activeKeyObjectBytes);
        internal static long ReleaseFailures => Interlocked.Read(ref _releaseFailures);
        internal static int RetainedReleaseOwners
        {
            get
            {
                lock (_retainedOwners)
                {
                    return _retainedOwners.Count;
                }
            }
        }
        internal long KeyObjectBytes => _keyObjectBytes;
        internal Exception ReleaseFault
        {
            get
            {
                lock (_gate)
                {
                    return _releaseFault;
                }
            }
        }

        /// <summary>核对平台、密钥长度和 ABI，建立 AES-GCM 系统所有者；构造失败时结算已取得的句柄。</summary>
        internal CngSegmentAead(byte[] secret)
        {
            if (!IsSupported)
            {
                throw Unsupported();
            }

            ResourceBundleEnvelope.ValidateKey(secret);
            ValidateAbi();
            try
            {
                int status = BCryptOpenAlgorithmProvider(out _algorithm, "AES", "Microsoft Primitive Provider", 0);
                if (_algorithm != IntPtr.Zero)
                {
                    Interlocked.Increment(ref _activeAlgorithms);
                }

                Check(status, "BCryptOpenAlgorithmProvider");
                if (_algorithm == IntPtr.Zero)
                {
                    throw new CryptographicException("CNG returned no algorithm owner.");
                }

                const string Mode = "ChainingModeGCM";
                Check(BCryptSetProperty(_algorithm, "ChainingMode", Mode, (Mode.Length + 1) * 2, 0), "BCryptSetProperty");
                Check(BCryptGetTagLengths(_algorithm, "AuthTagLength", out var tags, 12, out var tagBytes, 0), "BCryptGetProperty(AuthTagLength)");
                if (tagBytes != 12 || tags.Minimum < 0 || tags.Increment < 0 || tags.Minimum > 16 || tags.Maximum < 16 ||
                    tags.Increment != 0 && (16 - tags.Minimum) % tags.Increment != 0)
                {
                    throw new CryptographicException("CNG provider does not support the required 128-bit tag.");
                }

                Check(BCryptGetProperty(_algorithm, "ObjectLength", out var bytes, sizeof(int), out var propertyBytes, 0), "BCryptGetProperty(ObjectLength)");
                if (propertyBytes != sizeof(int) || bytes <= 0)
                {
                    throw new CryptographicException("Invalid CNG key-object size.");
                }

                GenerateKey(secret, bytes);
            }
            catch (Exception error)
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanup)
                {
                    throw new AggregateException("CNG construction and owner cleanup failed.", error, cleanup);
                }
                throw;
            }
        }

        /// <summary>固定调用方密钥副本供 CNG 生成系统密钥，并登记实际取得的句柄与对象字节诊断。</summary>
        private unsafe void GenerateKey(byte[] secret, int bytes)
        {
            fixed (byte* pointer = secret)
            {
                int status = BCryptGenerateSymmetricKey(_algorithm, out _key, IntPtr.Zero, 0, (IntPtr)pointer, secret.Length, 0);
                if (_key != IntPtr.Zero)
                {
                    _keyObjectBytes = bytes;
                    Interlocked.Increment(ref _activeKeys);
                    Interlocked.Add(ref _activeKeyObjectBytes, bytes);
                }
                Check(status, "BCryptGenerateSymmetricKey");
                if (_key == IntPtr.Zero)
                {
                    throw new CryptographicException("CNG returned no key owner.");
                }
            }
        }

        /// <summary>加密一个明文段，将 16 字节认证标签附在密文之后。</summary>
        internal int Encrypt(byte[] input, int count, byte[] nonce, byte[] output) => Transform(true, input, count, nonce, output);
        /// <summary>解密并认证包含末尾标签的密文段；原生状态或输出长度异常时清除输出。</summary>
        internal int Decrypt(byte[] input, int count, byte[] nonce, byte[] output) => Transform(false, input, count, nonce, output);

        /// <summary>在互斥保护下校验缓冲与 nonce 并调用系统单段 AEAD，只有状态和输出长度均正确才返回。</summary>
        private unsafe int Transform(bool encrypt, byte[] input, int count, byte[] nonce, byte[] output)
        {
            lock (_gate)
            {
                if (_isDisposed || _key == IntPtr.Zero || _releaseFault != null)
                {
                    throw new ObjectDisposedException(nameof(CngSegmentAead));
                }

                if (input == null || output == null || nonce == null)
                {
                    throw new ArgumentNullException();
                }

                if (count < (encrypt ? 0 : 16) || count > input.Length || nonce.Length != 12 || ReferenceEquals(input, output))
                {
                    throw new ArgumentException("Invalid single-segment AEAD input.");
                }

                int plainCount = encrypt ? count : count - 16;
                int resultCount = encrypt ? checked(count + 16) : plainCount;
                if (output.Length < resultCount)
                {
                    throw new ArgumentException("Segment output too short.");
                }

                fixed (byte* inputPointer = input, outputPointer = output, noncePointer = nonce)
                {
                    byte empty = 0;
                    var info = new AuthInfo
                    {
                        Size = 88,
                        Version = 1,
                        Nonce = (IntPtr)noncePointer,
                        NonceBytes = 12,
                        Tag = (IntPtr)(encrypt ? outputPointer + plainCount : inputPointer + plainCount),
                        TagBytes = 16
                    };
                    // 空消息仍须认证；NULL output 会变成尺寸查询，不能当作 tag 验证。
                    var source = plainCount == 0 ? (IntPtr)(&empty) : (IntPtr)inputPointer;
                    var destination = plainCount == 0 ? (IntPtr)(&empty) : (IntPtr)outputPointer;
                    int status = encrypt
                        ? BCryptEncrypt(_key, source, plainCount, ref info, IntPtr.Zero, 0, destination, plainCount, out var written, 0)
                        : BCryptDecrypt(_key, source, plainCount, ref info, IntPtr.Zero, 0, destination, plainCount, out written, 0);
                    GC.KeepAlive(this);
                    if (status != 0 || written != plainCount)
                    {
                        Array.Clear(output, 0, output.Length);
                        Check(status, encrypt ? "BCryptEncrypt" : "BCryptDecrypt");
                        throw new CryptographicException("Unexpected authenticated segment size.");
                    }
                    return resultCount;
                }
            }
        }

        /// <summary>先销毁密钥再关闭算法所有者；失败留存对象与诊断，后续调用报告原失败而不重试原生释放。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_releaseFault != null)
                {
                    throw new ResourceContentCleanupException(this, _releaseFault);
                }

                if (_isDisposed)
                {
                    return;
                }

                if (_key != IntPtr.Zero)
                {
                    int status = BCryptDestroyKey(_key);
                    if (status != 0)
                    {
                        throw RetainFailure(status, "BCryptDestroyKey");
                    }

                    _key = IntPtr.Zero;
                    Interlocked.Decrement(ref _activeKeys);
                    Interlocked.Add(ref _activeKeyObjectBytes, -_keyObjectBytes);
                }
                if (_algorithm != IntPtr.Zero)
                {
                    int status = BCryptCloseAlgorithmProvider(_algorithm, 0);
                    if (status != 0)
                    {
                        throw RetainFailure(status, "BCryptCloseAlgorithmProvider");
                    }

                    _algorithm = IntPtr.Zero;
                    Interlocked.Decrement(ref _activeAlgorithms);
                }
                _isDisposed = true;
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>记录一次原生释放失败并静态留存当前所有者，避免在句柄状态有歧义时继续关闭或终结。</summary>
        private ResourceContentCleanupException RetainFailure(int status, string operation)
        {
            _releaseFault = NativeFailure(status, operation);
            Interlocked.Increment(ref _releaseFailures);
            lock (_retainedOwners)
            {
                _retainedOwners.Add(this);
            }

            GC.SuppressFinalize(this);
            return new ResourceContentCleanupException(this, _releaseFault);
        }

        /// <summary>为遗漏显式释放的对象尝试收尾；原生释放失败已由 Dispose 留存，不在终结器中重试。</summary>
        ~CngSegmentAead()
        {
            try
            {
                Dispose();
            }
            catch (ResourceContentCleanupException) { /* owner 已留存并记入诊断，不在 finalizer 重试。 */ }
        }

        /// <summary>核对当前进程中认证结构与标签范围结构的大小及成员偏移，不匹配时拒绝原生调用。</summary>
        private static void ValidateAbi()
        {
            var names = new[] { nameof(AuthInfo.Size), nameof(AuthInfo.Version), nameof(AuthInfo.Nonce), nameof(AuthInfo.NonceBytes),
                nameof(AuthInfo.AuthData), nameof(AuthInfo.AuthDataBytes), nameof(AuthInfo.Tag), nameof(AuthInfo.TagBytes),
                nameof(AuthInfo.MacContext), nameof(AuthInfo.MacContextBytes), nameof(AuthInfo.AadBytes), nameof(AuthInfo.DataBytes), nameof(AuthInfo.Flags) };
            var offsets = new[] { 0, 4, 8, 16, 24, 32, 40, 48, 56, 64, 68, 72, 80 };
            if (Marshal.SizeOf<AuthInfo>() != 88 || Marshal.SizeOf<TagLengths>() != 12)
            {
                throw new PlatformNotSupportedException("Unexpected CNG AEAD ABI size.");
            }

            for (int i = 0; i < names.Length; ++i)
            {
                if (Marshal.OffsetOf<AuthInfo>(names[i]).ToInt32() != offsets[i])
                {
                    throw new PlatformNotSupportedException("Unexpected CNG AEAD ABI member offset.");
                }
            }
        }

        /// <summary>将非成功 NTSTATUS 转为包含操作名与状态码的密码异常。</summary>
        private static void Check(int status, string operation)
        {
            if (status != 0)
            {
                throw NativeFailure(status, operation);
            }
        }

        /// <summary>格式化系统操作失败，不把输入、密钥或明文放入异常文本。</summary>
        private static CryptographicException NativeFailure(int status, string operation) =>
            new(operation + " NTSTATUS=0x" + unchecked((uint)status).ToString("X8"));

        /// <summary>CNG 返回的认证标签长度范围 ABI，用于确认后端支持 16 字节标签。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct TagLengths
        {
            internal int Minimum;
            internal int Maximum;
            internal int Increment;
        }

        /// <summary>与 Windows x64 认证加密参数对应的布局；成员顺序与偏移由 ValidateAbi 明确核对。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct AuthInfo
        {
            internal int Size;
            internal int Version;
            internal IntPtr Nonce;
            internal int NonceBytes;
            internal IntPtr AuthData;
            internal int AuthDataBytes;
            internal IntPtr Tag;
            internal int TagBytes;
            internal IntPtr MacContext;
            internal int MacContextBytes;
            internal int AadBytes;
            internal ulong DataBytes;
            internal int Flags;
        }

#if ENABLE_IL2CPP && !UNITY_EDITOR
        // IL2CPP不传递DefaultDllImportSearchPaths，按确切System32模块句柄绑定，不走插件目录搜索。
        // 与P/Invoke运行时缓存相同，此模块是进程期依赖；provider/key仍逐owner确定释放。
        private static readonly IntPtr SystemModule = LoadSystemModule();
        private static readonly OpenAlgorithm BCryptOpenAlgorithmProvider = Bind<OpenAlgorithm>("BCryptOpenAlgorithmProvider");
        private static readonly SetProperty BCryptSetProperty = Bind<SetProperty>("BCryptSetProperty");
        private static readonly GetProperty BCryptGetProperty = Bind<GetProperty>("BCryptGetProperty");
        private static readonly GetTagLengths BCryptGetTagLengths = Bind<GetTagLengths>("BCryptGetProperty");
        private static readonly GenerateSymmetricKey BCryptGenerateSymmetricKey = Bind<GenerateSymmetricKey>("BCryptGenerateSymmetricKey");
        private static readonly TransformSegment BCryptEncrypt = Bind<TransformSegment>("BCryptEncrypt");
        private static readonly TransformSegment BCryptDecrypt = Bind<TransformSegment>("BCryptDecrypt");
        private static readonly DestroyKey BCryptDestroyKey = Bind<DestroyKey>("BCryptDestroyKey");
        private static readonly CloseAlgorithm BCryptCloseAlgorithmProvider = Bind<CloseAlgorithm>("BCryptCloseAlgorithmProvider");

        /// <summary>从系统目录装载并核对确切的 bcrypt 模块；失败时只释放本次取得的模块引用。</summary>
        private static IntPtr LoadSystemModule()
        {
            var directory = new System.Text.StringBuilder(32768);
            uint length = GetSystemDirectoryW(directory, (uint)directory.Capacity);
            if (length == 0 || length >= directory.Capacity) throw new PlatformNotSupportedException("Cannot resolve the Windows system directory.");
            string path = Path.GetFullPath(Path.Combine(directory.ToString(), "bcrypt.dll"));
            var module = LoadLibraryExW(path, IntPtr.Zero, 0x00000800); // LOAD_LIBRARY_SEARCH_SYSTEM32
            if (module == IntPtr.Zero) throw new PlatformNotSupportedException("Cannot load the System32 CNG dependency: " + Marshal.GetLastWin32Error());
            try
            {
                var actual = new System.Text.StringBuilder(32768);
                uint written = GetModuleFileNameW(module, actual, (uint)actual.Capacity);
                if (written == 0 || written >= actual.Capacity || !Path.GetFullPath(actual.ToString()).Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new PlatformNotSupportedException("The CNG dependency is not the exact System32 module.");
                return module;
            }
            catch (Exception error)
            {
                if (!FreeLibrary(module)) throw new AggregateException("CNG dependency validation and module cleanup failed.", error,
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                throw;
            }
        }

        /// <summary>将已核对系统模块中的导出地址绑定为固定 ABI 委托，不从任意插件目录解析。</summary>
        private static T Bind<T>(string entry) where T : Delegate
        {
            var address = GetProcAddress(SystemModule, entry);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException("System32 bcrypt.dll: " + entry);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        /// <summary>打开 CNG 算法所有者的原生 ABI。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int OpenAlgorithm(out IntPtr algorithm, [MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string implementation, int flags);
        /// <summary>设置 CNG 算法字符串属性的原生 ABI。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int SetProperty(IntPtr algorithm, [MarshalAs(UnmanagedType.LPWStr)] string property, [MarshalAs(UnmanagedType.LPWStr)] string input, int inputBytes, int flags);
        /// <summary>读取 CNG 整数属性的原生 ABI。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int GetProperty(IntPtr algorithm, [MarshalAs(UnmanagedType.LPWStr)] string property, out int value, int valueBytes, out int written, int flags);
        /// <summary>读取 CNG 标签长度范围结构的原生 ABI。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int GetTagLengths(IntPtr algorithm, [MarshalAs(UnmanagedType.LPWStr)] string property, out TagLengths value, int valueBytes, out int written, int flags);
        /// <summary>从显式密钥字节生成系统对称密钥所有者的原生 ABI。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GenerateSymmetricKey(IntPtr algorithm, out IntPtr key, IntPtr keyObject, int keyObjectBytes, IntPtr secret, int secretBytes, int flags);
        /// <summary>单段加解密共用的 CNG 参数 ABI，认证标签经 AuthInfo 单独传递。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int TransformSegment(IntPtr key, IntPtr input, int inputBytes, ref AuthInfo auth, IntPtr iv, int ivBytes, IntPtr output, int outputBytes, out int written, int flags);
        /// <summary>销毁系统密钥所有者的原生 ABI。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int DestroyKey(IntPtr key);
        /// <summary>关闭系统算法所有者的原生 ABI。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int CloseAlgorithm(IntPtr algorithm, int flags);
        /// <summary>查询系统目录，为确切模块路径提供来源。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetSystemDirectoryW(System.Text.StringBuilder buffer, uint size);
        /// <summary>按明确路径及搜索标志取得系统模块引用。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        /// <summary>读取已装载模块的实际路径，供装载后身份核对。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder path, uint size);
        /// <summary>只在给定模块句柄中查找指定导出地址。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string entry);
        /// <summary>释放模块身份核对失败时本次持有的模块引用。</summary>
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
#else
        /// <summary>通过限定 System32 的 P/Invoke 打开 CNG 算法所有者。</summary>
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, int flags);
        /// <summary>设置算法属性，本封装用于选择 GCM 链接模式。</summary>
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptSetProperty(IntPtr algorithm, string property, string input, int inputBytes, int flags);
        /// <summary>读取整数属性，本封装用于取得系统密钥对象大小。</summary>
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptGetProperty(IntPtr algorithm, string property, out int value, int valueBytes, out int written, int flags);
        /// <summary>按标签范围结构读取属性，确认系统提供所需认证标签长度。</summary>
        [DllImport("bcrypt.dll", EntryPoint = "BCryptGetProperty", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptGetTagLengths(IntPtr algorithm, string property, out TagLengths value, int valueBytes, out int written, int flags);
        /// <summary>让系统从调用期间固定的密钥副本建立对称密钥所有者。</summary>
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptGenerateSymmetricKey(IntPtr algorithm, out IntPtr key, IntPtr keyObject, int keyObjectBytes, IntPtr secret, int secretBytes, int flags);
        /// <summary>调用 CNG 加密单段明文，认证标签写入认证参数指定的缓冲。</summary>
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptEncrypt(IntPtr key, IntPtr input, int inputBytes, ref AuthInfo auth, IntPtr iv, int ivBytes, IntPtr output, int outputBytes, out int written, int flags);
        /// <summary>调用 CNG 解密单段密文，并通过认证参数核对标签。</summary>
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptDecrypt(IntPtr key, IntPtr input, int inputBytes, ref AuthInfo auth, IntPtr iv, int ivBytes, IntPtr output, int outputBytes, out int written, int flags);
        /// <summary>销毁系统密钥句柄，只有成功返回后上层才结算诊断计数。</summary>
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptDestroyKey(IntPtr key);
        /// <summary>关闭系统算法句柄，失败时由上层保留所有者。</summary>
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
#endif
#else
        internal static bool IsSupported => false;
        internal static long ActiveAlgorithms => 0;
        internal static long ActiveKeys => 0;
        internal static long ActiveKeyObjectBytes => 0;
        internal static long ReleaseFailures => 0;
        internal static int RetainedReleaseOwners => 0;
        internal long KeyObjectBytes => 0;
        internal Exception ReleaseFault => null;
        /// <summary>不受支持的编译后端明确拒绝构造，不加载替代密码实现。</summary>
        internal CngSegmentAead(byte[] secret)
        {
            throw Unsupported();
        }
        /// <summary>在不受支持后端拒绝加密，不产生输出。</summary>
        internal int Encrypt(byte[] input, int count, byte[] nonce, byte[] output) => throw Unsupported();
        /// <summary>在不受支持后端拒绝解密，不产生输出。</summary>
        internal int Decrypt(byte[] input, int count, byte[] nonce, byte[] output) => throw Unsupported();
        /// <summary>不受支持分支没有可归还的原生所有者。</summary>
        public void Dispose()
        {
        }
#endif
        /// <summary>生成统一的平台不支持错误，不把平台限制转换为静默回退。</summary>
        private static PlatformNotSupportedException Unsupported() =>
            new("Encrypted Resource content requires Windows x64 Editor or a validated Windows64 Mono/IL2CPP Player backend.");
    }

    /// <summary>携带无法确认释放完成的内容所有者，供上层保留责任而非遗失句柄后继续运行。</summary>
    internal sealed class ResourceContentCleanupException : IOException
    {
        internal IDisposable Owner
        {
            get;
        }
        /// <summary>保留未完成释放的所有者与底层错误，不在异常构造中再次尝试释放。</summary>
        internal ResourceContentCleanupException(IDisposable owner, Exception cause)
            : base("Resource native content owner release failed; owner retained without automatic retry.", cause) => Owner = owner;
    }
}
