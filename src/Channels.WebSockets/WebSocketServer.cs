﻿using Channels;
using Channels.Networking.Libuv;
using Channels.Networking.Sockets;
using Channels.Text.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Channels.WebSockets
{
    public abstract class WebSocketServer : IDisposable
    {
        private UvTcpListener uvListener;
        private UvThread uvThread;
        private SocketListener socketListener;

        private IPAddress ip;
        private int port;
        public int Port => port;
        public IPAddress IP => ip;

        public bool BufferFragments { get; set; }
        public bool AllowClientsMissingConnectionHeaders { get; set; } = true; // stoopid browsers

        public WebSocketServer()
        {
            if (!BitConverter.IsLittleEndian)
            {
                throw new NotSupportedException("This code has not been tested on big-endian architectures");
            }
        }
        public void Dispose() => Dispose(true);
        ~WebSocketServer() { Dispose(false); }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                GC.SuppressFinalize(this);
                Stop();
            }
        }
        public int ConnectionCount => connections.Count;

        public Task<int> CloseAllAsync(string message = null, Func<WebSocketConnection, bool> predicate = null)
        {
            if (connections.IsEmpty) return TaskResult.Zero; // avoid any processing
            return BroadcastAsync(WebSocketsFrame.OpCodes.Close, MessageWriter.Create(message, true), predicate);
        }
        public Task<int> BroadcastAsync(string message, Func<WebSocketConnection, bool> predicate = null)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (connections.IsEmpty) return TaskResult.Zero; // avoid any processing
            return BroadcastAsync(WebSocketsFrame.OpCodes.Text, MessageWriter.Create(message, true), predicate);
        }
        public Task<int> PingAsync(string message = null, Func<WebSocketConnection, bool> predicate = null)
        {
            if (connections.IsEmpty) return TaskResult.Zero; // avoid any processing
            return BroadcastAsync(WebSocketsFrame.OpCodes.Ping, MessageWriter.Create(message, true), predicate);
        }
        public Task<int> BroadcastAsync(byte[] message, Func<WebSocketConnection, bool> predicate = null)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (connections.IsEmpty) return TaskResult.Zero; // avoid any processing
            return BroadcastAsync(WebSocketsFrame.OpCodes.Binary, MessageWriter.Create(message), predicate);
        }
        private async Task<int> BroadcastAsync<T>(WebSocketsFrame.OpCodes opCode, T message, Func<WebSocketConnection, bool> predicate) where T : struct, IMessageWriter
        {
            int count = 0;
            foreach (var pair in connections)
            {
                var conn = pair.Key;
                if (!conn.IsClosed && (predicate == null || predicate(conn)))
                {
                    try
                    {
                        await conn.SendAsync<T>(opCode, ref message);
                        count++;
                    }
                    catch { } // not really all that bothered - they just won't get counted
                }
            }
            return count;
        }
        // todo: pick a more appropriate container for connection management; this insane choice is just arbitrary
        private ConcurrentDictionary<WebSocketConnection, WebSocketConnection> connections = new ConcurrentDictionary<WebSocketConnection, WebSocketConnection>();

        public void StartLibuv(IPAddress ip, int port) {
            if (uvListener == null && socketListener == null) {
                uvThread = new UvThread();
                uvListener = new UvTcpListener(uvThread, new IPEndPoint(ip, port));
                uvListener.OnConnection(OnConnection);
                uvListener.Start();
            }
        }
        public void StartManagedSockets(IPAddress ip, int port, ChannelFactory channelFactory = null) {
            if (uvListener == null && socketListener == null) {
                socketListener = new SocketListener(channelFactory);
                socketListener.OnConnection(OnConnection);
                socketListener.Start(new IPEndPoint(ip, port));
            }
        }

        private async Task OnConnection(IChannel connection)
        {
            using (connection)
            {
                WebSocketConnection socket = null;
                try
                {
                    WriteStatus(ConnectionType.Server, "Connected");

                    WriteStatus(ConnectionType.Server, "Parsing http request...");
                    var request = await ParseHttpRequest(connection.Input);
                    try
                    {
                        WriteStatus(ConnectionType.Server, "Identifying protocol...");
                        socket = GetProtocol(connection, ref request);
                        WriteStatus(ConnectionType.Server, $"Protocol: {WebSocketProtocol.Name}");
                        WriteStatus(ConnectionType.Server, "Authenticating...");
                        if (!await OnAuthenticateAsync(socket, ref request.Headers)) throw new InvalidOperationException("Authentication refused");
                        WriteStatus(ConnectionType.Server, "Completing handshake...");
                        await WebSocketProtocol.CompleteServerHandshakeAsync(ref request, socket);
                    }
                    finally
                    {
                        request.Dispose(); // can't use "ref request" or "ref headers" otherwise
                    }
                    WriteStatus(ConnectionType.Server, "Handshake complete hook...");
                    await OnHandshakeCompleteAsync(socket);

                    connections.TryAdd(socket, socket);
                    WriteStatus(ConnectionType.Server, "Processing incoming frames...");
                    await socket.ProcessIncomingFramesAsync(this);
                    WriteStatus(ConnectionType.Server, "Exiting...");
                    await socket.CloseAsync();
                }
                catch (Exception ex)
                {// meh, bye bye broken connection
                    try { socket?.Dispose(); } catch { }
                    WriteStatus(ConnectionType.Server, ex.StackTrace);
                    WriteStatus(ConnectionType.Server, ex.GetType().Name);
                    WriteStatus(ConnectionType.Server, ex.Message);
                }
                finally
                {
                    WebSocketConnection tmp;
                    if (socket != null) connections.TryRemove(socket, out tmp);
                    try { connection.Output.Complete(); } catch { }
                    try { connection.Input.Complete(); } catch { }
                }
            }
        }

        [Conditional("LOGGING")]
        internal static void WriteStatus(ConnectionType type, string message)
        {
#if LOGGING
            Console.WriteLine($"[{type}:{Environment.CurrentManagedThreadId}]: {message}");
#endif
        }


        


        

        
        protected internal virtual Task OnBinaryAsync(WebSocketConnection connection, ref Message message) => TaskResult.True;
        protected internal virtual Task OnTextAsync(WebSocketConnection connection, ref Message message) => TaskResult.True;

        static readonly char[] Comma = { ',' };

        protected virtual Task<bool> OnAuthenticateAsync(WebSocketConnection connection, ref HttpRequestHeaders headers) => TaskResult.True;
        protected virtual Task OnHandshakeCompleteAsync(WebSocketConnection connection) => TaskResult.True;

        private WebSocketConnection GetProtocol(IChannel connection, ref HttpRequest request)
        {
            var headers = request.Headers;
            string host = headers.GetAsciiString("Host");
            if (string.IsNullOrEmpty(host))
            {
                //4.   The request MUST contain a |Host| header field whose value
                //contains /host/ plus optionally ":" followed by /port/ (when not
                //using the default port).
                throw new InvalidOperationException("host required");
            }

            bool looksGoodEnough = false;
            // mozilla sends "keep-alive, Upgrade"; let's make it more forgiving
            var connectionParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (headers.ContainsKey("Connection"))
            {
                // so for mozilla, this will be the set {"keep-alive", "Upgrade"}
                var parts = headers.GetAsciiString("Connection").Split(Comma);
                foreach (var part in parts) connectionParts.Add(part.Trim());
            }
            if (connectionParts.Contains("Upgrade") && IsCaseInsensitiveAsciiMatch(headers.GetRaw("Upgrade"), "websocket"))
            {
                //5.   The request MUST contain an |Upgrade| header field whose value
                //MUST include the "websocket" keyword.
                //6.   The request MUST contain a |Connection| header field whose value
                //MUST include the "Upgrade" token.
                looksGoodEnough = true;
            }

            if (!looksGoodEnough && AllowClientsMissingConnectionHeaders)
            {
                if ((headers.ContainsKey("Sec-WebSocket-Version") && headers.ContainsKey("Sec-WebSocket-Key"))
                    || (headers.ContainsKey("Sec-WebSocket-Key1") && headers.ContainsKey("Sec-WebSocket-Key2")))
                {
                    looksGoodEnough = true;
                }
            }

            if (looksGoodEnough)
            {
                //9.   The request MUST include a header field with the name
                //|Sec-WebSocket-Version|.  The value of this header field MUST be

                if (!headers.ContainsKey("Sec-WebSocket-Version"))
                {
                    throw new NotSupportedException();
                }
                else
                {
                    var version = headers.GetRaw("Sec-WebSocket-Version").GetUInt32();
                    switch (version)
                    {

                        case 4:
                        case 5:
                        case 6:
                        case 7:
                        case 8: // these are all early drafts
                        case 13: // this is later drafts and RFC6455
                            break; // looks ok
                        default:
                            // should issues a 400 "upgrade required" and specify Sec-WebSocket-Version - see 4.4
                            throw new InvalidOperationException(string.Format("Sec-WebSocket-Version {0} is not supported", version));
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("Request was not a web-socket upgrade request");
            }
            //The "Request-URI" of the GET method [RFC2616] is used to identify the
            //endpoint of the WebSocket connection, both to allow multiple domains
            //to be served from one IP address and to allow multiple WebSocket
            //endpoints to be served by a single server.
            var socket = new WebSocketConnection(connection, ConnectionType.Server);
            socket.Host = host;
            socket.BufferFragments = BufferFragments;
            // Some early drafts used the latter, so we'll allow it as a fallback
            // in particular, two drafts of version "8" used (separately) **both**,
            // so we can't rely on the version for this (hybi-10 vs hybi-11).
            // To make it even worse, hybi-00 used Origin, so it is all over the place!
            socket.Origin = headers.GetAsciiString("Origin") ?? headers.GetAsciiString("Sec-WebSocket-Origin");
            socket.Protocol = headers.GetAsciiString("Sec-WebSocket-Protocol");
            socket.RequestLine = request.Path.Buffer.GetAsciiString();
            return socket;
        }
        
        

        
        
        private enum ParsingState
        {
            StartLine,
            Headers
        }
        internal static async Task<HttpResponse> ParseHttpResponse(IReadableChannel _input)
        {
            return new HttpResponse(await ParseHttpRequest(_input));
        }
        internal static async Task<HttpRequest> ParseHttpRequest(IReadableChannel _input)
        {
            PreservedBuffer Method = default(PreservedBuffer), Path = default(PreservedBuffer), HttpVersion = default(PreservedBuffer);
            Dictionary<string, PreservedBuffer> Headers = new Dictionary<string, PreservedBuffer>();
            try
            {
                ParsingState _state = ParsingState.StartLine;
                bool needMoreData = true;
                while (needMoreData)
                {
                    var readResult = await _input.ReadAsync();
                    var buffer = readResult.Buffer;
                    var consumed = buffer.Start;
                    needMoreData = true;

                    try
                    {
                        if (buffer.IsEmpty && readResult.IsCompleted)
                        {
                            throw new EndOfStreamException();
                        }

                        if (_state == ParsingState.StartLine)
                        {
                            // Find \n
                            ReadCursor delim;
                            ReadableBuffer startLine;
                            if (!buffer.TrySliceTo((byte)'\r', (byte)'\n', out startLine, out delim))
                            {
                                continue;
                            }


                            // Move the buffer to the rest
                            buffer = buffer.Slice(delim).Slice(2);

                            ReadableBuffer method;
                            if (!startLine.TrySliceTo((byte)' ', out method, out delim))
                            {
                                throw new Exception();
                            }

                            Method = method.Preserve();

                            // Skip ' '
                            startLine = startLine.Slice(delim).Slice(1);

                            ReadableBuffer path;
                            if (!startLine.TrySliceTo((byte)' ', out path, out delim))
                            {
                                throw new Exception();
                            }

                            Path = path.Preserve();

                            // Skip ' '
                            startLine = startLine.Slice(delim).Slice(1);

                            var httpVersion = startLine;
                            if (httpVersion.IsEmpty)
                            {
                                throw new Exception();
                            }

                            HttpVersion = httpVersion.Preserve();

                            _state = ParsingState.Headers;
                            consumed = buffer.Start;
                        }

                        // Parse headers
                        // key: value\r\n

                        while (!buffer.IsEmpty)
                        {
                            var ch = buffer.Peek();

                            if (ch == -1)
                            {
                                break;
                            }

                            if (ch == '\r')
                            {
                                // Check for final CRLF.
                                buffer = buffer.Slice(1);
                                ch = buffer.Peek();
                                buffer = buffer.Slice(1);

                                if (ch == -1)
                                {
                                    break;
                                }
                                else if (ch == '\n')
                                {
                                    consumed = buffer.Start;
                                    needMoreData = false;
                                    break;
                                }

                                // Headers don't end in CRLF line.
                                throw new Exception();
                            }

                            var headerName = default(ReadableBuffer);
                            var headerValue = default(ReadableBuffer);

                            // End of the header
                            // \n
                            ReadCursor delim;
                            ReadableBuffer headerPair;
                            if (!buffer.TrySliceTo((byte)'\n', out headerPair, out delim))
                            {
                                break;
                            }

                            buffer = buffer.Slice(delim).Slice(1);

                            // :
                            if (!headerPair.TrySliceTo((byte)':', out headerName, out delim))
                            {
                                throw new Exception();
                            }

                            headerName = headerName.TrimStart();
                            headerPair = headerPair.Slice(delim).Slice(1);

                            // \r
                            if (!headerPair.TrySliceTo((byte)'\r', out headerValue, out delim))
                            {
                                // Bad request
                                throw new Exception();
                            }

                            headerValue = headerValue.TrimStart();
                            Headers[ToHeaderKey(ref headerName)] = headerValue.Preserve();

                            // Move the consumed
                            consumed = buffer.Start;
                        }
                    }
                    finally
                    {
                        _input.Advance(consumed);
                    }
                }
                var result = new HttpRequest(Method, Path, HttpVersion, Headers);
                Method = Path = HttpVersion = default(PreservedBuffer);
                Headers = null;
                return result;
            }
            finally
            {
                Method.Dispose();
                Path.Dispose();
                HttpVersion.Dispose();
                if (Headers != null)
                {
                    foreach (var pair in Headers)
                        pair.Value.Dispose();
                }
            }
        }

        static readonly string[] CommonHeaders = new string[]
        {
            "Accept",
            "Accept-Encoding",
            "Accept-Language",
            "Cache-Control",
            "Connection",
            "Cookie",
            "Host",
            "Origin",
            "Pragma",
            "Sec-WebSocket-Extensions",
            "Sec-WebSocket-Key",
            "Sec-WebSocket-Key1",
            "Sec-WebSocket-Key2",
            "Sec-WebSocket-Accept",
            "Sec-WebSocket-Origin",
            "Sec-WebSocket-Protocol",
            "Sec-WebSocket-Version",
            "Upgrade",
            "Upgrade-Insecure-Requests",
            "User-Agent"
        }, CommonHeadersLowerCaseInvariant = CommonHeaders.Select(s => s.ToLowerInvariant()).ToArray();

        private static string ToHeaderKey(ref ReadableBuffer headerName)
        {
            var lowerCaseHeaders = CommonHeadersLowerCaseInvariant;
            for (int i = 0; i < lowerCaseHeaders.Length; i++)
            {
                if (IsCaseInsensitiveAsciiMatch(headerName, lowerCaseHeaders[i])) return CommonHeaders[i];
            }

            return headerName.GetAsciiString();
        }

        private static bool IsCaseInsensitiveAsciiMatch(ReadableBuffer bufferUnknownCase, string valueLowerCase)
        {
            if (bufferUnknownCase.Length != valueLowerCase.Length) return false;
            int charIndex = 0;
            foreach (var memory in bufferUnknownCase)
            {
                var span = memory.Span;
                for (int spanIndex = 0; spanIndex < span.Length; spanIndex++)
                {
                    char x = (char)span[spanIndex], y = valueLowerCase[charIndex++];
                    if (x != y && char.ToLowerInvariant(x) != y) return false;
                }
            }
            return true;
        }

        public void Stop()
        {
            uvListener?.Stop();
            uvThread?.Dispose();
            uvListener = null;
            uvThread = null;

            socketListener?.Stop();
            socketListener?.Dispose();
            socketListener = null;
        }
    }
}
