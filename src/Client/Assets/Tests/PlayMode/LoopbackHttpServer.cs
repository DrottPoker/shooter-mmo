using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Mmorpg.Client.Tests.PlayMode
{
    // A minimal HTTP/1.1 server on a free port of 127.0.0.1 for transport tests. It answers
    // GET requests by path from fixed routes and needs no administrator rights. Dispose stops
    // it and closes every connection it still holds.
    internal sealed class LoopbackHttpServer : IDisposable
    {
        private const int ChunkSize = 64 * 1024;

        private readonly TcpListener listener;
        private readonly Thread acceptThread;
        private readonly ConcurrentDictionary<string, Route> routes = new ConcurrentDictionary<string, Route>(StringComparer.Ordinal);
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private long streamedBytes;

        public LoopbackHttpServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LoopbackHttpServer" };
            acceptThread.Start();
        }

        public int Port { get; }

        public string BaseUrl
        {
            get { return "http://127.0.0.1:" + Port; }
        }

        // The bytes a streamed route wrote before the client stopped reading.
        public long StreamedBytes
        {
            get { return Interlocked.Read(ref streamedBytes); }
        }

        public void Serve(string path, byte[] body)
        {
            routes[path] = new Route(RouteKind.Body, 200, body, 0);
        }

        public void ServeStatus(string path, int statusCode)
        {
            routes[path] = new Route(RouteKind.Body, statusCode, Encoding.UTF8.GetBytes("{\"code\":\"not_found\"}"), 0);
        }

        // Announces and streams this many bytes, so a client that stops reading is seen.
        public void ServeStream(string path, long length)
        {
            routes[path] = new Route(RouteKind.Stream, 200, null, length);
        }

        // Accepts the request and never answers.
        public void ServeSilence(string path)
        {
            routes[path] = new Route(RouteKind.Silence, 0, null, 0);
        }

        public void Dispose()
        {
            stopping.Cancel();
            listener.Stop();
            lock (clients)
            {
                foreach (var client in clients)
                {
                    client.Close();
                }

                clients.Clear();
            }

            acceptThread.Join(TimeSpan.FromSeconds(5));
        }

        private void AcceptLoop()
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException || exception is InvalidOperationException)
                {
                    return;
                }

                lock (clients)
                {
                    clients.Add(client);
                }

                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
        }

        private void Handle(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var path = ReadRequestPath(stream);
                if (path == null)
                {
                    return;
                }

                if (!routes.TryGetValue(path, out var route))
                {
                    route = new Route(RouteKind.Body, 404, Encoding.UTF8.GetBytes("{}"), 0);
                }

                switch (route.Kind)
                {
                    case RouteKind.Silence:
                        stopping.Token.WaitHandle.WaitOne();
                        return;
                    case RouteKind.Stream:
                        WriteHeaders(stream, 200, route.Length);
                        var chunk = new byte[ChunkSize];
                        for (long sent = 0; sent < route.Length && !stopping.IsCancellationRequested; sent += chunk.Length)
                        {
                            stream.Write(chunk, 0, chunk.Length);
                            Interlocked.Add(ref streamedBytes, chunk.Length);
                        }

                        break;
                    default:
                        WriteHeaders(stream, route.StatusCode, route.Body.LongLength);
                        stream.Write(route.Body, 0, route.Body.Length);
                        break;
                }

                stream.Flush();
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException || exception is ObjectDisposedException)
            {
                // The client went away, such as after it aborted a body above its limit.
            }
            finally
            {
                lock (clients)
                {
                    clients.Remove(client);
                }

                client.Close();
            }
        }

        private static string ReadRequestPath(NetworkStream stream)
        {
            var request = new StringBuilder();
            var buffer = new byte[1];
            while (request.Length < 16 * 1024)
            {
                if (stream.Read(buffer, 0, 1) != 1)
                {
                    return null;
                }

                request.Append((char)buffer[0]);
                if (request.Length >= 4 && request.ToString(request.Length - 4, 4) == "\r\n\r\n")
                {
                    var requestLine = request.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
                    return requestLine.Length >= 2 ? requestLine[1] : null;
                }
            }

            return null;
        }

        private static void WriteHeaders(Stream stream, int statusCode, long contentLength)
        {
            var headers = "HTTP/1.1 " + statusCode + " " + (statusCode == 200 ? "OK" : "Not Found") + "\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Length: " + contentLength + "\r\n"
                + "Connection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(bytes, 0, bytes.Length);
        }

        private enum RouteKind
        {
            Body,
            Stream,
            Silence
        }

        private sealed class Route
        {
            public Route(RouteKind kind, int statusCode, byte[] body, long length)
            {
                Kind = kind;
                StatusCode = statusCode;
                Body = body;
                Length = length;
            }

            public RouteKind Kind { get; }

            public int StatusCode { get; }

            public byte[] Body { get; }

            public long Length { get; }
        }
    }
}
