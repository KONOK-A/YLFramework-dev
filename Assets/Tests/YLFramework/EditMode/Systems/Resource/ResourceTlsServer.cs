using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YLFramework.Tests.Resource
{
    /// <summary>A GET-only, IPv4-loopback HTTPS fixture. The caller owns the certificate.</summary>
    internal sealed class ResourceTlsServer : IDisposable
    {
        private const int MaxHeaderBytes = 32768;
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly CancellationToken _stopToken;
        private readonly X509Certificate2 _certificate;
        private readonly object _sync = new object();
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly List<Task> _workers = new List<Task>();
        private readonly List<Exception> _errors = new List<Exception>();
        private readonly Task _acceptTask;
        private Func<Request, Response> _responder;
        private int _requestCount;
        private int _disposed;

        internal Uri BaseUri
        {
            get;
        }
        internal int RequestCount => Volatile.Read(ref _requestCount);
        internal IReadOnlyList<Exception> Errors
        {
            get
            {
                lock (_sync)
                {
                    return _errors.ToArray();
                }
            }
        }

        // Responder runs on a worker thread, never on Unity's synchronization context.
        internal Func<Request, Response> Responder
        {
            get => Volatile.Read(ref _responder);
            set => Volatile.Write(ref _responder, value ?? throw new ArgumentNullException(nameof(value)));
        }

        internal ResourceTlsServer(X509Certificate2 certificate, Func<Request, Response> responder = null)
        {
            this._certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
            if (!certificate.HasPrivateKey)
            {
                throw new ArgumentException("A server certificate with a private key is required.", nameof(certificate));
            }

            _stopToken = _cancellation.Token;
            Responder = responder ?? (_ => new Response());
            try
            {
                _listener.Start();
                BaseUri = new Uri("https://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/");
                _acceptTask = Task.Run(AcceptLoopAsync);
            }
            catch
            {
                _listener.Stop();
                _cancellation.Dispose();
                throw;
            }
        }

        internal sealed class Request
        {
            /// <summary>The raw origin-form request target, including any query string.</summary>
            internal string Path
            {
                get;
            }
            internal string Method
            {
                get;
            }
            internal IDictionary<string, string> Headers
            {
                get;
            }
            internal int Number
            {
                get;
            }

            internal Request(string path, string method, IDictionary<string, string> headers, int number)
            {
                Path = path;
                Method = method;
                Headers = new ReadOnlyDictionary<string, string>(headers);
                Number = number;
            }
        }

        internal sealed class Response
        {
            internal int StatusCode { get; set; } = 200;
            internal IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal byte[] Body { get; set; } = Array.Empty<byte>();
            /// <summary>Body bytes to send before closing; Content-Length still defaults to the full body length.</summary>
            internal int? DisconnectAfterBytes
            {
                get; set;
            }
            internal int ChunkSize { get; set; } = 16384;
            internal TimeSpan ChunkDelay
            {
                get; set;
            }
            /// <summary>Use Timeout.InfiniteTimeSpan to withhold headers until disposal.</summary>
            internal TimeSpan HeaderDelay
            {
                get; set;
            }
            /// <summary>Keep the connection open indefinitely after this many body bytes, until disposal.</summary>
            internal int? StallAfterBytes
            {
                get; set;
            }
            /// <summary>Optional measurement of completed TLS body writes, not TLS wire bytes or client receipt.</summary>
            internal Action<int> BodyBytesWritten
            {
                get; set;
            }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stopToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (_stopToken.IsCancellationRequested)
                        {
                            client.Dispose();
                            break;
                        }
                        _clients.Add(client);
                        _workers.Add(Task.Run(() => ServeAsync(client)));
                    }
                }
            }
            catch (ObjectDisposedException) when (_stopToken.IsCancellationRequested) { }
            catch (SocketException) when (_stopToken.IsCancellationRequested) { }
            catch (InvalidOperationException) when (_stopToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Record(exception);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = new SslStream(client.GetStream(), false))
                {
                    client.NoDelay = true;
                    await stream.AuthenticateAsServerAsync(_certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false);
                    var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    if (request == null)
                    {
                        return;
                    }

                    Response response;
                    // Callback IOExceptions are fixture bugs, not expected network disconnects.
                    try
                    {
                        response = request.Method == "GET" ? Responder(request) : new Response
                        {
                            StatusCode = 405,
                            Headers = new Dictionary<string, string> { ["Allow"] = "GET" }
                        };
                        if (response == null)
                        {
                            throw new InvalidOperationException("The TLS fixture responder returned null.");
                        }
                    }
                    catch (Exception exception)
                    {
                        Record(exception);
                        return;
                    }
                    await WriteResponseAsync(stream, response).ConfigureAwait(false);
                }
            }
            // Client cancellation can close the socket during an asynchronous read/write.
            catch (IOException) { }
            catch (SocketException) { }
            catch (AuthenticationException) when (_stopToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stopToken.IsCancellationRequested) { }
            catch (InvalidOperationException) when (_stopToken.IsCancellationRequested) { }
            catch (OperationCanceledException) when (_stopToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Record(exception);
            }
            finally
            {
                lock (_sync)
                {
                    _clients.Remove(client);
                }
            }
        }

        private async Task<Request> ReadRequestAsync(SslStream stream)
        {
            var bytes = new byte[MaxHeaderBytes];
            var length = 0;
            var end = -1;
            while (end < 0)
            {
                if (length == bytes.Length)
                {
                    throw new InvalidDataException("TLS fixture request headers exceed 32768 bytes.");
                }

                var read = await stream.ReadAsync(bytes, length, bytes.Length - length, _stopToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                var start = Math.Max(0, length - 3);
                length += read;
                for (var i = start; i <= length - 4; i++)
                {
                    if (bytes[i] != 13 || bytes[i + 1] != 10 || bytes[i + 2] != 13 || bytes[i + 3] != 10)
                    {
                        continue;
                    }

                    end = i + 4;
                    break;
                }
            }
            for (var i = 0; i < end; i++)
            {
                if (bytes[i] != 9 && bytes[i] != 10 && bytes[i] != 13 && (bytes[i] < 32 || bytes[i] > 126))
                {
                    throw new InvalidDataException("TLS fixture request headers must be ASCII without control characters.");
                }
            }

            var lines = Encoding.ASCII.GetString(bytes, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length != 3 || !IsToken(first[0]) || !first[1].StartsWith("/", StringComparison.Ordinal) ||
                first[1].IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0 || (first[2] != "HTTP/1.1" && first[2] != "HTTP/1.0"))
            {
                throw new InvalidDataException("Malformed TLS fixture request line.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length - 2; i++)
            {
                var separator = lines[i].IndexOf(':');
                if (separator <= 0 || !IsToken(lines[i].Substring(0, separator)))
                {
                    throw new InvalidDataException("Malformed TLS fixture request header.");
                }

                var name = lines[i].Substring(0, separator);
                var value = lines[i].Substring(separator + 1).Trim(' ', '\t');
                if (!IsHeaderValue(value) || headers.ContainsKey(name))
                {
                    throw new InvalidDataException("Invalid or duplicate TLS fixture request header.");
                }

                headers.Add(name, value);
            }
            return new Request(first[1], first[0], headers, Interlocked.Increment(ref _requestCount));
        }

        private async Task WriteResponseAsync(SslStream stream, Response response)
        {
            if (response.StatusCode < 100 || response.StatusCode > 599 || response.ChunkSize <= 0 ||
                response.DisconnectAfterBytes < 0 || response.StallAfterBytes < 0 ||
                !IsDelay(response.HeaderDelay) || !IsDelay(response.ChunkDelay))
            {
                throw new ArgumentException("Invalid TLS fixture response status, byte count or delay.", nameof(response));
            }

            var body = response.Body ?? Array.Empty<byte>();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (response.Headers == null)
            {
                throw new ArgumentException("Response headers cannot be null.", nameof(response));
            }

            foreach (var pair in response.Headers)
            {
                if (!IsToken(pair.Key) || !IsHeaderValue(pair.Value))
                {
                    throw new InvalidDataException("Invalid TLS fixture response header.");
                }

                headers.Add(pair.Key, pair.Value);
            }
            if (!headers.ContainsKey("Content-Length"))
            {
                headers.Add("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture));
            }

            headers.Remove("Connection");
            var text = new StringBuilder("HTTP/1.1 ").Append(response.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(" Fixture\r\n");
            foreach (var pair in headers)
            {
                text.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            }

            text.Append("Connection: close\r\n\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(text.ToString());
            await Task.Delay(response.HeaderDelay, _stopToken).ConfigureAwait(false);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, _stopToken).ConfigureAwait(false);

            var limit = Math.Min(body.Length, response.DisconnectAfterBytes ?? body.Length);
            var offset = 0;
            while (true)
            {
                if (response.StallAfterBytes.HasValue && offset >= response.StallAfterBytes.Value)
                {
                    await Task.Delay(Timeout.Infinite, _stopToken).ConfigureAwait(false);
                }

                if (offset == limit)
                {
                    return;
                }

                var count = Math.Min(response.ChunkSize, limit - offset);
                if (response.StallAfterBytes.HasValue)
                {
                    count = Math.Min(count, response.StallAfterBytes.Value - offset);
                }

                await stream.WriteAsync(body, offset, count, _stopToken).ConfigureAwait(false);
                response.BodyBytesWritten?.Invoke(count);
                offset += count;
                if (offset < limit)
                {
                    await Task.Delay(response.ChunkDelay, _stopToken).ConfigureAwait(false);
                }
            }
        }

        private static bool IsDelay(TimeSpan delay) => delay == Timeout.InfiniteTimeSpan || (delay >= TimeSpan.Zero && delay.TotalMilliseconds <= int.MaxValue);

        private static bool IsToken(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (var character in value)
            {
                if (!(character >= '0' && character <= '9') && !(character >= 'A' && character <= 'Z') &&
                    !(character >= 'a' && character <= 'z') && "!#$%&'*+-.^_`|~".IndexOf(character) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsHeaderValue(string value)
        {
            if (value == null)
            {
                return false;
            }

            foreach (var character in value)
            {
                if (character != '\t' && (character < 32 || character > 126))
                {
                    return false;
                }
            }

            return true;
        }

        private void Record(Exception exception)
        {
            lock (_sync)
            {
                _errors.Add(exception);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Cancel();
            _listener.Stop();
            TcpClient[] active;
            lock (_sync)
            {
                active = _clients.ToArray();
            }

            foreach (var client in active)
            {
                client.Dispose();
            }

            try
            {
                if (!_acceptTask.Wait(5000))
                {
                    Record(new TimeoutException("TLS fixture accept loop did not stop."));
                }

                Task[] running;
                lock (_sync)
                {
                    running = _workers.ToArray();
                }

                if (!Task.WaitAll(running, 5000))
                {
                    Record(new TimeoutException("TLS fixture connection workers did not stop."));
                }
            }
            catch (AggregateException exception)
            {
                foreach (var failure in exception.Flatten().InnerExceptions)
                {
                    Record(failure);
                }
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }
}
