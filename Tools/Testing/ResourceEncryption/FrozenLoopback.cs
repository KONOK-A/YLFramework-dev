using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YLFramework.Testing
{
    public sealed class FrozenLoopback : IDisposable
    {
        public sealed class Request
        {
            public string method, target, path, at, endedAt, termination, entity, range, ifRange, contentRange, contentEncoding;
            public int status;
            public long bytes, declared, startOffset, requestHeaderBytes, responseHeaderBytes;
            public volatile bool held;
            public volatile bool completed;
        }

        private readonly ConcurrentQueue<Request> _requests = new ConcurrentQueue<Request>();
        private readonly Dictionary<string, (string Path, string Relative)> _routes = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Rate, long Stop, string Entity, string Behavior)> _shaped =
            new Dictionary<string, (long, long, string, string)>(StringComparer.Ordinal);
        private readonly List<Task> _handlers = new List<Task>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly int _port;
        private TcpListener _listener;
        private CancellationTokenSource _acceptCancellation;
        private Task _accepting;
        private Task _parseGate;
        private int _interrupt;
        private string _interruptRoute;
        private string _updateAnnouncementRoute;
        private string _heldDirectory;
        private volatile bool _disposed;

        public FrozenLoopback()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            StartAccepting();
        }

        public string Prefix => "http://127.0.0.1:" + _port + "/";
        public Request[] Snapshot() => _requests.ToArray();
        public static void RequireQuiet(Request[] requests)
        {
            if (requests.Length != 0) throw new InvalidDataException($"Online warm endpoint received {requests.Length} content request(s).");
        }

        public void SetPlainControl(string root, IEnumerable<string> paths)
        {
            lock (_routes)
            {
                foreach (var file in paths) Add("/p/", root, file);
            }
        }

        public void AddUpdateArtifact(string root, string route, string relative)
        {
            ValidateUpdateRoute(route);
            lock (_routes) _routes.Add(route, (ResolveUpdateFile(root, relative), relative));
        }

        public void PublishUpdateAnnouncement(string root, string route, string relative)
        {
            ValidateUpdateRoute(route);
            string path = ResolveUpdateFile(root, relative);
            lock (_routes)
            {
                if (_updateAnnouncementRoute == null && _routes.ContainsKey(route) ||
                    _updateAnnouncementRoute != null && _updateAnnouncementRoute != route)
                    throw new InvalidOperationException("Use one dedicated announcement route without replacing immutable files.");
                _updateAnnouncementRoute = route;
                _routes[route] = (path, relative);
            }
        }

        public void InterruptNextUpdateTransfer(string route)
        {
            ValidateUpdateRoute(route);
            if (Interlocked.CompareExchange(ref _interruptRoute, route, null) != null)
                throw new InvalidOperationException("An update transfer interruption is already armed.");
        }

        public void ShapeTransfer(string route, long bytesPerSecond, long stopAfter, string sha256, string behavior = "honor")
        {
            ValidateUpdateRoute(route);
            if (bytesPerSecond < 1 || stopAfter < 0 || sha256.Length != 64 ||
                !new[] { "honor", "ignore", "wrong-range", "short-range", "changed", "weak", "missing-etag", "changed-range",
                    "unsatisfiable", "bad-encoding" }.Contains(behavior) ||
                Array.Exists(sha256.ToCharArray(), character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("Invalid transfer experiment parameters.");
            lock (_routes)
            {
                if (!_routes.ContainsKey(route))
                    throw new ArgumentException("The experiment requires a registered artifact route.", nameof(route));
                _shaped[route] = (bytesPerSecond, stopAfter, (behavior == "weak" ? "W/" : "") + '"' + sha256 + '"', behavior);
            }
        }

        public void ClearTransferShape(string route)
        {
            lock (_routes) _shaped.Remove(route);
        }

        private static void ValidateUpdateRoute(string route)
        {
            if (string.IsNullOrEmpty(route) || !route.StartsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("An update route must start with /.");
            ValidateUpdatePath(route.Substring(1));
        }

        private static void ValidateUpdatePath(string relative)
        {
            if (string.IsNullOrEmpty(relative) || Array.Exists(relative.Split('/'), part =>
                part.Length == 0 || part == "." || part == ".." ||
                part.IndexOfAny(new[] { '\\', ':', '%', '?', '#' }) >= 0))
                throw new InvalidDataException("An update path must identify an owned file.");
        }

        private static string ResolveUpdateFile(string root, string relative)
        {
            ValidateUpdatePath(relative);
            var path = root;
            foreach (string part in relative.Split('/'))
            {
                path = Path.Combine(path, part);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Unexpected update source link.");
            }
            return path;
        }

        public void InterruptNextProtectedTransfer() => Interlocked.Exchange(ref _interrupt, 1);

        public void HoldNextRequestParsing(Task gate)
        {
            if (gate == null || Interlocked.CompareExchange(ref _parseGate, gate, null) != null)
                throw new InvalidOperationException("A request parsing gate is already active or invalid.");
        }

        public void HoldNextProtectedTransfer(string directory)
        {
            if (!Directory.Exists(directory) || File.Exists(Path.Combine(directory, "in-flight")) || File.Exists(Path.Combine(directory, "release")) ||
                Interlocked.CompareExchange(ref _heldDirectory, directory, null) != null)
                throw new InvalidOperationException("The held transfer requires a fresh gate directory.");
        }

        public void SetRoutes(string protectedRoot, string ordinaryRoot, IEnumerable<string> cipher, IEnumerable<string> plain)
        {
            lock (_routes)
            {
                foreach (var file in cipher) Add("/r/Delivery/", protectedRoot, file);
                foreach (var file in plain) Add("/o/", ordinaryRoot, file);
            }
        }

        private void Add(string segment, string root, string relative)
        {
            var parts = relative.Split('/');
            if (parts.Length > (segment == "/r/Delivery/" ? 2 : 1) || Array.Exists(parts,
                part => part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(new[] { '\\', ':', '%', '?', '#' }) >= 0))
                throw new InvalidDataException("Fixture route must name one owned file.");
            string path = Path.Combine(root, relative);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected loopback source link.");
            _routes.Add(segment + relative, (path, relative));
        }

        private void StartAccepting()
        {
            _acceptCancellation = new CancellationTokenSource();
            _accepting = AcceptAsync(_listener, _acceptCancellation.Token);
        }

        private async Task StopAcceptingAsync()
        {
            _acceptCancellation.Cancel();
            try { await _accepting.ConfigureAwait(false); }
            finally { _acceptCancellation.Dispose(); }
        }

        private void Track(TcpClient client)
        {
            _handlers.RemoveAll(task => task.IsCompletedSuccessfully);
            _handlers.Add(ProcessAsync(client));
        }

        private async Task AcceptAsync(TcpListener listener, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    Track(client);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private async Task ProcessAsync(TcpClient client)
        {
            using (client)
            {
                Request row = null;
                var shutdown = _shutdown.Token;
                try
                {
                    var gate = Interlocked.Exchange(ref _parseGate, null);
                    if (gate != null) await gate.WaitAsync(shutdown).ConfigureAwait(false);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var first = await reader.ReadLineAsync(shutdown).ConfigureAwait(false);
                    if (first == null) return;
                    string[] tokens = first.Split(' ');
                    row = new Request { method = tokens[0], target = tokens.Length > 1 ? tokens[1] : "", at = DateTime.UtcNow.ToString("o") };
                    _requests.Enqueue(row); // Count every attempt at ingress, including HEAD/404/short writes.
                    row.requestHeaderBytes = first.Length + 4;
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(shutdown).ConfigureAwait(false)))
                    {
                        row.requestHeaderBytes += line.Length + 2;
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                            row.range = line.Substring(6).Trim();
                        else if (line.StartsWith("If-Range:", StringComparison.OrdinalIgnoreCase))
                            row.ifRange = line.Substring(9).Trim();
                    }
                    (string Path, string Relative) route;
                    (long Rate, long Stop, string Entity, string Behavior) shape;
                    bool known;
                    lock (_routes)
                    {
                        known = _routes.TryGetValue(row.target, out route);
                        _shaped.TryGetValue(row.target, out shape);
                    }
                    row.path = known ? route.Relative : row.target;
                    row.status = known && (row.method == "GET" || row.method == "HEAD") ? 200 : 404;
                    using var file = row.status == 200 ? File.OpenRead(route.Path) : null;
                    row.declared = file?.Length ?? 0;
                    row.entity = shape.Behavior == "missing-etag" ? null : shape.Entity;
                    string coding = "";
                    if (file != null && row.method == "GET" && shape.Rate > 0 && shape.Behavior is not ("ignore" or "weak" or "missing-etag") &&
                        row.ifRange == row.entity && row.range != null && row.range.StartsWith("bytes=", StringComparison.Ordinal) &&
                        row.range.EndsWith("-", StringComparison.Ordinal) && long.TryParse(row.range.AsSpan(6, row.range.Length - 7), out long offset))
                    {
                        if (offset < 0 || offset >= file.Length || shape.Behavior == "unsatisfiable")
                        {
                            row.status = 416;
                            row.contentRange = $"bytes */{file.Length}";
                            row.declared = 0;
                        }
                        else
                        {
                            row.status = 206;
                            row.startOffset = offset;
                            long end = shape.Behavior == "short-range" && offset < file.Length / 2 ? file.Length / 2 - 1 : file.Length - 1;
                            row.declared = end - offset + 1;
                            row.contentRange = $"bytes {(shape.Behavior == "wrong-range" ? offset - 1 : offset)}-{end}/{file.Length}";
                            if (shape.Behavior == "changed-range")
                                row.entity = "\"changed-entity\"";
                            if (shape.Behavior == "bad-encoding")
                            {
                                row.contentEncoding = "gzip";
                                coding = "Content-Encoding: gzip\r\n";
                            }
                            file.Position = offset;
                        }
                    }
                    string reason = row.status switch { 200 => "OK", 206 => "Partial Content", 416 => "Range Not Satisfiable", _ => "Not Found" };
                    string entityHeader = row.entity == null ? "" : $"ETag: {row.entity}\r\n";
                    string rangeHeader = row.contentRange == null ? "" : $"Content-Range: {row.contentRange}\r\n";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {row.status} {reason}\r\nContent-Length: {row.declared}\r\n{entityHeader}{rangeHeader}{coding}Connection: close\r\n\r\n");
                    row.responseHeaderBytes = header.Length;
                    await stream.WriteAsync(header, shutdown).ConfigureAwait(false);
                    if (file != null && row.method == "GET")
                    {
                        if (shape.Rate > 0)
                        {
                            var elapsed = Stopwatch.StartNew();
                            long end = shape.Stop > 0 ? Math.Clamp(shape.Stop - row.startOffset, 0, row.declared) : row.declared;
                            var slice = new byte[(int)Math.Min(64 * 1024, Math.Max(1, shape.Rate / 8))];
                            while (row.bytes < end)
                            {
                                int chunk = (int)Math.Min(slice.Length, end - row.bytes);
                                var wait = TimeSpan.FromSeconds((double)(row.bytes + chunk) / shape.Rate) - elapsed.Elapsed;
                                if (wait > TimeSpan.Zero)
                                    await Task.Delay(wait, shutdown).ConfigureAwait(false);
                                await file.ReadExactlyAsync(slice.AsMemory(0, chunk), shutdown).ConfigureAwait(false);
                                if (shape.Behavior == "changed" && row.startOffset == 0 && row.bytes == 0)
                                    slice[0] ^= 1;
                                await stream.WriteAsync(slice.AsMemory(0, chunk), shutdown).ConfigureAwait(false);
                                row.bytes += chunk;
                            }
                            if (row.bytes != row.declared)
                            {
                                row.termination = "EndpointTruncated";
                                await stream.FlushAsync(shutdown).ConfigureAwait(false);
                                return;
                            }
                            row.termination = "Complete";
                            row.completed = true;
                            return;
                        }
                        var buffer = new byte[64 * 1024];
                        bool protectedBundle = row.target.StartsWith("/r/", StringComparison.Ordinal);
                        string held = protectedBundle ? Interlocked.Exchange(ref _heldDirectory, null) : null;
                        if (held != null)
                        {
                            int prefix = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(file.Length / 2, buffer.Length))).ConfigureAwait(false);
                            if (prefix == 0) throw new IOException("The held transfer must contain a nonempty partial body.");
                            await stream.WriteAsync(buffer.AsMemory(0, prefix), shutdown).ConfigureAwait(false);
                            row.bytes += prefix;
                            row.held = true;
                            string marker = Path.Combine(held, "in-flight");
                            string pending = marker + ".tmp";
                            using (var writer = new StreamWriter(File.Open(pending, FileMode.CreateNew)))
                                writer.Write(prefix.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            File.Move(pending, marker);
                            var wait = Stopwatch.StartNew();
                            while (!File.Exists(Path.Combine(held, "release")))
                            {
                                if (_disposed || wait.Elapsed.TotalSeconds >= 40) return;
                                await Task.Delay(10, shutdown).ConfigureAwait(false);
                            }
                        }
                        string updateInterrupt = Volatile.Read(ref _interruptRoute);
                        bool interruptUpdate = updateInterrupt == row.target &&
                            Interlocked.CompareExchange(ref _interruptRoute, null, updateInterrupt) == updateInterrupt;
                        if (interruptUpdate || (protectedBundle && Interlocked.Exchange(ref _interrupt, 0) == 1))
                        {
                            int prefix;
                            if (interruptUpdate)
                            {
                                prefix = (int)Math.Min(file.Length / 2, 384 * 1024);
                                if (prefix == 0) throw new IOException("The update transfer must have a nonempty partial body.");
                                buffer = new byte[prefix];
                                await file.ReadExactlyAsync(buffer, shutdown).ConfigureAwait(false);
                            }
                            else
                                prefix = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(file.Length / 2, buffer.Length))).ConfigureAwait(false);
                            await stream.WriteAsync(buffer.AsMemory(0, prefix), shutdown).ConfigureAwait(false);
                            row.bytes += prefix;
                            if (interruptUpdate) row.held = true;
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                            deadline.CancelAfter(TimeSpan.FromSeconds(40));
                            try { await stream.ReadAsync(buffer.AsMemory(0, 1), deadline.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { }
                            return;
                        }
                        int count;
                        while ((count = await file.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                        {
                            await stream.WriteAsync(buffer.AsMemory(0, count), shutdown).ConfigureAwait(false);
                            row.bytes += count;
                        }
                    }
                    await stream.FlushAsync(shutdown).ConfigureAwait(false);
                    row.completed = true;
                    row.termination = "Complete";
                }
                catch (IOException) when (row != null || shutdown.IsCancellationRequested)
                {
                    if (row != null) row.termination = "SocketClosed";
                }
                catch (SocketException) when (row != null || shutdown.IsCancellationRequested)
                {
                    if (row != null) row.termination = "SocketClosed";
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                finally
                {
                    if (row != null) row.endedAt = DateTime.UtcNow.ToString("o");
                }
            }
        }

        public Task<Request[]> CaptureRoundAsync() => SealRoundAsync(false);

        private async Task<Request[]> SealRoundAsync(bool pause)
        {
            var listener = _listener;
            if (listener == null) return Snapshot();
            try
            {
                await StopAcceptingAsync().ConfigureAwait(false);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                // After the client exits, drain the listen backlog before sealing its request ledger.
                do
                {
                    while (listener.Pending())
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        Track(listener.AcceptTcpClient());
                    }
                    await Task.WhenAll(_handlers).WaitAsync(deadline.Token).ConfigureAwait(false);
                    _handlers.Clear();
                } while (listener.Pending());
                var result = Snapshot();
                if (!pause) StartAccepting();
                else { listener.Stop(); _listener = null; }
                return result;
            }
            catch
            {
                listener.Stop();
                _listener = null;
                throw;
            }
        }

        public void Pause() => SealRoundAsync(true).GetAwaiter().GetResult();

        public void Resume()
        {
            if (_listener != null) throw new InvalidOperationException("Listener is already active.");
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            StartAccepting();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            try
            {
                if (_listener != null)
                {
                    try { StopAcceptingAsync().GetAwaiter().GetResult(); }
                    finally { _listener.Stop(); _listener = null; }
                }
                Task.WhenAll(_handlers).GetAwaiter().GetResult();
            }
            finally { _shutdown.Dispose(); }
        }
    }
}
