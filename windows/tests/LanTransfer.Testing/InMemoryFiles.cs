using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Testing;

// 内存版文件端口（设计 C5.2），与 Kotlin 侧 InMemoryFiles.kt 对应。临时文件与最终文件分开存放，所以临时文件不会以最终文件名出现（16.12）；
// 提交时按 FsProfile 做重名处理（Windows 不区分大小写，16.11）。

/// <summary>一个内存目录：是否存在、能否创建、是否可写、可用空间（null = 未知）与已提交的文件。</summary>
public sealed class MemoryDir
{
    public bool Exists { get; set; }

    public bool Creatable { get; set; } = true;

    public bool Writable { get; set; } = true;

    public long? FreeSpace { get; set; }

    internal OrderedDictionary<string, Bytes> Files { get; } = new();
}

/// <summary>已登记的临时文件。</summary>
public sealed class MemoryTemp
{
    internal MemoryTemp(DirRef dir, TransferId transferId, int index, long lastWriteUtcMs)
    {
        Dir = dir;
        TransferId = transferId;
        Index = index;
        LastWriteUtcMs = lastWriteUtcMs;
    }

    public DirRef Dir { get; }

    public TransferId TransferId { get; }

    public int Index { get; }

    public long LastWriteUtcMs { get; set; }

    internal MemoryStream Data { get; } = new();

    public long Length
    {
        get
        {
            lock (Data)
            {
                return Data.Length;
            }
        }
    }

    public byte[] Bytes()
    {
        lock (Data)
        {
            return Data.ToArray();
        }
    }
}

public sealed class InMemoryReceiveStorage(FsProfile profile, IClock clock) : IReceiveStorage
{
    private readonly Lock _gate = new();
    private readonly Dictionary<DirRef, MemoryDir> _dirs = new();
    private readonly OrderedDictionary<TempRef, MemoryTemp> _temps = new();

    /// <summary>返回非 null 时，在该临时文件当前长度处的写入失败，返回值为失败原因（17.6）。</summary>
    public Func<TempRef, long, string?> WriteFailure { get; set; } = (_, _) => null;

    /// <summary>返回非 null 时提交失败。</summary>
    public Func<TempRef, string?> CommitFailure { get; set; } = _ => null;

    public IReadOnlyList<TempRef> TempRefs
    {
        get
        {
            lock (_gate)
            {
                return [.. _temps.Keys];
            }
        }
    }

    /// <summary>目录的可配置状态（不存在时按“可创建、可写、空间未知”新建）。</summary>
    public MemoryDir Dir(DirRef @ref)
    {
        lock (_gate)
        {
            if (!_dirs.TryGetValue(@ref, out var d))
            {
                d = new MemoryDir();
                _dirs[@ref] = d;
            }

            return d;
        }
    }

    /// <summary>已提交的文件（名称 → 内容），按提交顺序。</summary>
    public IReadOnlyList<KeyValuePair<string, Bytes>> FilesIn(DirRef @ref)
    {
        lock (_gate)
        {
            return [.. Dir(@ref).Files];
        }
    }

    /// <summary>预先放入一个已存在的文件（用于测试重名处理）。</summary>
    public void PutFile(DirRef @ref, string name, Bytes content)
    {
        lock (_gate)
        {
            var d = Dir(@ref);
            d.Exists = true;
            d.Files[name] = content;
        }
    }

    public MemoryTemp? Temp(TempRef @ref)
    {
        lock (_gate)
        {
            return _temps.GetValueOrDefault(@ref);
        }
    }

    /// <summary>模拟临时文件被外部删除或被清理（17.5）。</summary>
    public void RemoveTemp(TempRef @ref)
    {
        lock (_gate)
        {
            _temps.Remove(@ref);
        }
    }

    private static string PathOf(DirRef dir) => dir switch
    {
        DirRef.Default => "/default",
        DirRef.Tree t => "/tree/" + t.Uri,
        DirRef.Path p => p.Value.Replace('\\', '/'),
        _ => throw new ArgumentOutOfRangeException(nameof(dir)),
    };

    private bool SameName(string a, string b) =>
        string.Equals(a, b, profile == FsProfile.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public DirStatus Probe(DirRef dir)
    {
        lock (_gate)
        {
            var d = Dir(dir);
            return d.Exists && d.Writable ? DirStatus.Usable
                : d.Exists ? DirStatus.NotWritable
                : d.Creatable ? DirStatus.Usable
                : DirStatus.CannotCreate;
        }
    }

    public bool EnsureDirectory(DirRef dir)
    {
        lock (_gate)
        {
            var d = Dir(dir);
            if (!d.Exists && d.Creatable)
            {
                d.Exists = true;
            }

            return d.Exists && d.Writable;
        }
    }

    public bool IsWritable(DirRef dir) => Probe(dir) == DirStatus.Usable;

    public long? FreeSpace(DirRef dir)
    {
        lock (_gate)
        {
            return Dir(dir).FreeSpace;
        }
    }

    public bool Exists(DirRef dir, string name)
    {
        lock (_gate)
        {
            return Dir(dir).Files.Keys.Any(k => SameName(k, name));
        }
    }

    public TempRef CreateTemp(DirRef dir, TransferId task, int index)
    {
        lock (_gate)
        {
            var @ref = new TempRef($"mem:{PathOf(dir)}/.lantransfer-tmp/{task.Value.ToHex()}-{index}.part");
            _temps.TryAdd(@ref, new MemoryTemp(dir, task, index, clock.WallMs()));
            return @ref;
        }
    }

    public WriteResult<ITempWriter> OpenAppend(TempRef temp, long offset)
    {
        lock (_gate)
        {
            if (!_temps.TryGetValue(temp, out var t))
            {
                return new WriteResult<ITempWriter>.Failed("临时文件不存在");
            }

            var d = Dir(t.Dir);
            if (!d.Exists || !d.Writable)
            {
                return new WriteResult<ITempWriter>.Failed("接收目录不可写入");
            }

            lock (t.Data)
            {
                if (offset < 0 || offset > t.Data.Length)
                {
                    return new WriteResult<ITempWriter>.Failed($"offset {offset} 超出临时文件长度 {t.Data.Length}");
                }

                t.Data.SetLength(offset);
                t.Data.Position = offset;
            }

            return new WriteResult<ITempWriter>.Ok(new MemoryTempWriter(this, temp, t, d));
        }
    }

    private sealed class MemoryTempWriter(InMemoryReceiveStorage owner, TempRef @ref, MemoryTemp t, MemoryDir d) : ITempWriter
    {
        private bool _closed;

        public IoResult Write(ReadOnlySpan<byte> data)
        {
            lock (owner._gate)
            {
                if (_closed)
                {
                    return new IoResult.Failed("写入器已关闭");
                }

                if (owner.WriteFailure(@ref, t.Length) is { } failure)
                {
                    return new IoResult.Failed(failure);
                }

                if (d.FreeSpace is { } free)
                {
                    if (data.Length > free)
                    {
                        return new IoResult.Failed("存储空间不足");
                    }

                    d.FreeSpace = free - data.Length;
                }

                lock (t.Data)
                {
                    t.Data.Seek(0, SeekOrigin.End);
                    t.Data.Write(data);
                }

                t.LastWriteUtcMs = owner.WallNow();
                return IoResult.Success;
            }
        }

        public IoResult FlushSync() => _closed ? new IoResult.Failed("写入器已关闭") : IoResult.Success;

        public void Close() => _closed = true;
    }

    private long WallNow() => clock.WallMs();

    public ReadResult<IChunkReader> OpenRead(TempRef temp)
    {
        lock (_gate)
        {
            return _temps.TryGetValue(temp, out var t)
                ? new ReadResult<IChunkReader>.Ok(new BytesReader(t.Bytes(), 0, null))
                : new ReadResult<IChunkReader>.Failed("临时文件不存在");
        }
    }

    public long? Length(TempRef temp)
    {
        lock (_gate)
        {
            return _temps.TryGetValue(temp, out var t) ? t.Length : null;
        }
    }

    public CommitResult Commit(TempRef temp, string sanitizedName)
    {
        lock (_gate)
        {
            if (!_temps.TryGetValue(temp, out var t))
            {
                return new CommitResult.Failed("临时文件不存在");
            }

            if (CommitFailure(temp) is { } failure)
            {
                return new CommitResult.Failed(failure);
            }

            var d = Dir(t.Dir);
            if (!d.Exists || !d.Writable)
            {
                return new CommitResult.Failed("接收目录不可写入");
            }

            var final = NameCollisionResolver.Resolve(sanitizedName, profile, n => d.Files.Keys.Any(k => SameName(k, n)));
            d.Files[final] = Bytes.Of(t.Bytes());
            _temps.Remove(temp);
            var fullPath = $"{PathOf(t.Dir)}/{final}";
            return new CommitResult.Ok(final, fullPath, new StoredFileRef("mem:" + fullPath));
        }
    }

    public bool Delete(TempRef temp)
    {
        lock (_gate)
        {
            return _temps.Remove(temp);
        }
    }

    public IReadOnlyList<TempInfo> ListTemps()
    {
        lock (_gate)
        {
            return [.. _temps.Select(kv => new TempInfo(kv.Key, kv.Value.TransferId, kv.Value.LastWriteUtcMs))];
        }
    }
}

/// <summary>
/// 从字节数组读取；<c>failAt</c> 非 null 时，读到该位置（绝对偏移）时返回 Failed（模拟读取过程中文件被删除，14.7）。
/// <c>beforeRead</c> 在每次读取前以当前偏移调用（可以阻塞，测试用它让读盘停在某个位置）。
/// </summary>
internal sealed class BytesReader(byte[] data, int start, long? failAt, Action<long>? beforeRead = null) : IChunkReader
{
    private int _pos = start;
    private volatile bool _closed;

    public ReadResult<int> Read(Span<byte> buffer)
    {
        if (_closed)
        {
            return new ReadResult<int>.Failed("读取器已关闭");
        }

        beforeRead?.Invoke(_pos);
        if (_closed)
        {
            return new ReadResult<int>.Failed("读取器已关闭");
        }

        if (failAt is { } f && _pos >= f)
        {
            return new ReadResult<int>.Failed("读取失败");
        }

        if (_pos >= data.Length)
        {
            return new ReadResult<int>.Ok(-1);
        }

        var limit = failAt is { } fa ? (int)Math.Min(data.Length, fa) : data.Length;
        var n = Math.Min(buffer.Length, limit - _pos);
        data.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return new ReadResult<int>.Ok(n);
    }

    public void Close() => _closed = true;
}

/// <summary>内存版“以 .txt 文件发送”缓存：写入的文件同时登记到 sources，可直接走文件发送流程；<see cref="FailWrites"/> 模拟缓存目录不可写。</summary>
public sealed class InMemoryOutgoingTextCache(InMemorySourceFiles sources) : IOutgoingTextCache
{
    private readonly Lock _gate = new();
    private readonly OrderedDictionary<string, SourceRef> _dirs = new();

    public bool FailWrites { get; set; }

    /// <summary>已创建的子目录名（按创建顺序）。</summary>
    public IReadOnlyList<string> DirNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _dirs.Keys];
            }
        }
    }

    public WriteResult<SourceRef> Write(string dirName, string fileName, ReadOnlySpan<byte> content)
    {
        lock (_gate)
        {
            if (FailWrites)
            {
                return new WriteResult<SourceRef>.Failed("injected failure");
            }

            if (_dirs.ContainsKey(dirName))
            {
                return new WriteResult<SourceRef>.Failed("目录已存在");
            }

            var @ref = new SourceRef($"mem:outgoing-text/{dirName}/{fileName}");
            _dirs[dirName] = @ref;
            sources.Put(@ref, fileName, Bytes.Of(content), mime: "text/plain");
            return new WriteResult<SourceRef>.Ok(@ref);
        }
    }

    public IReadOnlyList<SourceRef> List()
    {
        lock (_gate)
        {
            return [.. _dirs.Values];
        }
    }

    public bool Owns(SourceRef @ref)
    {
        lock (_gate)
        {
            return _dirs.Values.Contains(@ref);
        }
    }

    public bool Delete(SourceRef @ref)
    {
        lock (_gate)
        {
            var dir = _dirs.FirstOrDefault(kv => kv.Value == @ref).Key;
            if (dir is null || !_dirs.Remove(dir))
            {
                return false;
            }

            sources.Remove(@ref);
            return true;
        }
    }
}

/// <summary>内存版源文件。</summary>
public sealed class InMemorySourceFiles : ISourceFiles
{
    private readonly Lock _gate = new();
    private readonly OrderedDictionary<SourceRef, Entry> _entries = new();

    public sealed class Entry
    {
        internal Entry(string name, Bytes data, long? mtime, string mime, bool isDirectory)
        {
            Name = name;
            Data = data;
            Mtime = mtime;
            Mime = mime;
            IsDirectory = isDirectory;
        }

        public string Name { get; set; }

        public Bytes Data { get; set; }

        public long? Mtime { get; set; }

        public string Mime { get; set; }

        public bool IsDirectory { get; }

        /// <summary>false：打开即失败（已删除、无权限，14.7）。</summary>
        public bool Readable { get; set; } = true;

        /// <summary>非 null：读到该偏移时失败。</summary>
        public long? FailAt { get; set; }

        /// <summary>每次读取前以当前偏移调用（在读盘线程上，不持有锁；可以阻塞）。C# 的传输在真实时间里进行，测试用它让传输停在“进行中”。</summary>
        public Action<long>? BeforeRead { get; set; }
    }

    public Entry Put(SourceRef @ref, string name, Bytes data, long? mtime = 1_700_000_000_000, string mime = SourceMeta.OctetStream)
    {
        lock (_gate)
        {
            var e = new Entry(name, data, mtime, mime, isDirectory: false);
            _entries[@ref] = e;
            return e;
        }
    }

    public Entry PutDirectory(SourceRef @ref, string name)
    {
        lock (_gate)
        {
            var e = new Entry(name, Bytes.Empty, null, SourceMeta.OctetStream, isDirectory: true);
            _entries[@ref] = e;
            return e;
        }
    }

    public Entry? EntryOf(SourceRef @ref)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(@ref);
        }
    }

    /// <summary>模拟源文件被修改（17.5）。</summary>
    public void Modify(SourceRef @ref, Bytes data, long? mtime)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(@ref, out var e))
            {
                e.Data = data;
                e.Mtime = mtime;
            }
        }
    }

    public void Remove(SourceRef @ref)
    {
        lock (_gate)
        {
            _entries.Remove(@ref);
        }
    }

    public SourceMeta? Describe(SourceRef @ref)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(@ref, out var e) ? new SourceMeta(e.Name, e.Data.Length, e.Mtime, e.Mime, e.IsDirectory) : null;
        }
    }

    public SourceStat? Stat(SourceRef @ref)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(@ref, out var e) && !e.IsDirectory && e.Readable ? new SourceStat(e.Data.Length, e.Mtime) : null;
        }
    }

    public ReadResult<IChunkReader> Open(SourceRef @ref, long offset)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(@ref, out var e))
            {
                return new ReadResult<IChunkReader>.Failed("文件不存在");
            }

            if (e.IsDirectory || !e.Readable)
            {
                return new ReadResult<IChunkReader>.Failed("无法读取");
            }

            var bytes = e.Data.ToArray();
            if (offset < 0 || offset > bytes.Length)
            {
                return new ReadResult<IChunkReader>.Failed($"offset {offset} 超出文件长度 {bytes.Length}");
            }

            return new ReadResult<IChunkReader>.Ok(new BytesReader(bytes, (int)offset, e.FailAt, e.BeforeRead));
        }
    }
}
