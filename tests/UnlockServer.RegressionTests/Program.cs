using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnlockServer;
using UnlockServer.Models;
using UnlockServer.Network;
using UnlockServer.Services;
using UnlockServer.ViewModels;
using UnlockServer.Views;
using System.Windows;

class Program
{
    const string Address = "AA:BB:CC:DD:EE:FF";
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Private).SetValue(obj, value);
    static object Get(object obj, string name) => obj.GetType().GetField(name, Private).GetValue(obj);
    static void Call(object obj, string name) => obj.GetType().GetMethod(name, Private).Invoke(obj, null);
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Tick(UnlockManager manager) => Call(manager, "Tick");
    static void Expire(UnlockManager manager) => Set(manager, "_pendingUntil", DateTime.Now.AddSeconds(-1));
    static void Reset()
    {
        Application.Current.Dispatcher.Drain();
        BluetoothService.Shared.Devices.Clear();
        BluetoothService.Shared.Stall = false;
        BluetoothService.Shared.Users = 0;
        BluetoothService.Shared.BluetoothType = 2;
        WanClient.Locks = WanClient.Unlocks = 0;
        ToastService.Last = null;
        LocalUnlock.ClickUnlock = LocalUnlock.Available = false;
        LocalUnlock.UnlockResult = true;
        LocalUnlock.Requests = 0;
        LocalUnlock.Hint = null;
        LocalUnlock.RequestedSession = LocalUnlock.HintSession = null;
    }
    static UnlockManager Manager()
    {
        var manager = new UnlockManager { isautolock = true, lockOnlyWhenIdle = false, actionWarnSeconds = 10,
            sessionSwitchClass = new SessionSwitchClass(), manuallock = false };
        manager.SetBoundDevices(new[] { new BoundDevice { Address = Address } });
        Set(manager, "_bluetooth", BluetoothService.Shared);
        Set(manager, "_deviceLeftTime", (DateTime?)DateTime.Now.AddMinutes(-4));
        return manager;
    }
    static void Nearby()
    {
        BluetoothService.Shared.Devices.Add(new BluetoothDeviceModel { Address = Address, Name = "Watch",
            Type = "BLE", Rssi = -50, LastSeen = DateTime.Now });
    }
    static void Test(string name, Action test)
    {
        Reset(); test(); Console.WriteLine("PASS " + name);
    }
    static void WithChannels(Action<SessionUnlockChannel, SessionUnlockChannel> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "unlock-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { test(new SessionUnlockChannel(dir, 7), new SessionUnlockChannel(dir, 3)); }
        finally { Directory.Delete(dir, true); }
    }

    static void Main()
    {
        Test("manual unlock grace expires with no Bluetooth return", () =>
        {
            var manager = Manager();
            Set(manager, "_lastSoftwareLockTime", DateTime.Now);
            Call(manager, "HandleUserUnlockAfterSoftwareLock");
            Tick(manager);
            Assert(Get(manager, "_pending").ToString() == "None", "must suppress lock during grace");
            Set(manager, "_suppressLockUntil", DateTime.Now.AddSeconds(-1));
            Tick(manager); Application.Current.Dispatcher.Drain();
            Assert(ToastService.Last != null, "must show countdown after grace");
            Expire(manager);
            ToastService.Last.Timeout();
            Assert(WanClient.Locks == 0, "UI timeout must not execute OS actions");
            var uiThread = Environment.CurrentManagedThreadId;
            Task.Run(() => Tick(manager)).GetAwaiter().GetResult();
            Assert(WanClient.Locks == 1 && WanClient.LockThread != uiThread, "monitor must lock exactly once off UI thread");
            Application.Current.Dispatcher.Drain();
            Assert(ToastService.Last.Dismissed, "countdown must be dismissed after execution");
        });
        Test("device returns at deadline cancels locking", () =>
        {
            var manager = Manager(); Tick(manager); Expire(manager); Nearby(); Tick(manager);
            Assert(WanClient.Locks == 0 && Get(manager, "_pending").ToString() == "None", "revalidate presence before locking");
        });
        Test("cancel countdown needs no manager lock on UI", () =>
        {
            var manager = Manager(); Tick(manager); Application.Current.Dispatcher.Drain();
            var gate = Get(manager, "lockLock");
            using (var held = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var worker = Task.Run(() => { lock (gate) { held.Set(); release.Wait(); } });
                held.Wait();
                var cancel = Task.Run(() => ToastService.Last.Cancel());
                bool completed = cancel.Wait(1000);
                release.Set(); worker.Wait(); cancel.Wait();
                Assert(completed, "cancel callback blocked on monitor lock");
            }
            Expire(manager); Tick(manager);
            Assert(WanClient.Locks == 0, "cancel must win over expired countdown");
        });
        Test("stale countdown cannot cancel a newer countdown", () =>
        {
            var manager = Manager(); Tick(manager); Application.Current.Dispatcher.Drain();
            var old = ToastService.Last; Nearby(); Tick(manager);
            BluetoothService.Shared.Devices.Clear();
            Set(manager, "_deviceLeftTime", (DateTime?)DateTime.Now.AddMinutes(-1));
            Tick(manager); Application.Current.Dispatcher.Drain(); old.Cancel(); Expire(manager); Tick(manager);
            Assert(WanClient.Locks == 1, "old cancellation affected new request");
        });
        Test("silence grace ends and departure can lock", () =>
        {
            var manager = Manager(); BluetoothService.Shared.Stall = true; Tick(manager);
            Assert(Get(manager, "_pending").ToString() == "None", "must hold during bounded recovery");
            BluetoothService.Shared.Stall = false; Tick(manager); Expire(manager); Tick(manager);
            Assert(WanClient.Locks == 1, "silence must not suppress lock forever");
        });
        Test("locked session cannot unlock from stale RSSI", () =>
        {
            var manager = Manager(); manager.isautounlock = true; manager.sessionSwitchClass.IsLocked = true;
            Nearby(); Tick(manager);
            BluetoothService.Shared.Devices[0].LastSeen = DateTime.Now.AddMinutes(-1);
            Set(manager, "_deviceEnteredTime", (DateTime?)DateTime.Now.AddMinutes(-1));
            BluetoothService.Shared.Stall = true; LocalUnlock.ClickUnlock = true; Tick(manager);
            Assert(WanClient.Unlocks == 0, "stale RSSI authorized click unlock");
        });
        Test("rapid stop/start cannot resurrect delayed monitor", () =>
        {
            var manager = Manager(); manager.Start(); manager.Stop(); manager.Start(); manager.Stop();
            Thread.Sleep(2100);
            Assert(BluetoothService.Shared.Users == 0 && WanClient.Locks == 0, "stopped monitor resurrected");
        });
        Test("device dialog owns one scan reference and isolated models", () =>
        {
            BluetoothService.Shared.Users = 1; Nearby();
            BluetoothService.Shared.Devices.Add(new BluetoothDeviceModel { Address = "11:22:33:44:55:66", Name = "Headset", Type = "Classic", Rssi = -65 });
            var vm = new DeviceListViewModel(0); vm.StartScanning(); vm.StartScanning();
            Call(vm, "RefreshDeviceList");
            Assert(BluetoothService.Shared.Users == 2 && vm.Devices.Count == 2, "unbalanced acquire or missing list");
            Assert(!ReferenceEquals(vm.Devices[0], BluetoothService.Shared.Devices[0]), "UI shares scanner model");
            vm.BluetoothType = 1;
            Assert(vm.Devices.Count == 1 && vm.Devices[0].Type == "Classic", "classic filter failed");
            vm.BluetoothType = 2; vm.SearchText = "watch";
            Assert(vm.Devices.Count == 1 && vm.Devices[0].Type == "BLE", "BLE/name filters failed");
            Assert(BluetoothService.Shared.BluetoothType == 2, "dialog overwrote monitor scan type");
            BluetoothService.Shared.Devices[0].Rssi = -75;
            Assert(vm.Devices[0].Rssi == -50, "scanner directly mutated UI");
            Call(vm, "RefreshDeviceList"); Assert(vm.Devices[0].Rssi == -75, "snapshot not refreshed");
            vm.StopScanning(); vm.Dispose(); vm.Dispose();
            Assert(BluetoothService.Shared.Users == 1, "closing dialog stopped main monitor");
        });
        Test("RDP remains unlocked but console click targets the local lock screen", () =>
        {
            var manager = Manager(); Nearby();
            manager.sessionSwitchClass.IsLocked = false;
            LocalUnlock.Available = LocalUnlock.ClickUnlock = true;
            Tick(manager);
            Assert(LocalUnlock.Requests == 1 && LocalUnlock.RequestedSession == LocalUnlock.ClickSession,
                "remote unlocked state swallowed console click or wrong target");
            Assert(WanClient.Unlocks == 0, "console request used session-unaware remote service");
        });
        Test("console click still requires fresh Bluetooth", () =>
        {
            var manager = Manager(); Nearby();
            BluetoothService.Shared.Devices[0].LastSeen = DateTime.Now.AddMinutes(-1);
            LocalUnlock.Available = LocalUnlock.ClickUnlock = true; Tick(manager);
            Assert(LocalUnlock.Requests == 0 && WanClient.Unlocks == 0 && LocalUnlock.Hint == "away" &&
                LocalUnlock.HintSession == LocalUnlock.ClickSession, "stale Bluetooth authorized console unlock");
        });
        Test("console failure is returned to its session without remote fallback", () =>
        {
            var manager = Manager(); Nearby();
            LocalUnlock.Available = LocalUnlock.ClickUnlock = true; LocalUnlock.UnlockResult = false;
            Tick(manager);
            Assert(LocalUnlock.Hint == "fail" && LocalUnlock.HintSession == LocalUnlock.ClickSession && WanClient.Unlocks == 0,
                "failed console request was hidden or sent to remote service");
            Tick(manager);
            Assert(LocalUnlock.Hint == "fail", "fresh RSSI erased failure feedback");
        });
        Test("manual console click bypasses automatic retry cooldown", () =>
        {
            var manager = Manager(); Nearby(); manager.sessionSwitchClass.IsLocked = true;
            Set(manager, "isunlockfail", true);
            LocalUnlock.Available = LocalUnlock.ClickUnlock = true; Tick(manager);
            Assert(LocalUnlock.Requests == 1, "automatic retry cooldown swallowed user click");
        });
        Test("no bound device returns feedback rather than waiting forever", () =>
        {
            var manager = Manager(); manager.SetBoundDevices(Array.Empty<BoundDevice>());
            LocalUnlock.ClickUnlock = true; Tick(manager);
            Assert(LocalUnlock.Hint == "away" && LocalUnlock.Requests == 0, "empty configuration swallowed click");
        });
        Test("session protocol isolates requests and consumes only fresh clicks", () =>
        {
            WithChannels((console, remote) =>
            {
                var now = DateTime.UtcNow;
                File.WriteAllText(remote.PathFor("now"), now.Ticks.ToString());
                Assert(!console.ConsumeClick(now) && File.Exists(remote.PathFor("now")), "stole remote session click");
                File.WriteAllText(console.PathFor("now"), now.Ticks.ToString());
                Assert(console.ConsumeClick(now) && !console.ConsumeClick(now), "click not consumed once");
                foreach (var value in new[] { "garbage", "-1", long.MaxValue.ToString(), now.AddSeconds(-21).Ticks.ToString() })
                {
                    File.WriteAllText(console.PathFor("now"), value);
                    Assert(!console.ConsumeClick(now), "accepted expired/future/invalid click");
                }
            });
        });
        Test("request waits for target authentication result, not remote unlocked state", () =>
        {
            WithChannels((console, remote) =>
            {
                int poll = 0;
                bool ok = console.Request(() => { }, _ =>
                {
                    poll++;
                    var token = File.ReadAllText(console.PathFor("req"));
                    if (poll == 1) File.WriteAllText(remote.PathFor("result"), token + "|ok");
                    if (poll == 2) File.WriteAllText(console.PathFor("result"), "old-token|ok");
                    if (poll == 3) File.WriteAllText(console.PathFor("result"), token + "|submitted");
                    if (poll == 4) File.WriteAllText(console.PathFor("result"), token + "|ok");
                }, 5);
                Assert(ok && poll == 4 && !File.Exists(console.PathFor("req")), "accepted unrelated or submitted-only result");
            });
        });
        Test("concurrent requests cannot overwrite the same session token", () =>
        {
            WithChannels((console, remote) =>
            {
                bool ok = console.Request(() => { }, _ =>
                {
                    var token = File.ReadAllText(console.PathFor("req"));
                    bool competing = Task.Run(() => console.Request(() => { }, __ => { }, 1)).GetAwaiter().GetResult();
                    Assert(!competing && File.ReadAllText(console.PathFor("req")) == token,
                        "concurrent request replaced in-flight token");
                    File.WriteAllText(console.PathFor("result"), token + "|ok");
                }, 1);
                Assert(ok, "original request lost its response");
            });
        });
        Test("missing response, request deletion and authentication failure are not success", () =>
        {
            WithChannels((console, remote) =>
            {
                Assert(!console.Request(() => { }, _ => File.Delete(console.PathFor("req")), 2), "deletion incorrectly confirmed authentication");
                Assert(!console.Request(() => { }, _ => { }, 2) && !File.Exists(console.PathFor("req")), "timeout left live request");
                Assert(!console.Request(() => { }, _ =>
                    File.WriteAllText(console.PathFor("result"), File.ReadAllText(console.PathFor("req")) + "|fail"), 2),
                    "authentication failure reported success");
            });
        });
        Test("network signature is single-use and bound to the canonical string", () =>
        {
            var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
                                   17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 };
            var keys = new FakeKeys();
            keys.Add("c1", key);
            var auth = new ApiAuth(keys);

            var challenge = auth.IssueChallenge("c1");
            Assert(challenge.Nonce != null, "challenge refused");
            var sig = ApiAuth.ComputeSignature(key, "c1", "GET", "/v1/state", challenge.Nonce, "");
            Assert(auth.Verify("c1", "GET", "/v1/state", challenge.Nonce, sig, "").Ok, "valid signature rejected");
            Assert(auth.Verify("c1", "GET", "/v1/state", challenge.Nonce, sig, "").Code == AuthCode.ReplayedNonce,
                "nonce was reusable");

            var second = auth.IssueChallenge("c1");
            Assert(auth.Verify("c1", "GET", "/v1/state", second.Nonce, "bogus", "").Code == AuthCode.BadSignature,
                "wrong signature accepted");
            Assert(auth.Verify("ghost", "GET", "/v1/state", second.Nonce, sig, "").Code == AuthCode.UnknownClient,
                "unknown client accepted");

            for (var i = 0; i < 5; i++)
            {
                var c = auth.IssueChallenge("c1");
                auth.Verify("c1", "GET", "/v1/state", c.Nonce, "bogus", "");
            }
            Assert(auth.IssueChallenge("c1").Error == "rate_limited", "failures did not trigger lockout");
        });
        Test("network queue dedupes request ids and refuses overflow", () =>
        {
            var queue = new NetworkCommandQueue(2);
            Assert(queue.TryEnqueue("a", NetworkCommandKind.Lock, "c1", out _), "first enqueue refused");
            Assert(!queue.TryEnqueue("a", NetworkCommandKind.Lock, "c1", out var duplicate) && duplicate == "duplicate",
                "duplicate request accepted");
            Assert(queue.TryEnqueue("b", NetworkCommandKind.Unlock, "c1", out _), "second enqueue refused");
            Assert(!queue.TryEnqueue("c", NetworkCommandKind.Lock, "c1", out var busy) && busy == "busy",
                "overflow accepted");

            NetworkRequest request;
            Assert(queue.TryDequeue(out request) && request.Id == "a", "FIFO order lost");
            Assert(queue.Complete("a", NetworkRequestState.Done, null), "completion refused");
            Assert(queue.Find("a").State == NetworkRequestState.Done, "completion not persisted");
            Assert(!queue.Complete("a", NetworkRequestState.Rejected, "x"), "double completion accepted");
        });
        Test("network lock runs without any bound Bluetooth device", () =>
        {
            var queue = new NetworkCommandQueue();
            var manager = new UnlockManager
            {
                isautolock = false, isautounlock = false, manuallock = false,
                sessionSwitchClass = new SessionSwitchClass()
            };
            manager.SetNetworkQueue(queue);
            Set(manager, "_bluetooth", BluetoothService.Shared);
            manager.SetBoundDevices(Array.Empty<BoundDevice>());

            Assert(queue.TryEnqueue("lock1", NetworkCommandKind.Lock, "c1", out _), "enqueue refused");
            Tick(manager);
            Assert(WanClient.Locks == 1, "lock did not reach the OS");
            Assert(queue.Find("lock1").State == NetworkRequestState.Done, "lock request not completed");
        });
        Test("network unlock ignores Bluetooth presence and targets the console session", () =>
        {
            var queue = new NetworkCommandQueue();
            var manager = new UnlockManager
            {
                isautolock = false, isautounlock = false, manuallock = false,
                sessionSwitchClass = new SessionSwitchClass()
            };
            manager.SetNetworkQueue(queue);
            Set(manager, "_bluetooth", BluetoothService.Shared);
            manager.SetBoundDevices(new[] { new BoundDevice { Address = Address } });
            // 设备已绑定但没有任何广播，等价于"不在附近的另一路数据源"。
            manager.sessionSwitchClass.IsLocked = true;
            LocalUnlock.Available = true;
            LocalUnlock.UnlockResult = true;

            Assert(queue.TryEnqueue("unlock1", NetworkCommandKind.Unlock, "c1", out _), "enqueue refused");
            Tick(manager);
            Assert(LocalUnlock.Requests == 1, "unlock did not use the local provider");
            Assert(LocalUnlock.RequestedSession == WanClient.WTSGetActiveConsoleSessionId(),
                "unlock targeted a non-console session");
            Assert(queue.Find("unlock1").State == NetworkRequestState.Done, "unlock request not completed");
        });
        Test("network unlock refuses when the provider is not installed", () =>
        {
            var queue = new NetworkCommandQueue();
            var manager = new UnlockManager
            {
                isautolock = false, isautounlock = false, manuallock = false,
                sessionSwitchClass = new SessionSwitchClass()
            };
            manager.SetNetworkQueue(queue);
            Set(manager, "_bluetooth", BluetoothService.Shared);
            manager.sessionSwitchClass.IsLocked = true;
            LocalUnlock.Available = false;

            Assert(queue.TryEnqueue("unlock2", NetworkCommandKind.Unlock, "c1", out _), "enqueue refused");
            Tick(manager);
            var request = queue.Find("unlock2");
            Assert(request.State == NetworkRequestState.Rejected && request.Reason == "provider_not_ready",
                "missing provider was not reported");
            Assert(LocalUnlock.Requests == 0, "requested unlock without a provider");
        });
        Test("network lock on an already locked session reports no state change", () =>
        {
            var queue = new NetworkCommandQueue();
            var manager = new UnlockManager
            {
                isautolock = false, isautounlock = false, manuallock = false,
                sessionSwitchClass = new SessionSwitchClass()
            };
            manager.SetNetworkQueue(queue);
            Set(manager, "_bluetooth", BluetoothService.Shared);
            manager.sessionSwitchClass.IsLocked = true;

            Assert(queue.TryEnqueue("lock2", NetworkCommandKind.Lock, "c1", out _), "enqueue refused");
            Tick(manager);
            Assert(WanClient.Locks == 0, "locked session was locked twice");
            var request = queue.Find("lock2");
            Assert(request.State == NetworkRequestState.Done && request.Reason == "already_in_state",
                "idempotent lock not reported");
        });
        Console.WriteLine("All regression scenarios passed (OS/Bluetooth side effects simulated).");
    }

    class FakeKeys : IClientKeyProvider
    {
        private readonly System.Collections.Generic.Dictionary<string, byte[]> _keys =
            new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal);

        public void Add(string id, byte[] key) { _keys[id] = key; }

        public byte[] FindKey(string clientId)
        {
            byte[] key;
            return _keys.TryGetValue(clientId ?? "", out key) ? key : null;
        }
    }
}
