using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
#if LEGACY_CNG
using YLFramework.Resource;
#endif

public static partial class EncryptionStreamMicrobenchmark
{
    private const int Rounds = 5;
    private const int SparseCount = 16;
    private const int LargeLength = 134_219_484;
    private const uint SyntheticSeed = 0x594c5a42;
    private const string CandidateColumn = "combined";
    private const string ShippedSha = "963ba62687c2f895649923138083b78acff7bba470ce92ba90228c27e9a46f5c";
    private static readonly string[] BaselineColumns = { "shipped-double-sha", "shipped-single-sha" };
    private static readonly string[] LegacyColumns = { "cng-old", "cng-matched-double-sha" };
    private static readonly Input[] HistoricalInputs = {
        new("protected_assets_frozen-small_b667ab173ac47f9d66b852fadb075a0d.bundle", 8_390_371, "d1ea6e08492a1766cf6e8de1d67d4c7ec2c04f3fcb43a21657dda0df9e6cff7f"),
        new("protected_assets_frozen-large_da95a496844dcead88da505a964ba584.bundle", LargeLength, "cbca8370e933a9e278be2c83a85d9b1e4412b16f93ec117848352a32a0834745")
    };

    private sealed record Input(string File, long Length, string Sha256);
    private sealed record Cipher(string Path, long Length, string Sha256);
    private sealed record Job(string Raw, Input Input, Cipher TinkCipher = null, Cipher LegacyCipher = null, long[] SparseOffsets = null);
    private sealed record Variant(string Name, Native Native, int CipherShaPasses, bool FullAuthentication, bool Candidate = false);
    private sealed record Budget(long AuthSourceMax, long? SequentialSourceMax, int ExtraAuthHeaderAllowance, long PayloadLimit,
        bool IndependentAuthMatches, bool IndependentReadMatches, bool AuthWithinBudget, bool? SequentialWithinBudget,
        bool ReportedPayloadWithinBudget)
    {
        public bool MeetsSampleGates => IndependentAuthMatches && IndependentReadMatches && AuthWithinBudget &&
            SequentialWithinBudget != false && ReportedPayloadWithinBudget;
    }
    private sealed record Sample(int Order, int Round, long Size, string Mode, string Variant,
        long BytesRead, long ExtraCipherShaBytes, long StableCipherShaBytes, double ExtraCipherShaMs, double StableCipherShaMs,
        double AuthMs, double ReadMs, double TotalMs, long IndependentAuthSource, long IndependentReadSource,
        Counters AuthCounters, Counters AfterReadCounters, long? NativeReadSourceBytes, long? PayloadSourceBound,
        string ReadSha256, Budget NativeBudget);

    public static int Measure(string root, bool freshSeed, bool smoke, string packed, string history, string baselineDll,
        string candidateDll, string candidateSha, string[] names, string[] dlls)
    {
        var variants = new List<Variant>();
        var modules = new List<Native>();
        var samples = new List<Sample>();
        var jobs = new List<Job>();
        var rawFiles = new FileStream[HistoricalInputs.Length];
        var errors = new List<object>();
        var unavailable = new List<string>();
        if (string.IsNullOrEmpty(baselineDll)) unavailable.AddRange(BaselineColumns);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string setupError = null;
#if LEGACY_CNG
        const string legacy = "archived YL envelope: old first/last authentication versus two SHA passes plus ValidateAll";
#else
        const string legacy = "not supplied; legacy CNG columns unavailable";
        unavailable.AddRange(LegacyColumns);
#endif
        string historySha = null;
        bool historicalBytesVerified = false;
        Native baseline = null;
        bool expectsCandidate = !string.IsNullOrEmpty(candidateDll) || names.Contains(CandidateColumn, StringComparer.OrdinalIgnoreCase);
        int rounds = smoke ? 1 : Rounds;
        int measuredInputs = smoke ? 1 : HistoricalInputs.Length;
        DateTimeOffset startUtc = DateTimeOffset.UtcNow;

        Native Load(string path, bool isCandidate = false, string pin = null, string identity = null)
        {
            var module = new Native(path, pin, identity);
            modules.Add(module);
            Require(!isCandidate || module.Sha256 != ShippedSha, "The pinned old DLL cannot be supplied as the candidate.");
            return module;
        }

        void PrepareInputs()
        {
            for (int i = 0; i < HistoricalInputs.Length; i++)
            {
                var expected = HistoricalInputs[i];
                string file = freshSeed ? $"synthetic-{expected.Length}.bin" : expected.File;
                string path = Path.Combine(freshSeed ? root : packed, file);
                if (freshSeed) GenerateSynthetic(path, expected.Length);
                else Require(File.Exists(path), "Historical Bundle is missing: " + path);
                var raw = rawFiles[i] = OpenFile(path);
                Require(raw.Length == expected.Length, (freshSeed ? "Synthetic plaintext length differs: " : "Historical plaintext length differs: ") + path);
                string sha = Hex(SHA256.HashData(raw));
                Require(freshSeed || sha == expected.Sha256, "Whole historical plaintext SHA-256 differs: " + path);
                jobs.Add(new(path, new Input(file, expected.Length, sha)));
            }
        }

        try
        {
            if (!freshSeed)
            {
                Require(!string.IsNullOrEmpty(baselineDll), "Historical replay requires the pinned old baseline DLL.");
                using var oldEvidence = OpenFile(history);
                historySha = Hex(SHA256.HashData(oldEvidence));
                oldEvidence.Position = 0;
                using var document = JsonDocument.Parse(oldEvidence);
                var historyRows = document.RootElement.GetProperty("inputs").EnumerateArray().ToArray();
                Require(historyRows.Length == HistoricalInputs.Length && HistoricalInputs.All(input => historyRows.Count(row =>
                    row.GetProperty("bytes").GetInt64() == input.Length && row.GetProperty("sha256").GetString() == input.Sha256) == 1),
                    "Historical result does not identify exactly the two expected raw Bundles.");
                Require(document.RootElement.GetProperty("pluginSha256").GetString() == ShippedSha,
                    "Historical result names a different baseline DLL.");
                PrepareInputs(); // Verify both Bundles before starting any measurements.
                historicalBytesVerified = true;
            }

            if (!string.IsNullOrEmpty(baselineDll))
            {
                baseline = Load(baselineDll, pin: ShippedSha, identity: "historical baseline");
                variants.Add(new(BaselineColumns[0], baseline, 2, true));
                variants.Add(new(BaselineColumns[1], baseline, 1, true));
            }
            if (!string.IsNullOrEmpty(candidateDll))
                variants.Add(new(CandidateColumn, Load(candidateDll, true, candidateSha, "packaged candidate"), 1, true, true));
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                Require(!string.IsNullOrWhiteSpace(name) &&
                    !BaselineColumns.Contains(name, StringComparer.OrdinalIgnoreCase) && !LegacyColumns.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                    !variants.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), "Duplicate or reserved variant name: " + name);
                bool isCandidate = name.Equals(CandidateColumn, StringComparison.OrdinalIgnoreCase);
                variants.Add(new(name, Load(dlls[i], isCandidate), 1, true, isCandidate));
            }
#if LEGACY_CNG
            Require(CngSegmentAead.IsSupported, "Archived CNG backend isn't available in this process.");
            variants.Add(new(LegacyColumns[0], null, 1, false));
            variants.Add(new(LegacyColumns[1], null, 2, true));
#endif
            var candidate = variants.SingleOrDefault(v => v.Candidate);
            Require(!freshSeed || candidate != null, "FreshSeed requires a candidate DLL.");
            var sealer = baseline ?? candidate.Native;
            if (freshSeed) PrepareInputs();

            _ = ProcessReadBytes(); // Resolve the process-counter P/Invoke before tracing any reads.
            int order = 0;
            for (int inputIndex = 0; inputIndex < measuredInputs; inputIndex++)
            {
                Job job = jobs[inputIndex];
                Input input = job.Input;
                var raw = rawFiles[inputIndex];
                var bucket = new byte[Block];
                var sparse = new (long Offset, byte[] Bytes)[SparseCount];
                for (int block = 0; block < sparse.Length; block++)
                {
                    long offset = (input.Length - Block) * (block * 5 % SparseCount) / (SparseCount - 1);
                    raw.Position = offset;
                    var data = new byte[Block];
                    raw.ReadExactly(data, 0, data.Length);
                    sparse[block] = (offset, data);
                }

                byte[] aad = Aad(input.Length);
                string cipherPath = Path.Combine(root, $"{input.Length}.tink");
                Seal(sealer, job.Raw, cipherPath, key, aad);
                var tink = new Cipher(cipherPath, new FileInfo(cipherPath).Length, Sha(cipherPath));
#if LEGACY_CNG
                var context = new ResourceBundleContext("stream-comparison", "StandaloneWindows64", "2022.3.62f3", "Bundle", input.File);
                string legacyPath = Path.Combine(root, $"{input.Length}.cng");
                raw.Position = 0;
                using (var output = new FileStream(legacyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    ResourceBundleEnvelope.Encrypt(raw, output, context, "stream-comparison-key", key);
                var old = new Cipher(legacyPath, new FileInfo(legacyPath).Length, Sha(legacyPath));
#else
                Cipher old = null;
#endif
                jobs[inputIndex] = job with { TinkCipher = tink, LegacyCipher = old, SparseOffsets = sparse.Select(s => s.Offset).ToArray() };

                var modes = new[] { "sequential-32k", "jump-16x32k" };
                for (int round = smoke ? 0 : -1; round < rounds; round++)
                for (int m = 0; m < modes.Length; m++)
                for (int i = 0; i < variants.Count; i++)
                {
                    string mode = modes[(round + 1 + m) % modes.Length];
                    var variant = variants[(i + round + 1 + m) % variants.Count];
                    int attempt = order++;
                    string phase = "extra cipher SHA";
                    long t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        Cipher cipher = variant.Native == null ? old : tink;
                        long extra = 0;
                        if (variant.CipherShaPasses == 2)
                        {
                            using var probe = OpenFile(cipher.Path);
                            extra = VerifyCipher(probe, cipher);
                        }
                        long tExtra = Stopwatch.GetTimestamp();
                        phase = "owning-handle cipher SHA";
                        using var inputFile = OpenFile(cipher.Path);
                        long bytesHashed = VerifyCipher(inputFile, cipher);
                        long tHash = Stopwatch.GetTimestamp();

                        phase = "pre-authentication";
                        long source1 = ProcessReadBytes();
                        long tAuthStart = Stopwatch.GetTimestamp();
                        using var reader = variant.Native == null ? null : new Reader(variant.Native, inputFile, key, aad, input.Length);
#if LEGACY_CNG
                        using var legacyReader = reader == null ? ResourceBundleEnvelope.OpenDecrypt(inputFile, context, key, leaveOpen: true) : null;
                        if (legacyReader != null && variant.FullAuthentication) legacyReader.ValidateAll(default);
#endif
                        if (reader != null)
                        {
                            Require(Hash(input.Length, reader.Read, true).Sha256 == input.Sha256, "Pre-authenticated plaintext SHA differs.");
                        }
                        long tAuth = Stopwatch.GetTimestamp();
                        long authTrace = ProcessReadBytes() - source1;
                        var before = reader?.Snapshot();
                        phase = "32 KiB checked reads";
                        int Read(long offset)
                        {
                            if (reader != null) return reader.Read(offset, bucket, Block);
#if LEGACY_CNG
                            if (legacyReader.Position != offset) legacyReader.Position = offset;
                            return legacyReader.Read(bucket, 0, Block);
#else
                            throw new InvalidOperationException("No legacy reader was compiled.");
#endif
                        }

                        long bytes = 0;
                        string readSha = null;
                        bool sequential = mode == "sequential-32k";
                        long source2 = ProcessReadBytes();
                        long tReadStart = Stopwatch.GetTimestamp();
                        if (sequential)
                        {
                            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            while (bytes < input.Length)
                            {
                                int read = Read(bytes);
                                Require(read == Math.Min(Block, input.Length - bytes), "Sequential byte count differs.");
                                digest.AppendData(bucket, 0, read);
                                bytes += read;
                            }
                            Require(Read(bytes) == 0, "Sequential EOF differs.");
                            readSha = Hex(digest.GetHashAndReset());
                            Require(readSha == input.Sha256, "Second-pass sequential plaintext SHA differs.");
                        }
                        else
                        {
                            foreach (var spot in sparse)
                            {
                                int read = Read(spot.Offset);
                                Require(read == spot.Bytes.Length && bucket.AsSpan(0, read).SequenceEqual(spot.Bytes), "Jump bytes differ.");
                                bytes += read;
                            }
                        }
                        long tRead = Stopwatch.GetTimestamp(); // Only after validating the returned data and EOF where applicable.
                        long readTrace = ProcessReadBytes() - source2;
                        var end = reader?.Snapshot();
                        long? readSource = end?.Source - before?.Source;
                        long? payload = null;
                        Budget budget = null;
                        if (before != null)
                        {
                            Require(before.Served == input.Length && end.Served - before.Served == bytes &&
                                before.Source >= 0 && end.Source >= before.Source && before.Pages >= 0 && end.Pages >= before.Pages,
                                "Native counters violate served/source identities.");
                            payload = checked(Math.Max(before.NativeSourceBound, end.NativeSourceBound) + ReadLimit + FileBuffer);
                            bool large = input.Length == LargeLength;
                            int headerAllowance = large ? 0 : Header;
                            long singlePass = large ? cipher.Length * 102 / 100 : cipher.Length;
                            long authMax = singlePass + headerAllowance;
                            const long payloadMax = 4L * Segment;
                            budget = new(authMax, sequential ? singlePass : null, headerAllowance, payloadMax,
                                authTrace == before.Source, readTrace == readSource, before.Source >= cipher.Length && before.Source <= authMax,
                                sequential ? readSource <= singlePass : null,
                                before.NativeSourceBound > 0 && before.NativeSourceBound == end.NativeSourceBound && payload <= payloadMax);
                        }
                        samples.Add(new(attempt, round, input.Length, mode, variant.Name, bytes, extra, bytesHashed, Ms(t0, tExtra),
                            Ms(tExtra, tHash), Ms(tAuthStart, tAuth), Ms(tReadStart, tRead), Ms(t0, tRead), authTrace, readTrace, before, end,
                            readSource, payload, readSha, budget));
                    }
                    catch (Exception error)
                    {
                        errors.Add(new { Order = attempt, Round = round, Size = input.Length, Mode = mode, Variant = variant.Name,
                            Phase = phase, ElapsedMs = Ms(t0, Stopwatch.GetTimestamp()), Error = error.ToString() });
                    }
                    finally { CryptographicOperations.ZeroMemory(bucket); }
                }
            }
        }
        catch (Exception error) { setupError = error.ToString(); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            foreach (var raw in rawFiles) raw?.Dispose();
            foreach (var module in modules) module.Dispose();
        }

        var summaries = samples.Where(s => s.Round >= 0).GroupBy(s => (s.Size, s.Mode, s.Variant)).Select(g => new
        {
            g.Key.Size, g.Key.Mode, g.Key.Variant, Samples = g.Count(),
            MedianTotalMs = Median(g.Select(s => s.TotalMs)), MaxTotalMs = g.Max(s => s.TotalMs),
            MedianExtraCipherShaMs = Median(g.Select(s => s.ExtraCipherShaMs)),
            MedianStableCipherShaMs = Median(g.Select(s => s.StableCipherShaMs)),
            MedianAuthMs = Median(g.Select(s => s.AuthMs)), MedianReadMs = Median(g.Select(s => s.ReadMs)),
            AuthSourceBytes = g.Select(s => s.AuthCounters?.Source).Distinct().ToArray(),
            ReadSourceBytes = g.Select(s => s.NativeReadSourceBytes).Distinct().ToArray(),
            NativeEvidenceFailures = g.First().NativeBudget == null ? (int?)null : g.Count(s => !s.NativeBudget.MeetsSampleGates),
        }).OrderBy(s => s.Size).ThenBy(s => s.Mode).ThenBy(s => s.Variant).ToArray();

        bool sampled = setupError == null && errors.Count == 0 && summaries.Length == measuredInputs * variants.Count * 2 &&
            summaries.All(s => s.Samples == rounds);
        string candidateName = variants.SingleOrDefault(v => v.Candidate)?.Name;
        var gateFailures = samples.Where(s => s.Round >= 0 && s.Variant == candidateName && s.NativeBudget?.MeetsSampleGates != true)
            .Select(s => s.Order).ToArray();
        string qualification = !expectsCandidate ? "NotRun" : !sampled ? "Incomplete" : gateFailures.Length != 0 ? "Failed" :
            smoke ? "SmokePassed" : "Passed";
        bool succeeded = sampled && gateFailures.Length == 0;
        bool complete = !smoke && succeeded;
        string path = Path.Combine(root, "result.json");
        Save(path, new
        {
            Version = 2, Complete = complete, RunKind = smoke ? "Smoke" : "Matrix",
            Kind = "ABI microbenchmark with .NET hashing; not the product managed reader, Unity, Player, Tload or disk I/O evidence",
            Runtime = RuntimeInformation.FrameworkDescription, Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ProcessId = Environment.ProcessId,
            StartUtc = startUtc, EndUtc = DateTimeOffset.UtcNow, Block, Rounds = rounds, SparseCount,
            FileCache = smoke ? "no cache reset; diagnostic only, one sample per pattern on the smaller input" :
                "warmed by sealing, full cipher SHA and one interleaved warmup; no OS cache reset",
            ReadValidation = "Timed sequential output includes inline SHA-256 of the second pass and EOF; jump reads compare all bytes before stopping. Not the version 1 sentinel timing workload.",
            SourceCounters = "Independent phase deltas of Windows process ReadTransferCount must match candidate native ReadFile bytes exactly. Process-wide, not physical disk activity; CNG has no native counters.",
            PayloadEvidence = "Reported native source-derived bound plus 128 KiB managed hash and 4 KiB file buffer; not measured high-water. Excludes harness reference bytes and caller's 32 KiB read buffer.",
            MeasuredPayloadHighWaterBytes = (long?)null,
            CandidateSampleGates = qualification, CandidateGateFailures = gateFailures,
            GateScope = "Source and reported payload bounds gate measured candidate samples only. Warmups and comparators are retained for diagnosis; jump reads have no sequential budget.",
            InputMode = freshSeed ? "FreshSeed" : "Historical", HistoricalBytesVerified = historicalBytesVerified,
            SyntheticFixture = freshSeed ? $"xorshift32-le-v1; initial state = 0x{SyntheticSeed:x8} XOR plain length; not historical Bundle bytes" : null,
            Legacy = legacy, BaselineDllSha256 = baseline?.Sha256, ExpectedBaselineDllSha256 = ShippedSha,
            HistoricalResult = freshSeed ? null : history, HistoricalResultSha256 = historySha, UnavailableColumns = unavailable,
            Variants = variants.Select(v => new { v.Name, Role = v.Candidate ? "Candidate" : "Comparator", v.CipherShaPasses,
                v.FullAuthentication, Dll = v.Native?.Path, DllSha256 = v.Native?.Sha256 }),
            Inputs = jobs, Samples = samples, Errors = errors, SetupError = setupError, Summaries = complete ? summaries : null
        });
        if (complete)
        {
            foreach (var s in summaries)
                Console.WriteLine(FormattableString.Invariant($"{s.Size,9} {s.Mode,14} {s.Variant,23} | total {s.MedianTotalMs,8:F2}/{s.MaxTotalMs,8:F2} ms | auth/read {s.MedianAuthMs,8:F2}/{s.MedianReadMs,8:F2} ms"));
        }
        else if (setupError != null) Console.Error.WriteLine(setupError);
        if (gateFailures.Length != 0) Console.Error.WriteLine("Candidate sample gates failed at order(s): " + string.Join(", ", gateFailures));
        Console.WriteLine(path);
        return succeeded ? 0 : Math.Max(1, errors.Count + gateFailures.Length);
    }

    private static void GenerateSynthetic(string path, long length)
    {
        // Explicit integer sequence and byte order; System.Random output is not a fixture format.
        uint state = SyntheticSeed ^ checked((uint)length);
        var buffer = new byte[ReadLimit];
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileBuffer);
        for (long remaining = length; remaining > 0;)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            for (int i = 0; i < count; i += sizeof(uint))
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i, sizeof(uint)), state);
            }
            file.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    private static long VerifyCipher(FileStream file, Cipher expected)
    {
        Require(file.Length == expected.Length, "Cipher length differs.");
        var actual = Hash(expected.Length, (_, bytes, count) => file.Read(bytes, 0, count), false);
        Require(actual.Sha256 == expected.Sha256, "Cipher SHA-256 differs.");
        return actual.Bytes;
    }

    private static double Ms(long start, long end) => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
    private static double Median(IEnumerable<double> data)
    {
        var ordered = data.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }
}
