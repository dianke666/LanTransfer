using LanTransfer.Domain;
using LanTransfer.Protocol;

namespace LanTransfer.Engine.Ports;

// C5.2 文件端口，与 Kotlin 侧 Files.kt 同名镜像。临时文件不会以最终文件名出现（16.12）；
// 提交时才做文件名处理后的重名处理与不覆盖重命名（16.11）。这些方法是同步的（与 Kotlin 侧一致），引擎在线程池上调用。

/// <summary>不带返回值的 I/O 结果。<see cref="Failed.Detail"/> 是给用户看的失败原因（17.6）。</summary>
public abstract record IoResult
{
    private IoResult()
    {
    }

    public static Ok Success { get; } = new();

    public sealed record Ok : IoResult;

    public sealed record Failed(string Detail) : IoResult;
}

public abstract record WriteResult<T>
{
    private WriteResult()
    {
    }

    public sealed record Ok(T Value) : WriteResult<T>;

    public sealed record Failed(string Detail) : WriteResult<T>;
}

public abstract record ReadResult<T>
{
    private ReadResult()
    {
    }

    public sealed record Ok(T Value) : ReadResult<T>;

    public sealed record Failed(string Detail) : ReadResult<T>;
}

/// <summary>临时文件写入器。</summary>
public interface ITempWriter
{
    IoResult Write(ReadOnlySpan<byte> data);

    /// <summary>落盘（fsync）；接收方在校验与提交之前调用。</summary>
    IoResult FlushSync();

    void Close();
}

/// <summary>顺序读取器（源文件或续传时重新计算哈希的临时文件）。</summary>
public interface IChunkReader
{
    /// <summary>读入 <paramref name="buffer"/>：Ok(n) 中 n ≥ 1 为读到的字节数，n = -1 表示 EOF；读取失败返回 Failed（14.7）。</summary>
    ReadResult<int> Read(Span<byte> buffer);

    void Close();
}

public abstract record CommitResult
{
    private CommitResult()
    {
    }

    public sealed record Ok(string FinalName, string FullPath, StoredFileRef StoredRef) : CommitResult;

    public sealed record Failed(string Detail) : CommitResult;
}

/// <summary>源文件元数据：名称不含路径；无法识别类型时 Mime 为 application/octet-stream（14.4、14.9）。</summary>
public sealed record SourceMeta(string Name, long Size, long? Mtime, string Mime, bool IsDirectory)
{
    public const string OctetStream = "application/octet-stream";
}

public interface IReceiveStorage
{
    /// <summary>目录不存在但可以创建时也算 Usable（写第一个字节之前再创建，16.14）。</summary>
    DirStatus Probe(DirRef dir);

    /// <summary>创建目录及缺失的上级目录（16.14）。</summary>
    bool EnsureDirectory(DirRef dir);

    /// <summary>选择新目录时检查（16.13）。</summary>
    bool IsWritable(DirRef dir);

    /// <summary>可用空间；无法获知时为 null（15.5）。</summary>
    long? FreeSpace(DirRef dir);

    /// <summary>供 NameCollisionResolver 使用；Windows 不区分大小写。</summary>
    bool Exists(DirRef dir, string name);

    /// <summary>该任务该文件的临时文件引用；已存在时返回同一个（续传）。</summary>
    TempRef CreateTemp(DirRef dir, TransferId task, int index);

    /// <summary>截断到 offset 后追加。</summary>
    WriteResult<ITempWriter> OpenAppend(TempRef temp, long offset);

    ReadResult<IChunkReader> OpenRead(TempRef temp);

    /// <summary>null 表示临时文件不存在（RESUME_STATE 报 NONE）。</summary>
    long? Length(TempRef temp);

    /// <summary>内部做重名处理和不覆盖重命名。</summary>
    CommitResult Commit(TempRef temp, string sanitizedName);

    bool Delete(TempRef temp);

    /// <summary>供 TempFileJanitor 使用（17.7）。</summary>
    IReadOnlyList<TempInfo> ListTemps();
}

/// <summary>
/// “以 .txt 文件发送”的缓存目录（设计 C1.12、M4：Windows 为 <c>%LOCALAPPDATA%\LanTransfer\cache\outgoing-text\</c>）。
/// 设计 C5.2 未列出，此处补充，与 Kotlin 侧 <c>OutgoingTextCache</c> 对应。
/// </summary>
public interface IOutgoingTextCache
{
    /// <summary>
    /// 新建子目录 <paramref name="dirName"/>，在其中写入文件 <paramref name="fileName"/>，内容恰好为 <paramref name="content"/>（不加任何字节）；
    /// 返回的引用可直接交给 <see cref="ISourceFiles"/> 读取（Windows 为完整路径）。
    /// </summary>
    WriteResult<SourceRef> Write(string dirName, string fileName, ReadOnlySpan<byte> content);

    /// <summary>缓存中现有的全部文件（TempFileJanitor 启动清理用，M4）。</summary>
    IReadOnlyList<SourceRef> List();

    /// <summary><paramref name="ref"/> 是否是本缓存中的文件。</summary>
    bool Owns(SourceRef @ref);

    /// <summary>删除 <paramref name="ref"/> 所在的整个子目录；不存在时返回 false。</summary>
    bool Delete(SourceRef @ref);
}

public interface ISourceFiles
{
    SourceMeta? Describe(SourceRef @ref);

    /// <summary>续传前比对大小和修改时间（17.5）；不存在或无法读取时为 null。</summary>
    SourceStat? Stat(SourceRef @ref);

    /// <summary>从 offset 起读取；打开失败返回 Failed（14.7）。</summary>
    ReadResult<IChunkReader> Open(SourceRef @ref, long offset);
}
