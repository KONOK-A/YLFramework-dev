using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace YLFramework.Resource
{
    internal sealed class ResourceContentStore
    {
        private const int FormatVersion = 1;
        private const int DigestLength = 32;
        private const int MaxRecordBytes = 4096;
        private const uint MoveReplaceExisting = 0x1;
        private const uint MoveWriteThrough = 0x8;
        private const string StateFile = "state";
        private const string JournalFile = "journal";
        private const string WriterFile = "writer.lock";
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';
        private readonly string _ownerRoot;
        private readonly string _application;
        private readonly string _channel;

        internal ResourceContentStore(string storageRoot, string application, string channel)
        {
            if (string.IsNullOrWhiteSpace(storageRoot))
            {
                throw new ArgumentException("A content storage root is required.", nameof(storageRoot));
            }

            ValidateIdentity(application, nameof(application));
            ValidateIdentity(channel, nameof(channel));
            _application = application;
            _channel = channel;
            _ownerRoot = Path.Combine(Path.GetFullPath(storageRoot), "YLResourceContent");
            using var identity = new MemoryStream();
            using var writer = new BinaryWriter(identity, Utf8, leaveOpen: true);
            writer.Write(application);
            writer.Write(channel);
            writer.Flush();
            var directory = BitConverter.ToString(Hash(identity.ToArray())).Replace("-", "").ToLowerInvariant();
            Root = Path.Combine(_ownerRoot, directory);
        }

        internal string Root
        {
            get;
        }

        internal string VersionsRoot => Path.Combine(Root, "versions");

        // 参数为 true 时，状态替换已完成。验收宿主在这两个紧邻写盘的边界停住进程。
        internal Action<bool> StateCommitBoundary
        {
            get; set;
        }

        internal State ReadState()
        {
            State state = ReadRecord(StateFile, ReadStatePayload);
            if (state != null || (!IsRegularFile(Path.Combine(Root, JournalFile)) && !HasPublishedVersion()))
            {
                return state;
            }

            // v1 的空 journal 也只能在成功首次启动之后产生。它与已发布的清单只说明曾有状态，不能重建最高版本。
            // 无写者锁的查询可能跨过另一进程的首次提交。此时重新读已提交的记录。
            return ReadRecord(StateFile, ReadStatePayload) ??
                throw new InvalidDataException("The committed resource content state is missing despite existing installation history.");
        }

        private bool HasPublishedVersion()
        {
            if (!EnsureOwnedDirectory(VersionsRoot, create: false))
            {
                return false;
            }

            foreach (string path in Directory.EnumerateFiles(VersionsRoot, "*.json"))
            {
                if (Path.GetExtension(path) == ".json" && IsDigest(Path.GetFileNameWithoutExtension(path)) && IsRegularFile(path))
                {
                    return true;
                }
            }

            return false;
        }

        internal bool TryAcquireWriter(out Writer writer)
        {
            EnsureOwnedDirectory(_ownerRoot, create: true);
            EnsureOwnedDirectory(Root, create: true);
            string lockPath = Path.Combine(Root, WriterFile);
            IsRegularFile(lockPath);

            FileStream stream = null;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!IsWindows)
                {
                    // Unix Mono 的 FileShare 只在进程内生效，另持有系统字节范围锁。
                    stream.Lock(0, 1);
                }

                writer = new Writer(this, stream);
                stream = null;
                return true;
            }
            catch (IOException error) when (IsLockContention(error))
            {
                writer = null;
                return false;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        private Journal ReadJournal() =>
            ReadRecord(JournalFile, ReadJournalPayload) ?? new Journal(null, null, null, null);

        private bool TryCommit(long expectedGeneration, State next, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State current = ReadState();
            if ((current?.Generation ?? 0) != expectedGeneration)
            {
                return false;
            }

            if (next == null || next.Generation != checked(expectedGeneration + 1) ||
                !IsAllowedTransition(current, next))
            {
                throw new ArgumentException("Invalid resource content state transition.", nameof(next));
            }

            WriteRecord(StateFile, writer => WriteState(writer, next), current != null, cancellationToken,
                StateCommitBoundary);
            return true;
        }

        private void WriteJournal(Journal journal, CancellationToken cancellation = default)
        {
            if (journal == null)
            {
                throw new ArgumentNullException(nameof(journal));
            }

            // 已损坏的意图必须被报告，不能在准备或回滚请求中静默覆盖。
            bool exists = ReadRecord(JournalFile, ReadJournalPayload) != null;
            WriteRecord(JournalFile, writer => WriteJournal(writer, journal), exists, cancellation, null);
        }

        private T ReadRecord<T>(string record, Func<BinaryReader, T> readPayload) where T : class
        {
            EnsureOwnedDirectory(_ownerRoot, create: false);
            EnsureOwnedDirectory(Root, create: false);
            string path = Path.Combine(Root, record);
            if (!IsRegularFile(path))
            {
                return null;
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var input = new BinaryReader(file, Utf8, leaveOpen: true);
            long length = file.Length;
            if (length <= DigestLength || length > MaxRecordBytes)
            {
                throw new InvalidDataException($"Invalid resource content {record} length.");
            }

            byte[] payload = input.ReadBytes((int)length - DigestLength);
            byte[] digest = input.ReadBytes(DigestLength);
            if (!CryptographicOperations.FixedTimeEquals(digest, Hash(payload)) || file.ReadByte() != -1)
            {
                throw new InvalidDataException($"Invalid resource content {record} checksum.");
            }

            using var data = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(data, Utf8, leaveOpen: true);
            try
            {
                if (reader.ReadInt32() != FormatVersion || reader.ReadString() != record ||
                    reader.ReadString() != _application || reader.ReadString() != _channel)
                {
                    throw new InvalidDataException($"Invalid resource content {record} format or identity.");
                }

                T result = readPayload(reader);
                if (data.Position != data.Length)
                {
                    throw new InvalidDataException($"Unexpected data in resource content {record}.");
                }

                return result;
            }
            catch (Exception error) when (error is ArgumentException || error is EndOfStreamException ||
                error is FormatException)
            {
                throw new InvalidDataException($"Invalid resource content {record} payload.", error);
            }
        }

        private void WriteRecord(string record, Action<BinaryWriter> writePayload, bool replace,
            CancellationToken cancellationToken, Action<bool> commitBoundary)
        {
            using var data = new MemoryStream();
            using var writer = new BinaryWriter(data, Utf8, leaveOpen: true);
            writer.Write(FormatVersion);
            writer.Write(record);
            writer.Write(_application);
            writer.Write(_channel);
            writePayload(writer);
            writer.Flush();
            byte[] payload = data.ToArray();
            byte[] digest = Hash(payload);

            string destination = Path.Combine(Root, record);
            string temporary = Path.Combine(Root, $".{record}-{Guid.NewGuid():N}.tmp");
            bool ownsTemporary = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    file.Write(payload, 0, payload.Length);
                    file.Write(digest, 0, digest.Length);
                    file.Flush(flushToDisk: true);
                }

                commitBoundary?.Invoke(false);
                cancellationToken.ThrowIfCancellationRequested();
                ReplaceRecord(temporary, destination, replace);
                ownsTemporary = false;
                commitBoundary?.Invoke(true);
            }
            finally
            {
                if (ownsTemporary)
                {
                    DeleteTemporary(temporary);
                }
            }
        }

        private static void ReplaceRecord(string temporary, string destination, bool replace)
        {
            if (IsWindows)
            {
                // 不允许跨卷复制。ReplaceFileW 的部分失败模式会丢失原路径，改用同卷重命名。
                uint flags = MoveWriteThrough | (replace ? MoveReplaceExisting : 0);
                if (!MoveFileExW(WindowsPath(temporary), WindowsPath(destination), flags))
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error());
                    throw new IOException("Could not commit the resource content record.", error);
                }
            }
            else if (replace)
            {
                File.Replace(temporary, destination, null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }

        private static string WindowsPath(string path)
        {
            if (path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }

            return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

        private static void DeleteTemporary(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 原始提交异常交给调用方。未提交的临时文件不会成为版本记录。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。下次读取只接受确定的记录路径。
            }
        }

        private int CleanupTemporaryRecords(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using var directory = ResourceContentFileIO.PinDirectory(Root, false);
            int removed = 0;
            foreach (string path in Directory.EnumerateFiles(Root))
            {
                cancellation.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                if ((IsTemporaryRecordName(name, StateFile) || IsTemporaryRecordName(name, JournalFile)) &&
                    ResourceContentFileIO.TryRemoveTemporaryFile(path, cancellation))
                {
                    removed++;
                }
            }

            return removed;
        }

        private static bool IsTemporaryRecordName(string name, string record)
        {
            const string suffix = ".tmp";
            const int guidLength = 32;
            string prefix = $".{record}-";
            if (name.Length != prefix.Length + guidLength + suffix.Length ||
                !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            string identifier = name.Substring(prefix.Length, guidLength);
            return Guid.TryParseExact(identifier, "N", out var guid) && guid.ToString("N") == identifier;
        }

        private static bool IsAllowedTransition(State current, State next)
        {
            if (current == null)
            {
                return next.Previous == null && next.Active.Equals(next.Highest);
            }

            if (next.Active.Sequence > current.Highest.Sequence)
            {
                return next.Active.Equals(next.Highest) && current.Active.Equals(next.Previous);
            }

            return current.Previous != null && current.Previous.Equals(next.Active) &&
                next.Previous == null && current.Highest.Equals(next.Highest);
        }

        private static bool IsLockContention(IOException error)
        {
            int code = error.HResult & 0xffff;
            return code == 32 || code == 33 || (!IsWindows && (code == 11 || code == 13 || code == 35));
        }

        private static bool EnsureOwnedDirectory(string path, bool create)
        {
            FileAttributes? attributes = ReadAttributes(path);
            if (attributes == null && create)
            {
                Directory.CreateDirectory(path);
                attributes = File.GetAttributes(path);
            }

            if (attributes.HasValue &&
                (attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                throw new IOException($"Invalid resource content storage directory '{path}'.");
            }

            return attributes.HasValue;
        }

        private static bool IsRegularFile(string path)
        {
            FileAttributes? attributes = ReadAttributes(path);
            if (attributes.HasValue &&
                (attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException($"Invalid resource content record path '{path}'.");
            }

            return attributes.HasValue;
        }

        private static FileAttributes? ReadAttributes(string path)
        {
            try
            {
                return File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        private static void ValidateIdentity(string identity, string parameter)
        {
            if (string.IsNullOrWhiteSpace(identity) || Utf8.GetByteCount(identity) > 128)
            {
                throw new ArgumentException("A content storage identity is required (maximum 128 bytes).", parameter);
            }
        }

        internal static bool IsDigest(string digest)
        {
            if (digest == null || digest.Length != DigestLength * 2)
            {
                return false;
            }

            foreach (char character in digest)
            {
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f'))
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(bytes);
        }

        private static State ReadStatePayload(BinaryReader reader) =>
            new(reader.ReadInt64(), ReadVersion(reader), ReadVersion(reader), ReadVersion(reader));

        private static void WriteState(BinaryWriter writer, State state)
        {
            writer.Write(state.Generation);
            WriteVersion(writer, state.Active);
            WriteVersion(writer, state.Previous);
            WriteVersion(writer, state.Highest);
        }

        private static Journal ReadJournalPayload(BinaryReader reader)
        {
            Version ready = ReadVersion(reader);
            Intent activation = ReadIntent(reader);
            Intent rollback = ReadIntent(reader);
            Attempt attempt = ReadPresence(reader) ? new Attempt(ReadIntent(reader), ReadPresence(reader)) : null;
            return new Journal(ready, activation, rollback, attempt);
        }

        private static void WriteJournal(BinaryWriter writer, Journal journal)
        {
            WriteVersion(writer, journal.Ready);
            WriteIntent(writer, journal.Activation);
            WriteIntent(writer, journal.Rollback);
            writer.Write(journal.Attempt != null);
            if (journal.Attempt != null)
            {
                WriteIntent(writer, journal.Attempt.Intent);
                writer.Write(journal.Attempt.IsRollback);
            }
        }

        private static Intent ReadIntent(BinaryReader reader) => ReadPresence(reader)
            ? new Intent(ReadVersion(reader), reader.ReadInt64(), ReadVersion(reader))
            : null;

        private static void WriteIntent(BinaryWriter writer, Intent intent)
        {
            writer.Write(intent != null);
            if (intent != null)
            {
                WriteVersion(writer, intent.Target);
                writer.Write(intent.ExpectedGeneration);
                WriteVersion(writer, intent.ExpectedActive);
            }
        }

        private static Version ReadVersion(BinaryReader reader) => ReadPresence(reader)
            ? new Version(reader.ReadInt64(), reader.ReadString())
            : null;

        private static void WriteVersion(BinaryWriter writer, Version version)
        {
            writer.Write(version != null);
            if (version != null)
            {
                writer.Write(version.Sequence);
                writer.Write(version.Digest);
            }
        }

        private static bool ReadPresence(BinaryReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1)
            {
                throw new InvalidDataException("Invalid resource content record flag.");
            }

            return value == 1;
        }

        internal sealed class Writer : IDisposable
        {
            private readonly ResourceContentStore _store;
            private readonly object _gate = new();
            private FileStream _lease;

            internal Writer(ResourceContentStore store, FileStream lease)
            {
                _store = store;
                _lease = lease;
            }

            internal State ReadState()
            {
                lock (_gate)
                {
                    RequireLease();
                    return _store.ReadState();
                }
            }

            internal Journal ReadJournal()
            {
                lock (_gate)
                {
                    RequireLease();
                    return _store.ReadJournal();
                }
            }

            internal void WriteJournal(Journal journal, CancellationToken cancellation = default)
            {
                lock (_gate)
                {
                    RequireLease();
                    _store.WriteJournal(journal, cancellation);
                }
            }

            internal int CleanupTemporaryRecords(CancellationToken cancellation)
            {
                lock (_gate)
                {
                    RequireLease();
                    return _store.CleanupTemporaryRecords(cancellation);
                }
            }

            internal bool TryCommit(long expectedGeneration, State next, CancellationToken cancellationToken = default)
            {
                lock (_gate)
                {
                    RequireLease();
                    return _store.TryCommit(expectedGeneration, next, cancellationToken);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _lease?.Dispose();
                    _lease = null;
                }
            }

            private void RequireLease()
            {
                if (_lease == null)
                {
                    throw new ObjectDisposedException(nameof(Writer));
                }
            }
        }

        internal sealed class Version : IEquatable<Version>
        {
            internal Version(long sequence, string digest)
            {
                if (sequence <= 0 || !IsDigest(digest))
                {
                    throw new ArgumentException("Invalid resource content version.");
                }

                Sequence = sequence;
                Digest = digest;
            }

            internal long Sequence
            {
                get;
            }
            internal string Digest
            {
                get;
            }

            public bool Equals(Version other) => other != null && Sequence == other.Sequence && Digest == other.Digest;
            public override bool Equals(object obj) => obj is Version version && Equals(version);
            public override int GetHashCode() => HashCode.Combine(Sequence, Digest);
        }

        internal sealed class State
        {
            internal State(long generation, Version active, Version previous, Version highest)
            {
                if (generation <= 0 || active == null || highest == null)
                {
                    throw new ArgumentException("Invalid committed resource content state.");
                }

                bool isBaseline = generation == 1 && previous == null && active.Equals(highest);
                bool isActivation = generation >= 2 && previous != null &&
                    previous.Sequence < active.Sequence && active.Equals(highest);
                bool isRollback = generation >= 3 && previous == null && active.Sequence < highest.Sequence;
                if (!isBaseline && !isActivation && !isRollback)
                {
                    throw new ArgumentException("Invalid committed resource content state.");
                }

                Generation = generation;
                Active = active;
                Previous = previous;
                Highest = highest;
            }

            internal long Generation
            {
                get;
            }
            internal Version Active
            {
                get;
            }
            internal Version Previous
            {
                get;
            }
            internal Version Highest
            {
                get;
            }
            internal long HighestSequence => Highest.Sequence;
            internal string HighestDigest => Highest.Digest;
        }

        internal sealed class Intent
        {
            internal Intent(Version target, long expectedGeneration, Version expectedActive)
            {
                if (target == null || expectedGeneration < 0 ||
                    (expectedGeneration == 0) != (expectedActive == null) || target.Equals(expectedActive))
                {
                    throw new ArgumentException("Invalid resource content intent.");
                }

                Target = target;
                ExpectedGeneration = expectedGeneration;
                ExpectedActive = expectedActive;
            }

            internal Version Target
            {
                get;
            }
            internal long ExpectedGeneration
            {
                get;
            }
            internal Version ExpectedActive
            {
                get;
            }

            internal bool Matches(State state) =>
                ExpectedGeneration == (state?.Generation ?? 0) && Equals(ExpectedActive, state?.Active);

            internal bool IsSameIntent(Intent intent) => intent != null && Target.Equals(intent.Target) &&
                ExpectedGeneration == intent.ExpectedGeneration && Equals(ExpectedActive, intent.ExpectedActive);
        }

        internal sealed class Attempt
        {
            internal Attempt(Intent intent, bool isRollback)
            {
                Intent = intent ?? throw new ArgumentNullException(nameof(intent));
                IsRollback = isRollback;
            }

            internal Intent Intent
            {
                get;
            }
            internal bool IsRollback
            {
                get;
            }
        }

        internal sealed class Journal
        {
            internal Journal(Version ready, Intent activation, Intent rollback, Attempt attempt)
            {
                if ((activation != null && !activation.Target.Equals(ready)) ||
                    (rollback != null && rollback.ExpectedGeneration == 0) ||
                    (attempt != null && !attempt.Intent.IsSameIntent(attempt.IsRollback ? rollback : activation)))
                {
                    throw new ArgumentException("Invalid resource content journal.");
                }

                Ready = ready;
                Activation = activation;
                Rollback = rollback;
                Attempt = attempt;
            }

            internal Version Ready
            {
                get;
            }
            internal Intent Activation
            {
                get;
            }
            internal Intent Rollback
            {
                get;
            }
            internal Attempt Attempt
            {
                get;
            }
        }
    }
}
