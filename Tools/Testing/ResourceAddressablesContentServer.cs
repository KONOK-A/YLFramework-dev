using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YLFramework.Testing
{
    // A test-only, read-only origin for this acceptance build, not a general file/download service.
    public sealed class ResourceAddressablesContentServer : IDisposable
    {
        public sealed class Request
        {
            public int round;
            public string path;
            public int status;
            public long bytes;
            public string sha256;
            public string method, receivedUtc, completedUtc;
        }

        private readonly TcpListener _listener;
        private readonly Task _worker;
        private readonly string _root;
        private readonly string _prefix;
        private readonly Dictionary<string, string> _files;
        private readonly ConcurrentQueue<Request> _requests = new ConcurrentQueue<Request>();
        private readonly object _gate = new object();
        private TcpClient _active;
        private volatile bool _stopping;
        private readonly string _gates;
        private readonly string _gatedPath;

        public string BaseUrl { get; }

        public ResourceAddressablesContentServer(string root, string token, IDictionary<string, string> files)
            : this(root, token, files, null, null) { }

        public ResourceAddressablesContentServer(string root, string token, IDictionary<string, string> files, string gates, string gatedPath)
        {
            _root = Path.GetFullPath(root);
            RejectLinks(_root);
            if (!Directory.Exists(_root) || token == null || token.Length != 32 || files == null || files.Count == 0)
                throw new InvalidOperationException("The content fixture requires a frozen owned build.");
            _prefix = "/" + token + "/";
            _files = new Dictionary<string, string>(files, StringComparer.Ordinal);
            _gates = gates;
            _gatedPath = gatedPath;
            if (_gates != null) RejectLinks(_gates);
            foreach (var file in _files)
            {
                if (string.IsNullOrEmpty(file.Key) || file.Key.Contains("..") || file.Key.Contains("\\") ||
                    file.Key.StartsWith("/", StringComparison.Ordinal) || file.Value.Length != 64)
                    throw new InvalidOperationException("Invalid frozen content path or hash.");
                RejectLinks(Path.Combine(_root, file.Key));
            }
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + _prefix;
            _worker = Task.Run(Serve);
        }

        public Request[] Snapshot() => _requests.ToArray();

        private async Task Serve()
        {
            while (!_stopping)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (SocketException) when (_stopping) { return; }
                catch (ObjectDisposedException) when (_stopping) { return; }
                using (client)
                {
                    lock (_gate) _active = client;
                    try { Respond(client); }
                    catch (IOException) when (_stopping) { return; }
                    catch (ObjectDisposedException) when (_stopping) { return; }
                    finally { lock (_gate) _active = null; }
                }
            }
        }

        private void Respond(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            var first = reader.ReadLine() ?? "";
            var fields = first.Split(' ');
            int headerBytes = first.Length;
            for (var i = 0; i < 64; i++)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrEmpty(line)) break;
                headerBytes += line.Length;
                if (headerBytes > 16384) throw new IOException("Fixture HTTP headers are too large.");
            }
            var record = new Request { path = fields.Length > 1 ? fields[1] : "", status = 404,
                method = fields[0], receivedUtc = DateTime.UtcNow.ToString("o") };
            byte[] body = Array.Empty<byte>();
            if (fields.Length == 3 && (fields[0] == "GET" || fields[0] == "HEAD") &&
                fields[1].StartsWith(_prefix, StringComparison.Ordinal))
            {
                var pathAndQuery = fields[1].Substring(_prefix.Length).Split('?');
                var relative = Uri.UnescapeDataString(pathAndQuery[0]);
                if (pathAndQuery.Length == 2 && pathAndQuery[1].StartsWith("round=", StringComparison.Ordinal))
                    int.TryParse(pathAndQuery[1].Substring(6), out record.round);
                record.path = relative;
                if (!relative.Contains("..") && !relative.Contains("\\") && _files.TryGetValue(relative, out var expected))
                {
                    var file = Path.Combine(_root, relative);
                    RejectLinks(file);
                    body = File.ReadAllBytes(file);
                    using var sha = SHA256.Create();
                    record.sha256 = BitConverter.ToString(sha.ComputeHash(body)).Replace("-", "").ToLowerInvariant();
                    record.status = record.sha256 == expected ? 200 : 409;
                    if (record.status != 200) body = Array.Empty<byte>();
                }
            }
            else if (fields.Length == 3 && fields[0] != "GET" && fields[0] != "HEAD") record.status = 405;
            if (_gates != null)
            {
                // Keep attempts, not just successful responses, in the isolated experiment's receipt.
                _requests.Enqueue(record);
                if (record.status == 200 && record.method == "GET" && record.path == _gatedPath && (record.round == 2 || record.round == 4))
                {
                    string stem = Path.Combine(_gates, "round-" + record.round);
                    try { using (File.Open(stem + ".arrived", FileMode.CreateNew)) { } }
                    catch (IOException) when (File.Exists(stem + ".arrived")) { }
                    var timeout = System.Diagnostics.Stopwatch.StartNew();
                    while (!_stopping && !File.Exists(stem + ".release"))
                    {
                        if (timeout.ElapsedMilliseconds > 15000) throw new IOException("The Player did not release the loopback response gate.");
                        Thread.Sleep(5);
                    }
                    if (_stopping) return;
                }
            }
            string reason = record.status == 200 ? "OK" : record.status == 409 ? "Conflict" : "Not Found";
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + record.status + " " + reason +
                "\r\nContent-Length: " + body.Length + "\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            if (fields.Length != 0 && fields[0] == "GET")
            {
                stream.Write(body, 0, body.Length);
                record.bytes = body.Length;
            }
            stream.Flush();
            record.completedUtc = DateTime.UtcNow.ToString("o");
            if (_gates == null) _requests.Enqueue(record);
        }

        private static void RejectLinks(string path)
        {
            for (var cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked content fixture paths are refused.");
        }

        public void Dispose()
        {
            if (_stopping) return;
            _stopping = true;
            _listener.Stop();
            lock (_gate) _active?.Dispose();
            if (!_worker.Wait(10000)) throw new TimeoutException("The content fixture did not stop.");
            _worker.GetAwaiter().GetResult();
        }
    }
}
