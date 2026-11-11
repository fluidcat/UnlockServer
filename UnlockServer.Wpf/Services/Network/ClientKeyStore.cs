using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UnlockServer.Network
{
    public sealed class NetworkClientInfo
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public DateTime CreatedUtc { get; internal set; }
        public string Fingerprint { get; internal set; }
    }

    public sealed class ClientKeyCreated
    {
        public string ClientId { get; internal set; }
        public string SecretBase64 { get; internal set; }
        public string Fingerprint { get; internal set; }
    }

    /// <summary>
    /// 客户端密钥库。密钥用 DPAPI(LocalMachine) 保护后整体落盘，不写入 INI，
    /// 避免和设置文件里那套固定密钥的 AES 包装混在一起。
    /// </summary>
    public sealed class ClientKeyStore : IClientKeyProvider
    {
        private sealed class Entry
        {
            public string Id;
            public string Name;
            public byte[] Key;
            public DateTime CreatedUtc;
        }

        private readonly string _path;
        private readonly object _gate = new object();
        private List<Entry> _entries;

        public ClientKeyStore(string path = null)
        {
            _path = path ?? DefaultPath;
        }

        public static string DefaultPath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "UnlockServer", "network");
                return Path.Combine(dir, "clients.dat");
            }
        }

        public IList<NetworkClientInfo> List()
        {
            lock (_gate)
            {
                return Load().Select(e => new NetworkClientInfo
                {
                    Id = e.Id,
                    Name = e.Name,
                    CreatedUtc = e.CreatedUtc,
                    Fingerprint = Fingerprint(e.Key)
                }).ToList();
            }
        }

        /// <summary>
        /// 新增客户端，返回新生成的密钥（Base64）。密钥只在这一刻可见，之后只显示指纹。
        /// </summary>
        public ClientKeyCreated Add(string name)
        {
            lock (_gate)
            {
                var entries = Load();
                var key = new byte[32];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(key);

                var id = "c" + Guid.NewGuid().ToString("N").Substring(0, 12);
                entries.Add(new Entry
                {
                    Id = id,
                    Name = Clean(name),
                    Key = key,
                    CreatedUtc = DateTime.UtcNow
                });
                Save(entries);
                return new ClientKeyCreated
                {
                    ClientId = id,
                    SecretBase64 = Convert.ToBase64String(key),
                    Fingerprint = Fingerprint(key)
                };
            }
        }

        public bool Remove(string clientId)
        {
            lock (_gate)
            {
                var entries = Load();
                var removed = entries.RemoveAll(e => string.Equals(e.Id, clientId, StringComparison.Ordinal));
                if (removed == 0) return false;
                Save(entries);
                return true;
            }
        }

        public byte[] FindKey(string clientId)
        {
            if (string.IsNullOrEmpty(clientId)) return null;
            lock (_gate)
            {
                var entry = Load().FirstOrDefault(e =>
                    string.Equals(e.Id, clientId, StringComparison.OrdinalIgnoreCase));
                return entry == null ? null : entry.Key;
            }
        }

        public int Count
        {
            get { lock (_gate) return Load().Count; }
        }

        public static string Fingerprint(byte[] key)
        {
            if (key == null || key.Length == 0) return "";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(key);
                return BitConverter.ToString(hash, 0, 6).Replace("-", "");
            }
        }

        private List<Entry> Load()
        {
            if (_entries != null) return _entries;
            _entries = new List<Entry>();
            try
            {
                if (!File.Exists(_path)) return _entries;
                var protectedBytes = File.ReadAllBytes(_path);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
                foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 4) continue;
                    var key = Convert.FromBase64String(parts[2]);
                    long ticks;
                    if (!long.TryParse(parts[3], out ticks)) ticks = DateTime.UtcNow.Ticks;
                    _entries.Add(new Entry
                    {
                        Id = parts[0],
                        Name = parts[1],
                        Key = key,
                        CreatedUtc = new DateTime(Math.Min(ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc)
                    });
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"读取网络客户端密钥失败: {ex.Message}");
                _entries = new List<Entry>();
            }
            return _entries;
        }

        private void Save(List<Entry> entries)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                foreach (var e in entries)
                {
                    sb.Append(e.Id).Append('\t')
                      .Append(Clean(e.Name)).Append('\t')
                      .Append(Convert.ToBase64String(e.Key)).Append('\t')
                      .Append(e.CreatedUtc.Ticks).Append('\n');
                }

                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
                var temp = _path + ".tmp";
                File.WriteAllBytes(temp, protectedBytes);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temp, _path);
                _entries = entries;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLine($"保存网络客户端密钥失败: {ex.Message}");
            }
        }

        private static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "客户端";
            return name.Replace('\t', ' ').Replace('\n', ' ').Trim();
        }
    }
}
