using LanTransfer.Domain;
using LanTransfer.Engine.Ports;

namespace LanTransfer.Platform.Windows.Files;

/// <summary>“以 .txt 文件发送”的缓存目录（设计 M4）：<c>%LOCALAPPDATA%\LanTransfer\cache\outgoing-text\</c>。</summary>
public sealed class WindowsOutgoingTextCache : IOutgoingTextCache
{
    private readonly string _root;

    public WindowsOutgoingTextCache(string? rootDir = null)
    {
        _root = Path.GetFullPath(rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanTransfer",
            "cache",
            "outgoing-text"));
    }

    public WriteResult<SourceRef> Write(string dirName, string fileName, ReadOnlySpan<byte> content)
    {
        try
        {
            var dir = Path.Combine(_root, dirName);
            Directory.CreateDirectory(dir);
            var full = Path.Combine(dir, fileName);
            using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(content);
            fs.Flush(true);
            return new WriteResult<SourceRef>.Ok(new SourceRef(full));
        }
        catch (Exception ex)
        {
            return new WriteResult<SourceRef>.Failed(ex.Message);
        }
    }

    public IReadOnlyList<SourceRef> List()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var items = new List<SourceRef>();
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(_root))
            {
                foreach (var file in Directory.EnumerateFiles(sub))
                {
                    items.Add(new SourceRef(file));
                }
            }
        }
        catch
        {
        }

        return items;
    }

    public bool Owns(SourceRef @ref)
    {
        try
        {
            var full = Path.GetFullPath(@ref.Value);
            var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool Delete(SourceRef @ref)
    {
        if (!Owns(@ref))
        {
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(@ref.Value));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return false;
            }

            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
