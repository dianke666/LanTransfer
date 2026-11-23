using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Platform.Windows.Files;

/// <summary>
/// Windows 接收文件存储（设计 C5.2、C7.1、C7.5、M4）：
/// 默认目录为当前用户“下载”已知文件夹下的 LanTransfer；临时文件放在隐藏子目录 .lantransfer-tmp\；
/// 提交前写入 NTFS Zone.Identifier（ZoneId=3），并使用不覆盖的 MoveFileEx + NameCollisionResolver（不区分大小写）。
/// </summary>
public sealed partial class FileSystemReceiveStorage : IReceiveStorage
{
    public const string TempFolderName = ".lantransfer-tmp";

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    private readonly Func<IReadOnlyList<DirRef>> _knownDirs;
    private readonly Lock _commitGate = new();

    public FileSystemReceiveStorage(Func<IReadOnlyList<DirRef>>? knownDirs = null)
    {
        _knownDirs = knownDirs ?? (() => [DirRef.Default.Instance]);
    }

    public static string GetDefaultReceiveDirectory()
    {
        if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var ptr) == 0 && ptr != IntPtr.Zero)
        {
            try
            {
                var downloads = Marshal.PtrToStringUni(ptr);
                if (!string.IsNullOrWhiteSpace(downloads))
                {
                    return Path.Combine(downloads, "LanTransfer");
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads", "LanTransfer");
    }

    public string ResolvePath(DirRef dir) => dir switch
    {
        DirRef.Default => GetDefaultReceiveDirectory(),
        DirRef.Path p => p.Value,
        DirRef.Tree t => t.Uri,
        _ => GetDefaultReceiveDirectory(),
    };

    public DirStatus Probe(DirRef dir)
    {
        var path = ResolvePath(dir);
        try
        {
            if (Directory.Exists(path))
            {
                return CanWriteInto(path) ? DirStatus.Usable : DirStatus.NotWritable;
            }

            // 不存在时查找最近的已存在上级目录是否可写
            var cur = path;
            while (!string.IsNullOrEmpty(cur) && !Directory.Exists(cur))
            {
                var parent = Path.GetDirectoryName(cur);
                if (string.Equals(parent, cur, StringComparison.OrdinalIgnoreCase))
                {
                    return DirStatus.CannotCreate;
                }

                cur = parent;
            }

            if (string.IsNullOrEmpty(cur) || !Directory.Exists(cur))
            {
                return DirStatus.CannotCreate;
            }

            return CanWriteInto(cur) ? DirStatus.Usable : DirStatus.CannotCreate;
        }
        catch
        {
            return DirStatus.CannotCreate;
        }
    }

    public bool EnsureDirectory(DirRef dir)
    {
        var path = ResolvePath(dir);
        try
        {
            Directory.CreateDirectory(path);
            return CanWriteInto(path);
        }
        catch
        {
            return false;
        }
    }

    public bool IsWritable(DirRef dir)
    {
        var path = ResolvePath(dir);
        return Directory.Exists(path) && CanWriteInto(path);
    }

    public long? FreeSpace(DirRef dir)
    {
        var path = ResolvePath(dir);
        var cur = path;
        while (!string.IsNullOrEmpty(cur) && !Directory.Exists(cur))
        {
            cur = Path.GetDirectoryName(cur);
        }

        if (string.IsNullOrEmpty(cur))
        {
            return null;
        }

        return GetDiskFreeSpaceExW(cur, out var free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : null;
    }

    public bool Exists(DirRef dir, string name)
    {
        var path = ResolvePath(dir);
        var full = Path.Combine(path, name);
        return File.Exists(full) || Directory.Exists(full);
    }

    public TempRef CreateTemp(DirRef dir, TransferId task, int index)
    {
        var baseDir = ResolvePath(dir);
        var tmpDir = Path.Combine(baseDir, TempFolderName);
        var full = Path.Combine(tmpDir, $"{task.Value.ToHex()}-{index}.part");
        return new TempRef(full);
    }

    public WriteResult<ITempWriter> OpenAppend(TempRef temp, long offset)
    {
        try
        {
            var tmpDir = Path.GetDirectoryName(temp.Value);
            if (!string.IsNullOrEmpty(tmpDir))
            {
                Directory.CreateDirectory(tmpDir);
                TryHideDirectory(tmpDir);
            }

            var fs = new FileStream(temp.Value, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            try
            {
                if (offset < 0 || offset > fs.Length)
                {
                    offset = 0;
                }

                fs.SetLength(offset);
                fs.Seek(offset, SeekOrigin.Begin);
                return new WriteResult<ITempWriter>.Ok(new FileTempWriter(fs));
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            return new WriteResult<ITempWriter>.Failed(ex.Message);
        }
    }

    public ReadResult<IChunkReader> OpenRead(TempRef temp)
    {
        try
        {
            var fs = new FileStream(temp.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
            return new ReadResult<IChunkReader>.Ok(new StreamChunkReader(fs));
        }
        catch (Exception ex)
        {
            return new ReadResult<IChunkReader>.Failed(ex.Message);
        }
    }

    public long? Length(TempRef temp)
    {
        try
        {
            var fi = new FileInfo(temp.Value);
            return fi.Exists ? fi.Length : null;
        }
        catch
        {
            return null;
        }
    }

    public CommitResult Commit(TempRef temp, string sanitizedName)
    {
        if (!File.Exists(temp.Value))
        {
            return new CommitResult.Failed("临时文件不存在");
        }

        var tmpDir = Path.GetDirectoryName(temp.Value);
        var targetDir = string.IsNullOrEmpty(tmpDir) ? GetDefaultReceiveDirectory() : Path.GetDirectoryName(tmpDir) ?? tmpDir;
        var dirRef = new DirRef.Path(targetDir);

        lock (_commitGate)
        {
            try
            {
                Directory.CreateDirectory(targetDir);
                WriteMarkOfTheWeb(temp.Value);

                var startAt = 1;
                for (var attempt = 0; attempt < 256; attempt++)
                {
                    var candidate = attempt == 0 && !Exists(dirRef, sanitizedName)
                        ? sanitizedName
                        : NameCollisionResolver.Resolve(sanitizedName, FsProfile.Windows, n => Exists(dirRef, n), startAt);

                    var destPath = Path.Combine(targetDir, candidate);
                    if (MoveFileExW(temp.Value, destPath, 0))
                    {
                        return new CommitResult.Ok(candidate, destPath, new StoredFileRef(destPath));
                    }

                    var err = Marshal.GetLastWin32Error();
                    // ERROR_ALREADY_EXISTS (183) / ERROR_FILE_EXISTS (80): 重试下一个序号
                    if (err is 183 or 80)
                    {
                        startAt++;
                        continue;
                    }

                    return new CommitResult.Failed($"重命名失败（错误码 {err}）");
                }

                return new CommitResult.Failed("重名冲突次数过多");
            }
            catch (Exception ex)
            {
                return new CommitResult.Failed(ex.Message);
            }
        }
    }

    public bool Delete(TempRef temp)
    {
        try
        {
            if (!File.Exists(temp.Value))
            {
                return false;
            }

            File.Delete(temp.Value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<TempInfo> ListTemps()
    {
        var result = new List<TempInfo>();
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _knownDirs())
        {
            dirs.Add(ResolvePath(d));
        }

        foreach (var dir in dirs)
        {
            var tmpDir = Path.Combine(dir, TempFolderName);
            if (!Directory.Exists(tmpDir))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(tmpDir, "*.part"))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        var m = TempFilePattern().Match(fi.Name);
                        TransferId? tid = m.Success ? new TransferId(Bytes16.OfHex(m.Groups[1].Value)) : null;
                        var lwa = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                        result.Add(new TempInfo(new TempRef(file), tid, lwa));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        return result;
    }

    private static bool CanWriteInto(string dir)
    {
        var probe = Path.Combine(dir, $".lt-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch
            {
            }

            return false;
        }
    }

    private static void TryHideDirectory(string dir)
    {
        try
        {
            var attr = File.GetAttributes(dir);
            if ((attr & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(dir, attr | FileAttributes.Hidden);
            }
        }
        catch
        {
        }
    }

    /// <summary>写入 NTFS Mark-of-the-Web 备用数据流（C7.5）；非 NTFS 卷自动跳过。</summary>
    private static void WriteMarkOfTheWeb(string path)
    {
        try
        {
            var ads = path + ":Zone.Identifier";
            File.WriteAllText(ads, "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch
        {
        }
    }

    [GeneratedRegex("^([0-9a-f]{32})-(\\d+)\\.part$", RegexOptions.IgnoreCase)]
    private static partial Regex TempFilePattern();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    private sealed class FileTempWriter(FileStream stream) : ITempWriter
    {
        public IoResult Write(ReadOnlySpan<byte> data)
        {
            try
            {
                stream.Write(data);
                return IoResult.Success;
            }
            catch (Exception ex)
            {
                return new IoResult.Failed(ex.Message);
            }
        }

        public IoResult FlushSync()
        {
            try
            {
                stream.Flush(flushToDisk: true);
                return IoResult.Success;
            }
            catch (Exception ex)
            {
                return new IoResult.Failed(ex.Message);
            }
        }

        public void Close() => stream.Dispose();
    }
}

internal sealed class StreamChunkReader(FileStream stream) : IChunkReader
{
    public ReadResult<int> Read(Span<byte> buffer)
    {
        try
        {
            var n = stream.Read(buffer);
            return new ReadResult<int>.Ok(n == 0 ? -1 : n);
        }
        catch (Exception ex)
        {
            return new ReadResult<int>.Failed(ex.Message);
        }
    }

    public void Close() => stream.Dispose();
}
