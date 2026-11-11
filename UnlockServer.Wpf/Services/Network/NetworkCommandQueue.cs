using System;
using System.Collections.Generic;

namespace UnlockServer.Network
{
    public enum NetworkCommandKind
    {
        Lock = 0,
        Unlock = 1
    }

    public enum NetworkRequestState
    {
        Pending = 0,
        Done = 1,
        Rejected = 2
    }

    public sealed class NetworkRequest
    {
        public string Id { get; internal set; }
        public NetworkCommandKind Kind { get; internal set; }
        public string ClientId { get; internal set; }
        public DateTime CreatedUtc { get; internal set; }
        public NetworkRequestState State { get; internal set; }
        public string Reason { get; internal set; }
        public DateTime UpdatedUtc { get; internal set; }
    }

    /// <summary>
    /// 网络命令队列：HTTP 处理器只入队，监控循环消费。
    /// 有界、按 requestId 去重、结果保留一段时间供客户端回查。
    /// </summary>
    public sealed class NetworkCommandQueue
    {
        public const int DefaultCapacity = 16;
        public const int DefaultRetentionMinutes = 10;

        private readonly int _capacity;
        private readonly TimeSpan _retention;
        private readonly Func<DateTime> _clock;
        private readonly object _gate = new object();
        private readonly List<NetworkRequest> _pending = new List<NetworkRequest>();
        private readonly Dictionary<string, NetworkRequest> _history =
            new Dictionary<string, NetworkRequest>(StringComparer.Ordinal);

        public NetworkCommandQueue(int capacity = DefaultCapacity,
            int retentionMinutes = DefaultRetentionMinutes,
            Func<DateTime> clock = null)
        {
            _capacity = capacity > 0 ? capacity : DefaultCapacity;
            _retention = TimeSpan.FromMinutes(retentionMinutes > 0 ? retentionMinutes : DefaultRetentionMinutes);
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int PendingCount
        {
            get { lock (_gate) return _pending.Count; }
        }

        /// <summary>
        /// 入队。requestId 重复、队列已满或参数非法时返回 false 并给出原因。
        /// </summary>
        public bool TryEnqueue(string requestId, NetworkCommandKind kind, string clientId, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(requestId) || requestId.Length > 64)
            {
                error = "invalid_request";
                return false;
            }

            lock (_gate)
            {
                Purge();
                if (_history.ContainsKey(requestId))
                {
                    error = "duplicate";
                    return false;
                }
                if (_pending.Count >= _capacity)
                {
                    error = "busy";
                    return false;
                }

                var now = _clock();
                var request = new NetworkRequest
                {
                    Id = requestId,
                    Kind = kind,
                    ClientId = clientId ?? "",
                    CreatedUtc = now,
                    State = NetworkRequestState.Pending,
                    UpdatedUtc = now
                };
                _pending.Add(request);
                _history[requestId] = request;
                return true;
            }
        }

        public bool TryDequeue(out NetworkRequest request)
        {
            lock (_gate)
            {
                Purge();
                if (_pending.Count == 0)
                {
                    request = null;
                    return false;
                }
                request = _pending[0];
                _pending.RemoveAt(0);
                return true;
            }
        }

        /// <summary>
        /// 写回执行结果。只有 Pending 能迁移；重复回报返回 false。
        /// </summary>
        public bool Complete(string requestId, NetworkRequestState state, string reason)
        {
            if (state != NetworkRequestState.Done && state != NetworkRequestState.Rejected)
                throw new ArgumentOutOfRangeException("state", "只能迁移到 Done 或 Rejected");

            lock (_gate)
            {
                NetworkRequest request;
                if (!_history.TryGetValue(requestId, out request)) return false;
                if (request.State != NetworkRequestState.Pending) return false;
                request.State = state;
                request.Reason = reason;
                request.UpdatedUtc = _clock();
                return true;
            }
        }

        /// <summary>
        /// 回查结果；过期或未知返回 null。
        /// </summary>
        public NetworkRequest Find(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return null;
            lock (_gate)
            {
                Purge();
                NetworkRequest request;
                return _history.TryGetValue(requestId, out request) ? request : null;
            }
        }

        private void Purge()
        {
            var now = _clock();
            if (_history.Count == 0) return;
            var expired = new List<string>();
            foreach (var pair in _history)
                if ((now - pair.Value.UpdatedUtc) > _retention) expired.Add(pair.Key);
            foreach (var key in expired)
            {
                _history.Remove(key);
                _pending.RemoveAll(r => string.Equals(r.Id, key, StringComparison.Ordinal));
            }
        }
    }
}
