using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using LanTransfer.Domain;

namespace LanTransfer.Platform.Windows.SystemServices;

/// <summary>生产环境密码学安全随机数（C5.4）。</summary>
public sealed class CryptoRng : IRng
{
    public static CryptoRng Instance { get; } = new();

    private CryptoRng()
    {
    }

    public byte[] NextBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}

/// <summary>
/// 单实例互斥体与命名管道唤醒（设计 Architecture、C7.5）：
/// 保证桌面上只运行一个 LanTransfer 实例；再次启动或通知激活时唤醒主窗口。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\LanTransfer.SingleInstance";
    public const string PipeName = "LanTransfer.SingleInstance.Pipe";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public bool TryAcquire(Action<string> onMessage)
    {
        var m = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (!created)
        {
            m.Dispose();
            return false;
        }

        _mutex = m;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => ListenLoopAsync(onMessage, ct), CancellationToken.None);
        return true;
    }

    public static void SignalExistingInstance(string payload = "SHOW")
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1_000);
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch
        {
        }
    }

    private static async Task ListenLoopAsync(Action<string> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var msg = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                onMessage(string.IsNullOrWhiteSpace(msg) ? "SHOW" : msg);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}

/// <summary>有进行中文件传输时阻止系统空闲睡眠（设计 C7.1）。</summary>
public sealed class PowerGuard : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private bool _active;

    public void SetTransferActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        _ = SetThreadExecutionState(active ? EsContinuous | EsSystemRequired : EsContinuous);
    }

    public void Dispose() => SetTransferActive(false);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
