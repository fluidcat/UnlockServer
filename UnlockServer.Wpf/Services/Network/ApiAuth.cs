using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace UnlockServer.Network
{
    /// <summary>
    /// 客户端密钥来源。生产实现读取 DPAPI 保护的文件，测试可注入内存实现。
    /// </summary>
    public interface IClientKeyProvider
    {
        byte[] FindKey(string clientId);
    }

    public enum AuthCode
    {
        Ok = 0,
        Malformed,
        UnknownClient,
        BadSignature,
        ReplayedNonce,
        RateLimited
    }

    public sealed class AuthResult
    {
        public AuthCode Code { get; private set; }
        public string Message { get; private set; }
        public bool Ok => Code == AuthCode.Ok;

        private AuthResult(AuthCode code, string message)
        {
            Code = code;
            Message = message;
        }

        public static AuthResult Fail(AuthCode code, string message) => new AuthResult(code, message);
        public static readonly AuthResult Success = new AuthResult(AuthCode.Ok, null);
    }

    public sealed class ChallengeResult
    {
        public string Nonce { get; set; }
        public int ExpiresIn { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 局域网控制接口的鉴权：服务端签发一次性 nonce，客户端用 HMAC-SHA256 签名。
    /// nonce 由服务端签发而不是用客户端时间戳，避免两端时钟偏差造成误拒或放大重放窗口。
    /// </summary>
    public sealed class ApiAuth
    {
        public const int NonceTtlSeconds = 30;
        private const int MaxLiveNonces = 2048;
        private const int ChallengePerMinute = 20;
        private const int FailureLimit = 5;
        private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(5);

        private sealed class NonceEntry
        {
            public string ClientId;
            public DateTime ExpiresUtc;
        }

        private sealed class FailureEntry
        {
            public DateTime WindowStartUtc;
            public int Count;
            public DateTime BlockedUntilUtc;
            public DateTime LastIssuedUtc;
            public int IssuedCount;
        }

        private readonly Dictionary<string, NonceEntry> _nonces =
            new Dictionary<string, NonceEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, FailureEntry> _failures =
            new Dictionary<string, FailureEntry>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private readonly IClientKeyProvider _keys;
        private readonly Func<DateTime> _clock;

        public ApiAuth(IClientKeyProvider keys, Func<DateTime> clock = null)
        {
            if (keys == null) throw new ArgumentNullException("keys");
            _keys = keys;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// 签发一次性 nonce。返回的 Nonce 为 null 时 Error 说明原因。
        /// </summary>
        public ChallengeResult IssueChallenge(string clientId)
        {
            var result = new ChallengeResult { ExpiresIn = NonceTtlSeconds };
            if (string.IsNullOrEmpty(clientId) || clientId.Length > 64)
            {
                result.Error = "invalid_client";
                return result;
            }

            lock (_gate)
            {
                var now = _clock();
                Prune(now);
                var entry = GetFailureEntry(clientId);
                if (entry.BlockedUntilUtc > now)
                {
                    result.Error = "rate_limited";
                    return result;
                }

                if (entry.LastIssuedUtc == DateTime.MinValue || (now - entry.LastIssuedUtc).TotalMinutes >= 1)
                {
                    entry.IssuedCount = 0;
                    entry.LastIssuedUtc = now;
                }
                if (++entry.IssuedCount > ChallengePerMinute)
                {
                    result.Error = "rate_limited";
                    return result;
                }

                if (_nonces.Count >= MaxLiveNonces)
                {
                    result.Error = "busy";
                    return result;
                }

                var nonce = NewNonce();
                _nonces[nonce] = new NonceEntry { ClientId = clientId, ExpiresUtc = now.AddSeconds(NonceTtlSeconds) };
                result.Nonce = nonce;
                return result;
            }
        }

        /// <summary>
        /// 校验签名。nonce 在进入校验时即被消费，并发重放不可能成功。
        /// </summary>
        public AuthResult Verify(string clientId, string method, string path, string nonce, string signature, string body)
        {
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(method) ||
                string.IsNullOrEmpty(path) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(signature))
                return AuthResult.Fail(AuthCode.Malformed, "缺少鉴权头");
            if (clientId.Length > 64 || nonce.Length > 64)
                return AuthResult.Fail(AuthCode.Malformed, "鉴权头过长");

            var key = _keys.FindKey(clientId);
            if (key == null || key.Length == 0)
                return CountFailure(clientId, AuthCode.UnknownClient, "未知的 clientId");

            lock (_gate)
            {
                var now = _clock();
                Prune(now);
                var entry = GetFailureEntry(clientId);
                if (entry.BlockedUntilUtc > now)
                    return AuthResult.Fail(AuthCode.RateLimited, "失败次数过多，稍后再试");

                NonceEntry issued;
                if (!_nonces.TryGetValue(nonce, out issued) ||
                    !string.Equals(issued.ClientId, clientId, StringComparison.Ordinal) ||
                    issued.ExpiresUtc <= now)
                {
                    // 无论是否命中都移除，避免同一 nonce 被反复试探。
                    _nonces.Remove(nonce);
                    return AuthResult.Fail(AuthCode.ReplayedNonce, "nonce 无效或已使用");
                }
                _nonces.Remove(nonce);
            }

            var expected = ComputeSignature(key, clientId, method, path, nonce, body);
            if (!FixedTimeEquals(expected, signature))
                return CountFailure(clientId, AuthCode.BadSignature, "签名不匹配");

            return AuthResult.Success;
        }

        /// <summary>
        /// 命令行字符串 = clientId|METHOD|path|nonce|sha256(body)。客户端只需实现这一个函数。
        /// </summary>
        public static string ComputeSignature(byte[] key, string clientId, string method, string path, string nonce, string body)
        {
            var bodyHash = Sha256Hex(body ?? "");
            var canonical = (clientId ?? "").ToLowerInvariant() + "|" +
                            (method ?? "").ToUpperInvariant() + "|" +
                            (path ?? "") + "|" + (nonce ?? "") + "|" + bodyHash;
            using (var hmac = new HMACSHA256(key))
            {
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
            }
        }

        public static string DecodeBase64Body(string bodyBase64)
        {
            if (string.IsNullOrEmpty(bodyBase64)) return "";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(bodyBase64)); }
            catch { return null; }
        }

        private AuthResult CountFailure(string clientId, AuthCode code, string message)
        {
            lock (_gate)
            {
                var now = _clock();
                var entry = GetFailureEntry(clientId);
                if ((now - entry.WindowStartUtc).TotalSeconds > FailureWindow.TotalSeconds)
                {
                    entry.WindowStartUtc = now;
                    entry.Count = 0;
                }
                entry.Count++;
                if (entry.Count >= FailureLimit)
                {
                    entry.BlockedUntilUtc = now.Add(BlockDuration);
                    entry.Count = 0;
                    entry.WindowStartUtc = now;
                }
            }
            return AuthResult.Fail(code, message);
        }

        private FailureEntry GetFailureEntry(string clientId)
        {
            FailureEntry entry;
            if (!_failures.TryGetValue(clientId, out entry))
            {
                entry = new FailureEntry { WindowStartUtc = _clock() };
                _failures[clientId] = entry;
            }
            return entry;
        }

        private void Prune(DateTime now)
        {
            if (_nonces.Count == 0) return;
            var expired = new List<string>();
            foreach (var pair in _nonces)
                if (pair.Value.ExpiresUtc <= now) expired.Add(pair.Key);
            foreach (var key in expired) _nonces.Remove(key);
        }

        private static string NewNonce()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));
        }

        private static string ToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static bool FixedTimeEquals(string expected, string actual)
        {
            if (expected == null || actual == null) return false;
            if (expected.Length != actual.Length) return false;
            var diff = 0;
            for (var i = 0; i < expected.Length; i++)
                diff |= expected[i] ^ actual[i];
            return diff == 0;
        }
    }
}
