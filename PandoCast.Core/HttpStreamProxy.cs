using PandoCast.Core.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace PandoCast.Core
{
    public sealed class HttpStreamProxy : IAsyncDisposable
    {
        private static readonly byte[] NotFoundResponse = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        private static readonly byte[] ServerErrorResponse = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

        private readonly Func<PandoraTrack?> _currentTrackProvider;
        private readonly HttpClient _httpClient = new();
        private readonly object _gate = new();
        private CancellationTokenSource? _listenerCancellation;
        private TcpListener? _listener;
        private Task? _listenerTask;

        public HttpStreamProxy(Func<PandoraTrack?> currentTrackProvider)
        {
            _currentTrackProvider = currentTrackProvider;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PandoCast/1.0");
        }

        public int Port { get; private set; }

        public bool IsRunning => _listener != null;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_listener != null) return Task.CompletedTask;

                _listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _listener = CreateListener();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _listenerTask = Task.Run(() => AcceptLoopAsync(_listener, _listenerCancellation.Token), CancellationToken.None);
            }

            return Task.CompletedTask;
        }

        public Uri GetStreamUri(Uri? remoteDeviceUri, PandoraTrack track)
        {
            IPAddress localAddress = LocalNetwork.GetLocalIPv4AddressFor(remoteDeviceUri);
            string token = Uri.EscapeDataString(track.TrackToken);
            return new Uri($"http://{localAddress}:{Port}/cast.m4a?track={token}");
        }

        public async Task StopAsync()
        {
            TcpListener? listener;
            CancellationTokenSource? listenerCancellation;
            Task? listenerTask;

            lock (_gate)
            {
                listener = _listener;
                listenerCancellation = _listenerCancellation;
                listenerTask = _listenerTask;

                _listener = null;
                _listenerCancellation = null;
                _listenerTask = null;
                Port = 0;
            }

            if (listener == null) return;

            listenerCancellation?.Cancel();
            listener.Stop();

            if (listenerTask != null)
            {
                try
                {
                    await listenerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (SocketException)
                {
                }
            }

            listenerCancellation?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _httpClient.Dispose();
        }

        private static TcpListener CreateListener()
        {
            try
            {
                var preferredListener = new TcpListener(IPAddress.Any, 8080);
                preferredListener.Start();
                return preferredListener;
            }
            catch (SocketException)
            {
                var fallbackListener = new TcpListener(IPAddress.Any, 0);
                fallbackListener.Start();
                return fallbackListener;
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using (client)
                using (NetworkStream outputStream = client.GetStream())
                {
                    string requestLine = await ReadRequestLineAsync(outputStream, cancellationToken).ConfigureAwait(false);

                    if (!IsStreamRequest(requestLine, out bool isHeadRequest))
                    {
                        await outputStream.WriteAsync(NotFoundResponse, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await DrainHeadersAsync(outputStream, cancellationToken).ConfigureAwait(false);

                    PandoraTrack? currentTrack = _currentTrackProvider();
                    string? audioUrl = currentTrack?.GetPlayableAudioUrl();

                    if (string.IsNullOrWhiteSpace(audioUrl))
                    {
                        await outputStream.WriteAsync(NotFoundResponse, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Get, audioUrl);
                    using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        await outputStream.WriteAsync(ServerErrorResponse, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseHeadersAsync(outputStream, response.Content.Headers, currentTrack!, isHeadRequest, cancellationToken).ConfigureAwait(false);

                    if (isHeadRequest) return;

                    await using Stream inputStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await inputStream.CopyToAsync(outputStream, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[STREAM PROXY ERROR] {ex.Message}");
            }
        }

        private static async Task<string> ReadRequestLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new List<byte>(128);
            var singleByte = new byte[1];

            while (buffer.Count < 4096)
            {
                int read = await stream.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;

                buffer.Add(singleByte[0]);
                if (buffer.Count >= 2 && buffer[^2] == '\r' && buffer[^1] == '\n') break;
            }

            return Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r', '\n');
        }

        private static async Task DrainHeadersAsync(Stream stream, CancellationToken cancellationToken)
        {
            int matched = 0;
            var singleByte = new byte[1];
            byte[] terminator = [(byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n'];

            while (matched < terminator.Length)
            {
                int read = await stream.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
                if (read == 0) return;

                matched = singleByte[0] == terminator[matched]
                    ? matched + 1
                    : singleByte[0] == terminator[0] ? 1 : 0;
            }
        }

        private static bool IsStreamRequest(string requestLine, out bool isHeadRequest)
        {
            isHeadRequest = false;
            string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;

            isHeadRequest = string.Equals(parts[0], "HEAD", StringComparison.OrdinalIgnoreCase);
            if (!isHeadRequest && !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase)) return false;

            return parts[1].StartsWith("/cast.m4a", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WriteResponseHeadersAsync(Stream outputStream, HttpContentHeaders sourceHeaders, PandoraTrack track, bool isHeadRequest, CancellationToken cancellationToken)
        {
            var headers = new StringBuilder();
            headers.Append("HTTP/1.1 200 OK\r\n");
            headers.Append("Connection: close\r\n");
            headers.Append("Accept-Ranges: none\r\n");
            headers.Append("Cache-Control: no-store\r\n");
            headers.Append($"Content-Type: {GetContentType(sourceHeaders.ContentType, track)}\r\n");

            if (sourceHeaders.ContentLength.HasValue && !isHeadRequest)
            {
                headers.Append($"Content-Length: {sourceHeaders.ContentLength.Value}\r\n");
            }

            headers.Append("\r\n");
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
            await outputStream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        }

        private static string GetContentType(MediaTypeHeaderValue? sourceContentType, PandoraTrack track)
        {
            if (!string.IsNullOrWhiteSpace(sourceContentType?.MediaType)) return sourceContentType.MediaType;

            return track.GetPreferredContentType();
        }
    }
}
