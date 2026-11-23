using System.Text;
using LanTransfer.Domain;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>Property 16: 线路机密性（任务 19.3，与 Kotlin 侧 WireConfidentialityPropertyTest 对应）。</summary>
public sealed class WireConfidentialityPropertyTests
{
    private const int Window = 16;

    private sealed class SecretWindows
    {
        private readonly List<(string Label, byte[] Bytes)> _windows = [];
        private readonly long[] _heads;

        public SecretWindows(Dictionary<string, byte[]> secrets)
        {
            foreach (var (label, s) in secrets)
            {
                for (var i = 0; i <= s.Length - Window; i++)
                {
                    _windows.Add((label, s[i..(i + Window)]));
                }
            }

            _heads = _windows.Select(w => Head(w.Bytes, 0)).Order().ToArray();
        }

        public string? FirstLeak(byte[] captured)
        {
            for (var i = 0; i <= captured.Length - Window; i++)
            {
                if (Array.BinarySearch(_heads, Head(captured, i)) < 0)
                {
                    continue;
                }

                foreach (var (label, w) in _windows)
                {
                    if (captured.AsSpan(i, Window).SequenceEqual(w))
                    {
                        return $"{label} @ {i}";
                    }
                }
            }

            return null;
        }

        private static long Head(byte[] b, int off)
        {
            long v = 0;
            for (var k = 0; k < 8; k++)
            {
                v = (v << 8) | b[off + k];
            }

            return v;
        }
    }

    private static string RandomText(Random rnd, int minChars, int maxChars)
    {
        const string ascii = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var n = rnd.Next(minChars, maxChars + 1);
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++)
        {
            sb.Append(rnd.Next(3) == 0 ? (char)(0x4E00 + rnd.Next(0x51A5)) : ascii[rnd.Next(ascii.Length)]);
        }

        while (StrictUtf8.Encode(sb.ToString()).Length < Window)
        {
            sb.Append('x');
        }

        return sb.ToString();
    }

    // Feature: lan-file-transfer, Property 16: 线路机密性
    // Validates: Requirements 4.7, 10.1, 10.7
    [Fact]
    public async Task Property16_WireConfidentiality()
    {
        var count = Pbt.Iterations(100);
        for (var i = 0; i < count; i++)
        {
            var seed = 16000 + i;
            var rnd = new Random(seed);
            var clock = new FakeClock();
            var net = new InMemoryNetwork(clock);
            using var a = new HostDevice("A", seed * 2 + 1, [Kit.Iface("wlan0", 192, 168, 1, 2)], net, clock, DeviceType.Android, FsProfile.Android);
            using var b = new HostDevice("B", seed * 2 + 2, [Kit.Iface("wlan0", 192, 168, 1, 3)], net, clock, DeviceType.Windows, FsProfile.Windows);

            Assert.IsType<StartResult.Running>(await a.Host.StartNetworkingAsync(Kit.Ct));
            var bPort = Assert.IsType<StartResult.Running>(await b.Host.StartNetworkingAsync(Kit.Ct)).Port;

            var secrets = new Dictionary<string, byte[]>
            {
                ["A 私钥"] = a.Identity.KeyPair.PrivateKeyBytes(),
                ["B 私钥"] = b.Identity.KeyPair.PrivateKeyBytes(),
            };

            var byQr = rnd.Next(2) == 0;
            if (byQr)
            {
                using var qrCts = new CancellationTokenSource();
                QrScreenState.Showing? shown = null;
                await foreach (var s in b.Services.Pairing.QrScreenAsync(qrCts.Token))
                {
                    if (s is QrScreenState.Showing sh)
                    {
                        shown = sh;
                        qrCts.Cancel();
                        break;
                    }
                }

                Assert.NotNull(shown);
                secrets["令牌"] = shown.Payload.Token.ToArray();
                Assert.Equal(new PairingOutcome.Done(PairingResult.Success), await Kit.Within(a.Services.Pairing.PairByQrAsync(shown.Text)));
            }
            else
            {
                var target = new Target.Address(b.Transport.Monitor.Interfaces.Value[0].Address, bPort);
                var outcomeTask = a.Services.Pairing.PairAsync(target);
                var req = (PairingUiState.Request)await Kit.Within(
                    b.Services.Pairing.State.FirstAsync(s => s is PairingUiState.Request { Sas: not null }, Kit.Ct));
                b.Services.Pairing.Respond(req.Id, accept: true);
                Assert.Equal(new PairingOutcome.Done(PairingResult.Success), await Kit.Within(outcomeTask));
            }

            // 等双方都完成信任提交与会话晋升，避免后到的 CommitTrust 把 AutoAccept 重置回 false
            await Kit.WaitUntilAsync(() => a.Trust.Get(b.Id) is not null && b.Trust.Get(a.Id) is not null, "both committed trust");
            await Kit.WaitUntilAsync(() => a.Services.Sessions.GetSession(b.Id) is not null && b.Services.Sessions.GetSession(a.Id) is not null, "both promoted session");

            a.Trust.SetAutoAccept(b.Id, true);
            b.Trust.SetAutoAccept(a.Id, true);

            // 随机文字
            var textRounds = rnd.Next(1, 3);
            for (var k = 0; k < textRounds; k++)
            {
                var (from, to) = rnd.Next(2) == 0 ? (a, b) : (b, a);
                var t = RandomText(rnd, 16, 180);
                secrets[$"文字 {k}"] = StrictUtf8.Encode(t);
                Assert.IsType<SendCheck.Sent>(await from.Services.Text.SendAsync(to.Id, t));
                await Kit.Within(to.Services.Text.Conversation(from.Id).FirstAsync(l => l.Any(m => m.Body == t), Kit.Ct));
            }

            // 随机文件
            var fileRounds = rnd.Next(1, 3);
            for (var k = 0; k < fileRounds; k++)
            {
                var (from, to) = rnd.Next(2) == 0 ? (a, b) : (b, a);
                var name = RandomText(rnd, 12, 32) + ".bin";
                var content = new byte[rnd.Next(Window, 8_000)];
                rnd.NextBytes(content);
                secrets[$"文件名 {k}"] = StrictUtf8.Encode(name);
                secrets[$"文件内容 {k}"] = content;
                var src = new SourceRef($"src:{from.Name}/{k}");
                from.Sources.Put(src, name, Bytes.Of(content));
                var tid = Assert.IsType<SendOfferResult.Sent>(await from.Services.Files.SendAsync(to.Id, [src])).Id;
                await to.AwaitTaskAsync(tid, TaskState.Completed);
                Assert.Contains(to.Storage.FilesIn(DirRef.Default.Instance).Select(kv => kv.Value), v => v == Bytes.Of(content));
            }

            var windows = new SecretWindows(secrets);
            foreach (var conn in net.Connections)
            {
                Assert.Null(windows.FirstLeak(conn.BytesFromClient()));
                Assert.Null(windows.FirstLeak(conn.BytesFromServer()));
            }

            foreach (var dg in net.Datagrams)
            {
                Assert.Null(windows.FirstLeak(dg.Data.ToArray()));
            }
        }
    }
}
