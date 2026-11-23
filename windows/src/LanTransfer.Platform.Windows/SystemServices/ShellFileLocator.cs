using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Platform.Windows.Files;

namespace LanTransfer.Platform.Windows.SystemServices;

/// <summary>
/// Windows 文件定位与打开（设计 C5.4、需求 19.1、19.4–19.7、19.11）：
/// 使用 AssocQueryString 检查扩展名关联后通过 ShellExecute 打开文件，用 SHOpenFolderAndSelectItems 在资源管理器中选中文件。
/// </summary>
public sealed class ShellFileLocator(FileSystemReceiveStorage storage) : IFileLocator
{
    public LocateResult OpenFile(StoredFileRef f, string mime)
    {
        var path = f.Value;
        if (!File.Exists(path))
        {
            return LocateResult.NotFound;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || !HasAssociatedApp(ext))
        {
            return LocateResult.NoHandler;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return LocateResult.Ok;
        }
        catch
        {
            return LocateResult.NoHandler;
        }
    }

    public LocateResult RevealInFolder(StoredFileRef f)
    {
        var path = f.Value;
        if (!File.Exists(path))
        {
            return LocateResult.NotFound;
        }

        try
        {
            var hr = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
            if (hr == 0 && pidl != IntPtr.Zero)
            {
                try
                {
                    var res = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
                    if (res == 0)
                    {
                        return LocateResult.Ok;
                    }
                }
                finally
                {
                    ILFree(pidl);
                }
            }

            // 回退 explorer /select
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return LocateResult.Ok;
        }
        catch
        {
            return LocateResult.NoAccess;
        }
    }

    public LocateResult OpenDirectory(DirRef dir)
    {
        var path = storage.ResolvePath(dir);
        if (!Directory.Exists(path))
        {
            return LocateResult.NoAccess;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return LocateResult.Ok;
        }
        catch
        {
            return LocateResult.NoAccess;
        }
    }

    public static bool HasAssociatedApp(string extension)
    {
        uint len = 0;
        var hr = AssocQueryStringW(0x00000040 /* ASSOCF_NOTRUNCATE */, 2 /* ASSOCSTR_EXECUTABLE */, extension, null, null, ref len);
        if (hr != 1 /* S_FALSE */ && hr != 0)
        {
            return false;
        }

        if (len <= 1)
        {
            return false;
        }

        var sb = new StringBuilder((int)len);
        hr = AssocQueryStringW(0x00000040, 2, extension, null, sb, ref len);
        return hr == 0 && sb.Length > 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(uint flags, uint str, string pszAssoc, string? pszExtra, StringBuilder? pszOut, ref uint pcchOut);
}
