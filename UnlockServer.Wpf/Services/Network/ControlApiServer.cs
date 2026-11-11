using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnlockServer.Network
{
    public sealed class ControlApiState
    {
        public bool Locked { get; set; }
        public int Pending { get; set; }
        public string ConsoleSession { get; set; }
    }

    /// <summary>
    /// 局域网控制接口。只做三件事：验签、入队、回查；不直接触发锁屏/解锁，
    /// 因为 DoUnlock 最长阻塞约 20 秒且监控循环持锁，HTTP 线程不能代劳。
    /// </summary>
    public sealed class ControlApiServer : IDisposable
    {
        private const int MaxBodyBytes = 4096;
        private readonly ApiAuth _auth;
        private readonly NetworkCommandQueue _queue;
        private readonly Func<ControlApiState> _state;
        private readonly Func<bool> _lockAllowed;
        private readonly Func<bool> _unlockAllowed;

        private HttpListener _listener;
        private CancellationTokenSource _cancellation;
        private string _endpoint;

        public ControlApiServer(ApiAuth auth, NetworkCommandQueue queue, Func<ControlApiState> state,
            Func<bool> lockAllowed, Func<bool> unlockAllowed)
        {
            if (auth == null) throw new ArgumentNullException("auth");
            if (queue == null) throw new ArgumentNullException("queue");
            _auth = auth;
            _queue = queue;
            _state = state ?? (() => new ControlApiState());
            _lockAllowed = lockAllowed ?? (() => true);
            _unlockAllowed = unlockAllowed ?? (() => true);
        }

        public bool IsRunning => _listener != null && _listener.IsListening;
        public string Endpoint => _endpoint;
        public string LastError { get; private set; }

        public bool Start(string listenAddress, int port, out string error)
        {
            error = null;
            Stop();

            if (port <= 0 || port > 65535)
            {
                error = "端口不合法";
                return false;
            }

            var host = string.IsNullOrWhiteSpace(listenAddress) ||
                       listenAddress == "0.0.0.0" || listenAddress == "*" ? "+" : listenAddress;

            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://{host}:{port}/");
                listener.Start();
                _listener = listener;
                _endpoint = $"http://{(host == "+" ? "0.0.0.0" : host)}:{port}/";
                LastError = null;

                var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                var token = cancellation.Token;
                Task.Run(() => AcceptLoop(listener, token));
                LogHelper.WriteLine($"网络控制已监听 {_endpoint}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                error = ex.Message;
                Stop();
                LogHelper.WriteLine($"启动网络控制失败: {ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            try { _cancellation?.Cancel(); } catch { }
            _cancellation = null;
            var listener = _listener;
            _listener = null;
            _endpoint = null;
            if (listener != null)
            {
                try { listener.Close(); } catch { }
            }
        }

        private void AcceptLoop(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch
                {
                    if (token.IsCancellationRequested) return;
                    if (_listener == null || !_listener.IsListening) return;
                    Thread.Sleep(50);
                    continue;
                }

                var captured = context;
                Task.Run(() => Handle(captured));
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var path = request.Url == null ? "" : request.Url.AbsolutePath;
                var method = request.HttpMethod ?? "";

                if (string.Equals(path, "/v1/challenge", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        Write(context, 405, Error("method_not_allowed"));
                        return;
                    }
                    HandleChallenge(context);
                    return;
                }

                if (string.Equals(path, "/v1/state", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireAuth(context, method, path, "")) return;
                    Write(context, 200, BuildStateJson());
                    return;
                }

                if (string.Equals(path, "/v1/lock", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/v1/unlock", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        Write(context, 405, Error("method_not_allowed"));
                        return;
                    }
                    var body = ReadBody(request);
                    if (!RequireAuth(context, method, path, body)) return;
                    HandleCommand(context, path.EndsWith("unlock", StringComparison.Ordinal)
                        ? NetworkCommandKind.Unlock : NetworkCommandKind.Lock);
                    return;
                }

                if (path.StartsWith("/v1/requests/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!RequireAuth(context, method, path, "")) return;
                    HandleQuery(context, path.Substring("/v1/requests/".Length));
                    return;
                }

                Write(context, 404, Error("not_found"));
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"网络请求处理失败: {ex.Message}");
                try { Write(context, 500, Error("internal_error")); } catch { }
            }
        }

        private void HandleChallenge(HttpListenerContext context)
        {
            var clientId = context.Request.QueryString["client"];
            var challenge = _auth.IssueChallenge(clientId);
            if (challenge.Nonce == null)
            {
                var code = challenge.Error == "rate_limited" ? 429 : 400;
                Write(context, code, Error(challenge.Error ?? "rejected"));
                return;
            }
            Write(context, 200, "{\"nonce\":\"" + JsonEscape(challenge.Nonce) +
                               "\",\"expiresIn\":" + challenge.ExpiresIn + "}");
        }

        private bool RequireAuth(HttpListenerContext context, string method, string path, string body)
        {
            var clientId = context.Request.Headers["X-Client-Id"];
            var nonce = context.Request.Headers["X-Nonce"];
            var signature = context.Request.Headers["X-Signature"];
            var result = _auth.Verify(clientId, method, path, nonce, signature, body);
            if (result.Ok) return true;

            var code = result.Code == AuthCode.RateLimited ? 429 : 401;
            Write(context, code, Error(result.Message));
            LogHelper.WriteLine($"网络鉴权失败({result.Code}): {clientId ?? "(空)"} {path}");
            return false;
        }

        private void HandleCommand(HttpListenerContext context, NetworkCommandKind kind)
        {
            var allowed = kind == NetworkCommandKind.Lock ? _lockAllowed() : _unlockAllowed();
            if (!allowed)
            {
                Write(context, 403, Error(kind == NetworkCommandKind.Lock ? "lock_disabled" : "unlock_disabled"));
                return;
            }

            var clientId = context.Request.Headers["X-Client-Id"] ?? "";
            var requestId = Guid.NewGuid().ToString("N");
            string error;
            if (!_queue.TryEnqueue(requestId, kind, clientId, out error))
            {
                var code = error == "busy" ? 429 : 400;
                Write(context, code, Error(error));
                return;
            }

            LogHelper.WriteLine($"收到网络命令: {kind} 来自 {clientId}，请求 {requestId}");
            Write(context, 202, "{\"requestId\":\"" + requestId + "\",\"status\":\"pending\"}");
        }

        private void HandleQuery(HttpListenerContext context, string requestId)
        {
            var request = _queue.Find(requestId);
            if (request == null)
            {
                Write(context, 404, Error("unknown_request"));
                return;
            }

            var json = "{\"requestId\":\"" + JsonEscape(request.Id) +
                       "\",\"kind\":\"" + (request.Kind == NetworkCommandKind.Lock ? "lock" : "unlock") +
                       "\",\"status\":\"" + StateText(request.State) +
                       "\",\"reason\":\"" + JsonEscape(request.Reason ?? "") +
                       "\",\"createdAt\":\"" + request.CreatedUtc.ToString("o") +
                       "\",\"updatedAt\":\"" + request.UpdatedUtc.ToString("o") + "\"}";
            Write(context, 200, json);
        }

        private string BuildStateJson()
        {
            ControlApiState state;
            try { state = _state(); }
            catch { state = new ControlApiState(); }

            return "{\"locked\":" + (state.Locked ? "true" : "false") +
                   ",\"pending\":" + state.Pending +
                   ",\"consoleSession\":\"" + JsonEscape(state.ConsoleSession ?? "") + "\"" +
                   ",\"lockEnabled\":" + (_lockAllowed() ? "true" : "false") +
                   ",\"unlockEnabled\":" + (_unlockAllowed() ? "true" : "false") + "}";
        }

        private static string StateText(NetworkRequestState state)
        {
            switch (state)
            {
                case NetworkRequestState.Done: return "done";
                case NetworkRequestState.Rejected: return "rejected";
                default: return "pending";
            }
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            try
            {
                if (request.ContentLength64 <= 0) return "";
                using (var stream = request.InputStream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var buffer = new StringBuilder();
                    var chunk = new char[1024];
                    var total = 0;
                    int read;
                    while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        total += read;
                        if (total > MaxBodyBytes) break;
                        buffer.Append(chunk, 0, read);
                    }
                    return buffer.ToString();
                }
            }
            catch
            {
                return "";
            }
        }

        private static string Error(string message)
        {
            return "{\"error\":\"" + JsonEscape(message ?? "error") + "\"}";
        }

        private static void Write(HttpListenerContext context, int statusCode, string json)
        {
            var response = context.Response;
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            var bytes = Encoding.UTF8.GetBytes(json ?? "{}");
            response.ContentLength64 = bytes.Length;
            using (var stream = response.OutputStream)
                stream.Write(bytes, 0, bytes.Length);
        }

        private static string JsonEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length + 8);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public void Dispose() => Stop();
    }
}
