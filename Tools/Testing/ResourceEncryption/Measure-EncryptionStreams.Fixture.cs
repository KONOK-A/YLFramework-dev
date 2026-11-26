using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// Repository tooling only. The only shared contract here is the five native exports.
public static partial class EncryptionStreamMicrobenchmark
{
    private const int Segment = 1 << 20;
    private const int FirstPlain = 1_048_520;
    private const int NextPlain = 1_048_560;
    private const int Header = 40;
    private const int ReadLimit = 128 * 1024;
    private const int FileBuffer = 4096;
    private const int Block = 32 * 1024;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SealCall(SafeFileHandle plain, SafeFileHandle cipher, byte[] key, byte[] aad, int aadSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenCall(SafeFileHandle cipher, byte[] key, byte[] aad, int aadSize, long expectedLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadCall(IntPtr reader, long offset, IntPtr output, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseCall(IntPtr reader);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StatsCall(IntPtr reader, out long served, out long source, out long pages, out long bound);

    private sealed class Native : IDisposable
    {
        private readonly IntPtr _module;
        public readonly string Path;
        public readonly string Sha256;
        public readonly SealCall Seal;
        public readonly OpenCall Open;
        public readonly ReadCall Read;
        public readonly CloseCall Close;
        public readonly StatsCall Stats;

        public Native(string path, string expectedSha = null, string identity = null)
        {
            Path = path;
            // Keep the DLL immutable across SHA, load and export resolution.
            using var pinned = OpenFile(path);
            Sha256 = Hex(SHA256.HashData(pinned));
            if (!string.IsNullOrEmpty(expectedSha))
                Require(Sha256 == expectedSha, $"The {identity} DLL differs from the pinned SHA-256: {Sha256} (expected {expectedSha}).");
            _module = NativeLibrary.Load(path);
            try
            {
                Seal = Export<SealCall>("yl_seal");
                Open = Export<OpenCall>("yl_open");
                Read = Export<ReadCall>("yl_read");
                Close = Export<CloseCall>("yl_close");
                Stats = Export<StatsCall>("yl_read_statistics");
            }
            catch { Dispose(); throw; }
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));

        public void Dispose() => NativeLibrary.Free(_module);
    }

    private sealed record Counters(long Served, long Source, long Pages, long NativeSourceBound);

    // Match the managed reader's request widths, EOF clipping and per-call pin. The wrapper owns neither the input FileStream nor the key.
    private sealed class Reader : IDisposable
    {
        private readonly Native _native;
        private IntPtr _handle;
        public long Length { get; }

        public Reader(Native native, FileStream cipher, byte[] key, byte[] aad, long length)
        {
            _native = native;
            Length = length;
            _handle = native.Open(cipher.SafeFileHandle, key, aad, aad.Length, length);
            if (_handle == IntPtr.Zero) throw new InvalidDataException("Native open rejected fixture.");
        }

        public int Read(long offset, byte[] output, int count)
        {
            int requested = (int)Math.Min(count, Math.Max(0, Length - offset));
            if (requested == 0) return 0;
            int result = ReadRaw(offset, output, requested);
            if (result != requested) throw new InvalidDataException("Native read rejected fixture: " + result);
            return result;
        }

        public int ReadRaw(long offset, byte[] output, int count)
        {
            var pin = GCHandle.Alloc(output, GCHandleType.Pinned);
            try { return _native.Read(_handle, offset, pin.AddrOfPinnedObject(), count); }
            finally { pin.Free(); }
        }

        public Counters Snapshot()
        {
            _native.Stats(_handle, out long served, out long source, out long pages, out long bound);
            return new Counters(served, source, pages, bound);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            _native.Close(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    private static long ProcessReadBytes() => GetProcessIoCounters(GetCurrentProcess(), out var counters)
        ? checked((long)counters.ReadBytes) : throw new IOException("Cannot obtain independent Windows process I/O counters.");

    private static FileStream OpenFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBuffer);
    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);
    private static string Sha(string path) { using var input = OpenFile(path); return Hex(SHA256.HashData(input)); }
    private static byte[] Aad(long length) => Encoding.UTF8.GetBytes("YL native stream microbenchmark only: " + length);

    private static long CipherLength(long plain) => plain + Header + 16 * (1 + (plain + Header - 1) / NextPlain);

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }

    private static void Save(string path, object evidence)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, evidence, Pretty);
    }

    private static void Seal(Native native, string plain, string cipher, byte[] key, byte[] aad)
    {
        using var input = OpenFile(plain);
        using var output = new FileStream(cipher, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Require(native.Seal(input.SafeFileHandle, output.SafeFileHandle, key, aad, aad.Length) == 0, "yl_seal failed.");
        Require(output.Length == CipherLength(input.Length), "Ciphertext length differs from the format.");
    }

    private static (long Bytes, string Sha256) Hash(long length, Func<long, byte[], int, int> read, bool secret,
        CancellationToken token = default)
    {
        var buffer = new byte[ReadLimit];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long position = 0;
        try
        {
            int n;
            do
            {
                token.ThrowIfCancellationRequested();
                n = read(position, buffer, buffer.Length);
                hash.AppendData(buffer, 0, n);
                position += n;
            } while (n != 0);
            Require(position == length, "Hash byte count differs from expected length.");
            return (position, Hex(hash.GetHashAndReset()));
        }
        finally { if (secret) CryptographicOperations.ZeroMemory(buffer); }
    }

    public static int Contract(string root, string library, string oracleJar, string oracleSource)
    {
        using var native = new Native(library);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var cases = new List<object>();
        var errors = new List<string>();
        void Run(string name, Func<object> test)
        {
            try { cases.Add(new { Name = name, Passed = true, Evidence = test() }); }
            catch (Exception error)
            {
                cases.Add(new { Name = name, Passed = false, Error = error.ToString() });
                errors.Add(name + ": " + error.Message);
            }
        }

        try
        {
            foreach (int length in new[] { 19, FirstPlain, FirstPlain + 1, FirstPlain + NextPlain,
                FirstPlain + NextPlain + 1, 8 * Segment + 731 })
            {
                string plain = Path.Combine(root, $"plain-{length}");
                string cipher = Path.Combine(root, $"native-{length}.bin");
                var expected = new byte[length];
                new Random(length).NextBytes(expected);
                File.WriteAllBytes(plain, expected);
                byte[] aad = Aad(length);
                Seal(native, plain, cipher, key, aad);
                long cipherLength = new FileInfo(cipher).Length;
                string plainSha = Sha(plain);
                string cipherSha = Sha(cipher);
                Counters before = null, after = null;
                long independentAuth = -1, independentRead = -1;

                Run($"bytes-and-counters-{length}", () =>
                {
                    // Warm up the I/O counter P/Invoke before tracing the encrypted file.
                    _ = ProcessReadBytes();
                    using var file = OpenFile(cipher);
                    Require(Hash(length: cipherLength, (_, b, n) => file.Read(b, 0, n), false).Sha256 == cipherSha,
                        "Cipher SHA verification failed.");
                    long startIo = ProcessReadBytes();
                    using var reader = new Reader(native, file, key, aad, length);
                    Require(Hash(length, reader.Read, true).Sha256 == plainSha, "Authenticated clear SHA differs.");
                    independentAuth = ProcessReadBytes() - startIo;
                    before = reader.Snapshot();
                    long readIo = ProcessReadBytes();
                    var bucket = new byte[Block];
                    for (int p = 0, got; (got = reader.Read(p, bucket, bucket.Length)) != 0; p += got)
                        Require(bucket.AsSpan(0, got).SequenceEqual(expected.AsSpan(p, got)), "Wrong 32 KiB sequential plaintext.");
                    independentRead = ProcessReadBytes() - readIo;
                    after = reader.Snapshot();
                    Require(before.Served == length && after.Served - before.Served == length && after.Pages >= before.Pages,
                        "Logical counters do not conserve successful plaintext bytes.");
                    Require(before.Source == independentAuth && after.Source - before.Source == independentRead,
                        $"Cipher bytes differ from independent process read transfer: native {before.Source}/{after.Source - before.Source}, Windows {independentAuth}/{independentRead}.");
                    return new { Length = length, CipherLength = cipherLength, PlainSha256 = plainSha, CipherSha256 = cipherSha,
                        Before = before, After = after, IndependentAuth = independentAuth, IndependentRead = independentRead };
                });

                Run($"auth-read-budget-{length}", () =>
                {
                    Require(before != null && after != null, "Missing sequential observations.");
                    // For one segment the prior authenticated page can survive the rewind.
                    long authMax = length < 8 * Segment ? cipherLength + Header : cipherLength * 102 / 100;
                    long readMax = length < 8 * Segment ? cipherLength : cipherLength * 102 / 100;
                    Require(before.Source <= authMax && after.Source - before.Source <= readMax,
                        $"Repeated cipher reads: auth {before.Source}/{authMax}; sequential {after.Source - before.Source}/{readMax}.");
                    Require(before.Source >= cipherLength && before.NativeSourceBound > 0 &&
                        before.NativeSourceBound + ReadLimit + FileBuffer <= 4L * Segment,
                        "Missing authenticated source bytes or managed + native payload upper bound exceeds 4 MiB.");
                    return new { AuthMax = authMax, ReadMax = readMax, HeaderAllowance = Header,
                        SourceDerivedPayloadBound = before.NativeSourceBound + ReadLimit + FileBuffer };
                });

                Run($"seek-eof-handle-{length}", () =>
                {
                    Counters last;
                    using (var file = OpenFile(cipher))
                    using (var reader = new Reader(native, file, key, aad, length))
                    {
                        file.Dispose(); // The native duplicate must keep the exact file open.
                        bool denied = false;
                        try { using var write = File.Open(cipher, FileMode.Open, FileAccess.Write, FileShare.None); }
                        catch (IOException) { denied = true; }
                        Require(denied, "Native reader failed to retain the stable handle.");
                        var output = new byte[128];
                        foreach (long offset in new long[] { length - 3, Math.Min(length - 1, FirstPlain - 3), 0, length - 3 })
                        {
                            int got = reader.Read(offset, output, output.Length);
                            Require(got == Math.Min(output.Length, length - offset) &&
                                output.AsSpan(0, got).SequenceEqual(expected.AsSpan((int)offset, got)), "Wrong seek/short read.");
                        }
                        last = reader.Snapshot();
                        Require(reader.Read(length, output, output.Length) == 0 && reader.ReadRaw(length, output, 1) < 0 &&
                            reader.Snapshot() == last, "EOF unexpectedly performed a native data read.");
                        // The final byte is in the current page even when the last three bytes crossed a boundary.
                        Require(reader.Read(length - 1, output, output.Length) == 1 &&
                            output[0] == expected[length - 1], "A repeated read returned different plaintext.");
                        var hit = reader.Snapshot();
                        Require(hit.Source == last.Source && hit.Served - last.Served == 1 && hit.Pages == last.Pages,
                            "A repeated read of the same authenticated bytes did not hit the cache.");
                    }
                    using (File.Open(cipher, FileMode.Open, FileAccess.Write, FileShare.None)) { }
                    return new { Length = length, BeforeEof = last, RepeatedTailSourceBytes = 0 };
                });

                if (length < 8 * Segment) continue;
                Run("wrong-binding-or-key", () =>
                {
                    using var file = OpenFile(cipher);
                    byte[] wrong = (byte[])key.Clone();
                    try
                    {
                        wrong[0] ^= 1;
                        var rejections = new List<object>();
                        foreach (bool wrongKey in new[] { false, true })
                        {
                            using var reader = new Reader(native, file, wrongKey ? wrong : key, wrongKey ? aad : Aad(length + 1), length);
                            rejections.Add(RequireRejectedRead(reader, 0, Block, Segment - Header));
                        }
                        return rejections;
                    }
                    finally { CryptographicOperations.ZeroMemory(wrong); }
                });

                foreach (long position in new[] { 2L * Segment - 1, cipherLength - 1 })
                {
                    Run($"bad-tag-{position}", () =>
                    {
                        string bad = Path.Combine(root, $"tampered-{position}.bin");
                        File.Copy(cipher, bad);
                        using (var edit = File.Open(bad, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            edit.Position = position;
                            int original = edit.ReadByte();
                            edit.Position = position;
                            edit.WriteByte((byte)(original ^ 1));
                        }
                        using var file = OpenFile(bad);
                        using var reader = new Reader(native, file, key, aad, length);
                        long start = position == cipherLength - 1 ? FirstPlain + 7L * NextPlain - 3 : FirstPlain - 3;
                        return RequireRejectedRead(reader, start, 128, position == cipherLength - 1 ? cipherLength % Segment : Segment);
                    });
                }

                Run("short-source-read-fails-closed", () =>
                {
                    // Exercise a short ReadFile outside the product's deny-write invariant. This fixture
                    // controls a private copy and modifies it only after native header/length initialization.
                    string truncated = Path.Combine(root, "short-after-open.bin");
                    File.Copy(cipher, truncated);
                    using var file = new FileStream(truncated, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    using var reader = new Reader(native, file, key, aad, length);
                    file.SetLength(Segment + 17);
                    var failedRead = RequireRejectedRead(reader, FirstPlain - 3, 128, Segment + 17 - Header);
                    var before = reader.Snapshot();
                    // There is still a complete, authentic first segment. It must not revive a failed reader.
                    var output = Enumerable.Repeat((byte)0x55, 64).ToArray();
                    Require(reader.ReadRaw(0, output, output.Length) < 0 && output.All(b => b == 0) && reader.Snapshot() == before,
                        "An authentication failure allowed a later read or re-read the cipher source.");
                    return failedRead;
                });

                Run("wrong-expected-plaintext-length", () =>
                {
                    using var file = OpenFile(cipher);
                    var invalid = native.Open(file.SafeFileHandle, key, aad, aad.Length, length - 1);
                    if (invalid != IntPtr.Zero) native.Close(invalid);
                    Require(invalid == IntPtr.Zero, "yl_open accepted a different plaintext length.");
                    return new { Actual = length, Requested = length - 1 };
                });

                Run("cancellation-releases-duplicated-handle", () =>
                {
                    using var stop = new CancellationTokenSource();
                    bool cancelled = false;
                    try
                    {
                        using var file = OpenFile(cipher);
                        using var reader = new Reader(native, file, key, aad, length);
                        Hash(length, (offset, b, count) => { int n = reader.Read(offset, b, count); stop.Cancel(); return n; }, true, stop.Token);
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    Require(cancelled, "Authentication did not honor cancellation between calls.");
                    using (File.Open(cipher, FileMode.Open, FileAccess.Write, FileShare.None)) { }
                    return "native duplicate and owning input are closed";
                });

                if (oracleJar != null)
                {
                    Run("fixed-java-oracle-both-directions", () => Oracle(root, native, plain, cipher, plainSha, key, aad, oracleJar, oracleSource));
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Save(Path.Combine(root, "contract.json"), new { NativeDll = library, native.Sha256, Runtime = RuntimeInformation.FrameworkDescription,
                OracleJarSha256 = oracleJar == null ? null : Sha(oracleJar), Cases = cases, Errors = errors });
        }
        foreach (string error in errors) Console.Error.WriteLine(error);
        return errors.Count;
    }

    private static object RequireRejectedRead(Reader reader, long start, int count, long minCipher)
    {
        var bytes = Enumerable.Repeat((byte)0x55, count + 1).ToArray();
        var before = reader.Snapshot();
        long io = ProcessReadBytes();
        int status = reader.ReadRaw(start, bytes, count);
        long independent = ProcessReadBytes() - io;
        var after = reader.Snapshot();
        Require(status < 0 && bytes.Take(count).All(b => b == 0) && bytes[count] == 0x55,
            "On authentication failure native output must be zero with no write past the request.");
        Require(after.Served == before.Served && after.Source - before.Source >= minCipher && after.Source - before.Source == independent,
            "Failed reads did not conserve/count source vs served bytes.");
        return new { Before = before, After = after, IndependentSourceBytes = independent, Status = status };
    }

    private static object Oracle(string root, Native native, string plain, string cipher, string plainSha,
        byte[] key, byte[] aad, string jar, string source)
    {
        string keyPath = Path.Combine(root, "ephemeral-oracle-key");
        string aadPath = Path.Combine(root, "oracle-aad");
        string fromNative = Path.Combine(root, "oracle-from-native");
        string fromJava = Path.Combine(root, "oracle-from-java.bin");
        void Call(string op, string input, string output)
        {
            var command = new ProcessStartInfo("java.exe") { UseShellExecute = false, WorkingDirectory = root };
            foreach (var arg in new[] { "-cp", jar, source, op, keyPath, aadPath, input, output }) command.ArgumentList.Add(arg);
            using var process = Process.Start(command);
            if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("Java oracle timed out: " + op); }
            Require(process.ExitCode == 0, "The fixed Java oracle failed: " + op);
        }
        try
        {
            File.WriteAllBytes(keyPath, key);
            File.WriteAllBytes(aadPath, aad);
            Call("open", cipher, fromNative);
            Require(Sha(fromNative) == plainSha, "Java did not open exact native plaintext.");
            Call("seal", plain, fromJava);
            long length = new FileInfo(plain).Length;
            using var file = OpenFile(fromJava);
            using var reader = new Reader(native, file, key, aad, length);
            Require(Hash(length, reader.Read, true).Sha256 == plainSha, "Native did not open exact Java plaintext.");
            return new { PlainSha256 = plainSha, JavaCipherSha256 = Sha(fromJava), NativeCipherSha256 = Sha(cipher),
                AadSha256 = Hex(SHA256.HashData(aad)), OracleSourceSha256 = Sha(source) };
        }
        finally { File.Delete(keyPath); }
    }
}
