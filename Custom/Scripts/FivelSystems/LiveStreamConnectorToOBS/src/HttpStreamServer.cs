using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FivelSystems.LiveStreamConnectorToOBS
{
    /// <summary>
    /// MJPEG-over-HTTP server on a raw socket. Binds directly rather than via
    /// HttpListener, which on Windows requires a URL ACL or elevation for any
    /// non-loopback prefix. Must not reference a Unity API: runs off-thread.
    /// </summary>
    public class HttpStreamServer
    {
        private const string BOUNDARY = "mjpeg_boundary";
        private const int MAX_CLIENTS = 4;
        private const int REQUEST_LIMIT = 8 * 1024;
        private const int RECEIVE_TIMEOUT_MS = 5000;
        private const int SEND_TIMEOUT_MS = 10000;

        /// <summary>Kept below one frame so a slow link drops frames instead of queueing them.</summary>
        private const int SEND_BUFFER_BYTES = 64 * 1024;

        private readonly int _port;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _bindAll;
        private readonly string _accessKey;
        private int _jpegQuality;

        private TcpListener _listener;
        private Thread _acceptThread;
        private readonly object _frameLock = new object();
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private byte[] _currentFrame;
        private int _frameId;
        private volatile bool _stop;
        private int _clientCount;

        private static readonly byte[] CRLF = new byte[] { 13, 10 };

        public int JpegQuality
        {
            get { return _jpegQuality; }
            set { _jpegQuality = value < 10 ? 10 : (value > 100 ? 100 : value); }
        }

        public int ClientCount { get { return _clientCount; } }

        public HttpStreamServer(int port, int width, int height, int jpegQuality, bool bindAll, string accessKey)
        {
            _port = port;
            _width = width;
            _height = height;
            _bindAll = bindAll;
            _accessKey = accessKey == null ? "" : accessKey.Trim();
            _jpegQuality = jpegQuality;
        }

        public void Start()
        {
            _stop = false;
            IPAddress bind = _bindAll ? IPAddress.Any : IPAddress.Loopback;
            _listener = new TcpListener(bind, _port);

            // Rebuilds are frequent; don't fail on a socket still in TIME_WAIT.
            try { _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); }
            catch { }

            _listener.Start();
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Start();
        }

        public void Stop()
        {
            _stop = true;

            lock (_frameLock) Monitor.PulseAll(_frameLock);

            // A thread parked in Write() won't see the pulse; close under it.
            lock (_clients)
            {
                for (int i = 0; i < _clients.Count; i++)
                {
                    try { _clients[i].Close(); } catch { }
                }
                _clients.Clear();
            }

            try { if (_listener != null) _listener.Stop(); } catch { }
            if (_acceptThread != null && _acceptThread.IsAlive)
            {
                try { _acceptThread.Join(500); } catch { }
            }
            _listener = null;
            _acceptThread = null;
        }

        /// <summary>Publishes a frame and wakes every streaming thread.</summary>
        public void SubmitFrame(byte[] jpeg)
        {
            lock (_frameLock)
            {
                _currentFrame = jpeg;
                _frameId++;
                Monitor.PulseAll(_frameLock);
            }
        }

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    return;
                }
                if (client == null) continue;

                ThreadPool.QueueUserWorkItem(state => HandleClient((TcpClient)state), client);
            }
        }

        private void HandleClient(TcpClient client)
        {
            bool counted = false;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = RECEIVE_TIMEOUT_MS;
                client.SendTimeout = SEND_TIMEOUT_MS;
                try { client.SendBufferSize = SEND_BUFFER_BYTES; } catch { }

                lock (_clients) _clients.Add(client);

                NetworkStream ns = client.GetStream();
                string head = ReadRequestHead(ns);
                if (head == null) return;

                string path, query;
                if (!ParseRequestLine(head, out path, out query))
                {
                    WriteText(ns, 400, "Bad Request", "text/plain", "Bad Request");
                    return;
                }

                if (!IsAuthorized(query))
                {
                    WriteText(ns, 403, "Forbidden", "text/plain",
                              "Access key required. Append ?key=... to the URL.");
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    ServeIndex(ns);
                }
                else if (path == "/stream")
                {
                    if (Interlocked.Increment(ref _clientCount) > MAX_CLIENTS)
                    {
                        Interlocked.Decrement(ref _clientCount);
                        WriteText(ns, 503, "Service Unavailable", "text/plain", "Too many clients");
                        return;
                    }
                    counted = true;
                    ServeMjpeg(ns);
                }
                else
                {
                    WriteText(ns, 404, "Not Found", "text/plain", "Not Found");
                }
            }
            catch
            {
                // Malformed request or dropped client.
            }
            finally
            {
                if (counted) Interlocked.Decrement(ref _clientCount);
                lock (_clients) _clients.Remove(client);
                try { client.Close(); } catch { }
            }
        }

        private bool IsAuthorized(string query)
        {
            if (_accessKey.Length == 0) return true;
            return GetQueryValue(query, "key") == _accessKey;
        }

        /// <summary>Reads up to the header terminator, or null if nothing usable arrived.</summary>
        private static string ReadRequestHead(NetworkStream ns)
        {
            byte[] buf = new byte[REQUEST_LIMIT];
            int used = 0;
            while (used < REQUEST_LIMIT)
            {
                int n;
                try { n = ns.Read(buf, used, REQUEST_LIMIT - used); }
                catch { break; }
                if (n <= 0) break;
                used += n;
                for (int i = 3; i < used; i++)
                {
                    if (buf[i] == 10 && buf[i - 1] == 13 && buf[i - 2] == 10 && buf[i - 3] == 13)
                        return Encoding.ASCII.GetString(buf, 0, used);
                }
            }
            return used > 0 ? Encoding.ASCII.GetString(buf, 0, used) : null;
        }

        /// <summary>Splits the request line into path and query string.</summary>
        private static bool ParseRequestLine(string head, out string path, out string query)
        {
            path = "/";
            query = "";

            int lineEnd = head.IndexOf('\r');
            if (lineEnd < 0) lineEnd = head.Length;
            string line = head.Substring(0, lineEnd);

            string[] parts = line.Split(' ');
            if (parts.Length < 2) return false;
            if (parts[0] != "GET" && parts[0] != "HEAD") return false;

            string target = parts[1];
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                query = target.Substring(q + 1);
                target = target.Substring(0, q);
            }
            path = target.Length == 0 ? "/" : target;
            return true;
        }

        private static string GetQueryValue(string query, string name)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                if (eq <= 0) continue;
                if (pairs[i].Substring(0, eq) == name)
                    return Uri.UnescapeDataString(pairs[i].Substring(eq + 1));
            }
            return null;
        }

        private static void WriteText(NetworkStream ns, int status, string reason, string contentType, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body == null ? "" : body);
            string head = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                          "Content-Type: " + contentType + "; charset=utf-8\r\n" +
                          "Content-Length: " + payload.Length + "\r\n" +
                          "Access-Control-Allow-Origin: *\r\n" +
                          "Connection: close\r\n" +
                          "\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            ns.Write(headBytes, 0, headBytes.Length);
            if (payload.Length > 0) ns.Write(payload, 0, payload.Length);
            ns.Flush();
        }

        private void ServeIndex(NetworkStream ns)
        {
            string suffix = _accessKey.Length > 0 ? "?key=" + Uri.EscapeDataString(_accessKey) : "";
            string html =
                "<!DOCTYPE html><html><head><title>VaM Camera Stream</title>" +
                "<meta name='viewport' content='width=device-width,initial-scale=1'></head>" +
                "<body style='margin:0;background:#000;display:flex;align-items:center;justify-content:center;height:100vh;'>" +
                "<img src='/stream" + suffix + "' width='" + _width + "' height='" + _height + "' " +
                "style='max-width:100%;max-height:100%;width:auto;height:auto;'>" +
                "</body></html>";
            WriteText(ns, 200, "OK", "text/html", html);
        }

        /// <summary>
        /// Streams frames until the client goes away. Waits on the frame id so
        /// nothing is sent twice, and skips to the newest frame when the link
        /// is slower than the capture rate.
        /// </summary>
        private void ServeMjpeg(NetworkStream ns)
        {
            string head = "HTTP/1.1 200 OK\r\n" +
                          "Content-Type: multipart/x-mixed-replace; boundary=" + BOUNDARY + "\r\n" +
                          "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                          "Pragma: no-cache\r\n" +
                          "Access-Control-Allow-Origin: *\r\n" +
                          "Connection: close\r\n" +
                          "\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            ns.Write(headBytes, 0, headBytes.Length);
            ns.Flush();

            try
            {
                int lastId = 0;
                while (!_stop)
                {
                    byte[] frame;
                    lock (_frameLock)
                    {
                        while (!_stop && _frameId == lastId)
                            Monitor.Wait(_frameLock, 250);

                        if (_stop) break;
                        frame = _currentFrame;
                        lastId = _frameId;
                    }
                    if (frame == null) continue;

                    string part = "--" + BOUNDARY + "\r\n" +
                                  "Content-Type: image/jpeg\r\n" +
                                  "Content-Length: " + frame.Length + "\r\n\r\n";
                    byte[] partBytes = Encoding.ASCII.GetBytes(part);
                    ns.Write(partBytes, 0, partBytes.Length);
                    ns.Write(frame, 0, frame.Length);
                    ns.Write(CRLF, 0, CRLF.Length);
                    ns.Flush();
                }
            }
            catch
            {
                // Client disconnected or send timed out.
            }
        }
    }
}
