using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            public string method, target, path, at;
            public int status;
            public long bytes;
            public volatile bool held;
            public volatile bool completed;
        }

        private readonly ConcurrentQueue<Request> _requests = new ConcurrentQueue<Request>();
        private readonly Dictionary<string, (string Path, string Relative)> _routes = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        private readonly List<Task> _handlers = new List<Task>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly int _port;
        private TcpListener _listener;
        private CancellationTokenSource _acceptCancellation;
        private Task _accepting;
        private Task _parseGate;
        private int _interrupt;
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
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(shutdown).ConfigureAwait(false))) { }
                    (string Path, string Relative) route;
                    bool known;
                    lock (_routes) { known = _routes.TryGetValue(row.target, out route); }
                    row.path = known ? route.Relative : row.target;
                    row.status = known && (row.method == "GET" || row.method == "HEAD") ? 200 : 404;
                    using var file = row.status == 200 ? File.OpenRead(route.Path) : null;
                    string reason = row.status == 200 ? "OK" : "Not Found";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {row.status} {reason}\r\nContent-Length: {file?.Length ?? 0}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, shutdown).ConfigureAwait(false);
                    if (file != null && row.method == "GET")
                    {
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
                            using (File.Open(Path.Combine(held, "in-flight"), FileMode.CreateNew)) { }
                            var wait = Stopwatch.StartNew();
                            while (!File.Exists(Path.Combine(held, "release")))
                            {
                                if (_disposed || wait.Elapsed.TotalSeconds >= 40) return;
                                await Task.Delay(10, shutdown).ConfigureAwait(false);
                            }
                        }
                        if (protectedBundle && Interlocked.Exchange(ref _interrupt, 0) == 1)
                        {
                            int prefix = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(file.Length / 2, buffer.Length))).ConfigureAwait(false);
                            await stream.WriteAsync(buffer.AsMemory(0, prefix), shutdown).ConfigureAwait(false);
                            row.bytes += prefix;
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
                }
                catch (IOException) when (row != null || shutdown.IsCancellationRequested) { /* An interrupted request stays in the ledger. */ }
                catch (SocketException) when (row != null || shutdown.IsCancellationRequested) { }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
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
