using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using Microsoft.Win32;

namespace LanTransfer.Platform.Windows.Files;

/// <summary>
/// Windows 源文件读取适配（设计 C5.2、C7.1）：支持共享读写打开，MIME 依次查 HKCR 与内置映射表。
/// </summary>
public sealed class PathSourceFiles : ISourceFiles
{
    private static readonly Dictionary<string, string> BuiltInMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".csv"] = "text/csv",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        [".tar"] = "application/x-tar",
        [".gz"] = "application/gzip",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".heic"] = "image/heic",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".apk"] = "application/vnd.android.package-archive",
        [".exe"] = "application/vnd.microsoft.portable-executable",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public SourceMeta? Describe(SourceRef @ref)
    {
        try
        {
            var path = @ref.Value;
            if (Directory.Exists(path))
            {
                return new SourceMeta(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), 0, null, SourceMeta.OctetStream, IsDirectory: true);
            }

            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                return null;
            }

            var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            return new SourceMeta(fi.Name, fi.Length, mtime, DetectMime(fi.Extension), IsDirectory: false);
        }
        catch
        {
            return null;
        }
    }

    public SourceStat? Stat(SourceRef @ref)
    {
        try
        {
            var fi = new FileInfo(@ref.Value);
            if (!fi.Exists)
            {
                return null;
            }

            return new SourceStat(fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds());
        }
        catch
        {
            return null;
        }
    }

    public ReadResult<IChunkReader> Open(SourceRef @ref, long offset)
    {
        try
        {
            var fs = new FileStream(@ref.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            try
            {
                if (offset > 0)
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                }

                return new ReadResult<IChunkReader>.Ok(new StreamChunkReader(fs));
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            return new ReadResult<IChunkReader>.Failed(ex.Message);
        }
    }

    public static string DetectMime(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return SourceMeta.OctetStream;
        }

        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(extension);
            if (key?.GetValue("Content Type") is string reg && IsValidMime(reg))
            {
                return reg;
            }
        }
        catch
        {
        }

        return BuiltInMime.TryGetValue(extension, out var mime) ? mime : SourceMeta.OctetStream;
    }

    private static bool IsValidMime(string s) =>
        s.Length is > 0 and <= ProtocolConstants.MaxMimeBytes && s.All(c => c is >= ' ' and <= '~');
}
