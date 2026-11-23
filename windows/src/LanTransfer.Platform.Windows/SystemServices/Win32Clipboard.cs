using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using LanTransfer.Engine.Ports;

namespace LanTransfer.Platform.Windows.SystemServices;

/// <summary>
/// Win32 剪贴板适配（设计 C5.4、需求 13.3、13.9、13.10）：
/// 在 STA 线程调用 OpenClipboard（被占用时最多重试 1 s）；写入时同时写 CF_UNICODETEXT 与带显式长度的私有格式
/// <c>LanTransfer.UnicodeText</c>（保留含 NUL 的完整文本），写入后立即读回逐码点校验。
/// </summary>
public sealed class Win32Clipboard : IClipboard
{
    public const string PrivateFormatName = "LanTransfer.UnicodeText";
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int RetryTimeoutMs = 1_000;

    private static readonly uint PrivateFormatId = RegisterClipboardFormatW(PrivateFormatName);

    public Task<ClipRead> ReadTextAsync(CancellationToken ct = default) =>
        RunStaAsync(() => ReadCore(), ct);

    public Task<ClipWrite> WriteTextAsync(string s, CancellationToken ct = default) =>
        RunStaAsync(() => WriteCore(s), ct);

    /// <summary>
    /// Property 50 读回选择函数：私有格式存在且其第一个 NUL 之前的内容与 CF_UNICODETEXT 相同时取私有格式，否则取 CF_UNICODETEXT。
    /// </summary>
    public static string? SelectReadbackText(string? cfUnicode, string? privateText)
    {
        if (privateText is not null && cfUnicode is not null)
        {
            var nul = privateText.IndexOf('\0');
            var prefix = nul >= 0 ? privateText[..nul] : privateText;
            if (string.Equals(prefix, cfUnicode, StringComparison.Ordinal))
            {
                return privateText;
            }
        }

        return cfUnicode;
    }

    private static ClipRead ReadCore()
    {
        if (!TryOpenClipboard())
        {
            return new ClipRead.Failed();
        }

        try
        {
            var cf = ReadUnicodeText();
            var priv = ReadPrivateText();
            var text = SelectReadbackText(cf, priv);
            return string.IsNullOrEmpty(text) ? new ClipRead.NoText() : new ClipRead.Text(text);
        }
        catch
        {
            return new ClipRead.Failed();
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static ClipWrite WriteCore(string s)
    {
        if (!TryOpenClipboard())
        {
            return new ClipWrite.Failed();
        }

        try
        {
            if (!EmptyClipboard())
            {
                return new ClipWrite.Failed();
            }

            // 1. CF_UNICODETEXT（以 NUL 结尾；若 s 含 NUL，其他程序只看到第一个 NUL 之前的部分）
            var nul = s.IndexOf('\0');
            var stdText = nul >= 0 ? s[..nul] : s;
            var stdBytes = Encoding.Unicode.GetBytes(stdText + "\0");
            if (!SetGlobalData(CfUnicodeText, stdBytes))
            {
                return new ClipWrite.Failed();
            }

            // 2. 私有格式 LanTransfer.UnicodeText：4 字节大端 UTF-16LE 字节长度 + UTF-16LE 原文
            if (PrivateFormatId != 0)
            {
                var raw = Encoding.Unicode.GetBytes(s);
                var framed = new byte[4 + raw.Length];
                BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(0, 4), raw.Length);
                raw.CopyTo(framed.AsSpan(4));
                if (!SetGlobalData(PrivateFormatId, framed))
                {
                    return new ClipWrite.Failed();
                }
            }
        }
        catch
        {
            return new ClipWrite.Failed();
        }
        finally
        {
            CloseClipboard();
        }

        // 读回校验（13.3）
        var readback = ReadCore();
        if (s.Length == 0)
        {
            return readback is ClipRead.NoText ? new ClipWrite.Ok() : new ClipWrite.Failed();
        }

        return readback is ClipRead.Text t && string.Equals(t.Body, s, StringComparison.Ordinal)
            ? new ClipWrite.Ok()
            : new ClipWrite.Failed();
    }

    private static bool TryOpenClipboard()
    {
        var deadline = Environment.TickCount64 + RetryTimeoutMs;
        while (true)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(15);
        }
    }

    private static bool SetGlobalData(uint format, byte[] data)
    {
        var hMem = GlobalAlloc(GmemMoveable, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero)
        {
            return false;
        }

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }

        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        if (SetClipboardData(format, hMem) == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }

        return true;
    }

    private static string? ReadUnicodeText()
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText))
        {
            return null;
        }

        var h = GetClipboardData(CfUnicodeText);
        if (h == IntPtr.Zero)
        {
            return null;
        }

        var ptr = GlobalLock(h);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    private static string? ReadPrivateText()
    {
        if (PrivateFormatId == 0 || !IsClipboardFormatAvailable(PrivateFormatId))
        {
            return null;
        }

        var h = GetClipboardData(PrivateFormatId);
        if (h == IntPtr.Zero)
        {
            return null;
        }

        var size = (int)GlobalSize(h);
        if (size < 4)
        {
            return null;
        }

        var ptr = GlobalLock(h);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buf = new byte[size];
            Marshal.Copy(ptr, buf, 0, size);
            var len = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(0, 4));
            if (len < 0 || len + 4 > size || (len & 1) != 0)
            {
                return null;
            }

            return Encoding.Unicode.GetString(buf, 4, len);
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);
}
