using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Platform.Windows.Files;
using LanTransfer.Platform.Windows.Security;
using LanTransfer.Platform.Windows.Settings;
using LanTransfer.Platform.Windows.Storage;
using LanTransfer.Platform.Windows.SystemServices;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LanTransfer.Platform.Windows.Tests;

/// <summary>Windows 平台适配与存储层测试（任务 21.4–21.6、30.5、30.8）。</summary>
public sealed class PlatformWindowsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lt-win-tests-{Guid.NewGuid():N}");

    public PlatformWindowsTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    // Feature: lan-file-transfer, Property 48: 传输记录存储
    // Validates: Requirements 11.8, 20.1, 20.2, 20.3, 20.4, 20.5, 20.7
    [Fact]
    public void Property48_HistoryStore_RoundTrip_Dedupe_Ordering_Delete_And_Reopen()
    {
        var dbPath = Path.Combine(_tempDir, "history.db");
        var clock = new FakeClock();
        var peer = new DeviceId(Bytes16.Of(Enumerable.Repeat((byte)0x11, 16).ToArray()));
        var samples = new[]
        {
            "普通中文消息",
            "含\0空字符\0和\r\nCRLF\r单独CR\nLF\t制表符",
            "   前后空白  \u3000\u00A0",
            "\uFEFF带BOM与表情👨‍👩‍👧‍👦🎉",
        };

        var msgIds = new List<MessageId>();
        using (var db = new SqliteDatabase(dbPath, clock))
        {
            Assert.True(db.IsHealthy);
            var history = new SqliteHistoryStore(db);
            var tasks = new SqliteTaskStore(db);

            for (var i = 0; i < samples.Length; i++)
            {
                var mid = new MessageId(Bytes16.Of(Enumerable.Repeat((byte)(i + 1), 16).ToArray()));
                msgIds.Add(mid);
                var rec = new TextRecord(0, peer, "Pixel 9", mid, Direction.Receive, Bytes.Of(StrictUtf8.Encode(samples[i])), 1000 + i, new RecordTime(2000 + i, 480), TextStatus.Received);
                Assert.IsType<StoreResult.Ok>(history.AppendText(rec));
                // 重复消息去重（11.8）
                Assert.IsType<StoreResult.Duplicate>(history.AppendText(rec));
            }

            // 更新状态（20.7）
            Assert.IsType<StoreResult.Ok>(history.UpdateTextStatus(msgIds[0], peer, TextStatus.Delivered));

            // 写入任务与两个文件
            var tid = new TransferId(Bytes16.Of(Enumerable.Repeat((byte)0xAA, 16).ToArray()));
            var f0 = new TransferFile(tid, 0, 0, "a.txt", 100, "text/plain", null, FileState.Completed, 100, FinalName: "a.txt", FullPath: @"C:\a.txt", StoredRef: new StoredFileRef(@"C:\a.txt"));
            var f1 = new TransferFile(tid, 1, 0, "b.bin", 200, "application/octet-stream", null, FileState.Completed, 200, FinalName: "b.bin", FullPath: @"C:\b.bin", StoredRef: new StoredFileRef(@"C:\b.bin"));
            var task = new TransferTask(tid, peer, "Pixel 9", Direction.Receive, TaskState.Completed, null, DirRef.Default.Instance, new RecordTime(3000, 480), 3001, new ValueList<TransferFile>([f0, f1]));
            Assert.IsType<StoreResult.Ok>(tasks.Save(task));

            // 删除第一个文件记录，任务行仍保留；删除第二个后任务行一并移除
            Assert.IsType<StoreResult.Ok>(history.DeleteFileRecord(tid, 0));
            Assert.NotNull(tasks.Get(tid));
            Assert.IsType<StoreResult.Ok>(history.DeleteFileRecord(tid, 1));
            Assert.Null(tasks.Get(tid));
        }

        // 重新打开数据库验证无损往返与顺序
        using (var db2 = new SqliteDatabase(dbPath, clock))
        {
            var history = new SqliteHistoryStore(db2);
            var ok = Assert.IsType<ListResult<HistoryRecord>.Ok>(history.List());
            Assert.Equal(samples.Length, ok.Items.Count);
            for (var i = 0; i < samples.Length; i++)
            {
                var tr = Assert.IsType<HistoryRecord.Text>(ok.Items[i]).Record;
                Assert.Equal(samples[i], StrictUtf8.Decode(tr.TextUtf8.Span));
                Assert.Equal(i == 0 ? TextStatus.Delivered : TextStatus.Received, tr.Status);
            }

            // ClearAll 后列表为空，迟到的 UpdateTextStatus 不会重新创建
            Assert.IsType<StoreResult.Ok>(history.ClearAll());
            Assert.IsType<StoreResult.Ok>(history.UpdateTextStatus(msgIds[0], peer, TextStatus.Delivered));
            Assert.Empty(Assert.IsType<ListResult<HistoryRecord>.Ok>(history.List()).Items);
        }
    }

    // Feature: lan-file-transfer, Property 49: 信任列表存储
    // Validates: Requirements 9.1, 9.3, 9.9
    [Fact]
    public void Property49_TrustStore_Supports120Devices_Upsert_Delete_And_Reopen()
    {
        var dbPath = Path.Combine(_tempDir, "trust.db");
        var clock = new FakeClock();
        var ids = new List<DeviceId>();

        using (var db = new SqliteDatabase(dbPath, clock))
        {
            var trust = new SqliteTrustStore(db);
            for (var i = 0; i < 120; i++)
            {
                var raw = new byte[16];
                raw[0] = (byte)i;
                raw[15] = 0x5A;
                var id = new DeviceId(Bytes16.Of(raw));
                ids.Add(id);
                var type = (i % 3) switch { 0 => DeviceType.Android, 1 => DeviceType.Windows, _ => DeviceType.Ios };
                var pk = Bytes.Of(Enumerable.Repeat((byte)(i + 1), 32).ToArray());
                Assert.IsType<StoreResult.Ok>(trust.Upsert(new TrustedDevice(id, $"设备 {i}", type, pk, AutoAccept: i % 2 == 0, PairedAtUtcMs: 1000 + i)));
            }

            Assert.Equal(120, trust.All().Count);
            // 整条替换（9.9）
            var updatedPk = Bytes.Of(Enumerable.Repeat((byte)0xEE, 32).ToArray());
            Assert.IsType<StoreResult.Ok>(trust.Upsert(new TrustedDevice(ids[0], "新名称", DeviceType.Ios, updatedPk, AutoAccept: false, PairedAtUtcMs: 9999)));
            Assert.Equal(120, trust.All().Count);
            var d0 = trust.Get(ids[0])!;
            Assert.Equal("新名称", d0.DisplayName);
            Assert.Equal(DeviceType.Ios, d0.DeviceType);
            Assert.False(d0.AutoAccept);

            // 删除同时清除自动接收设置（9.3）
            Assert.IsType<StoreResult.Ok>(trust.Delete(ids[1]));
            Assert.Null(trust.Get(ids[1]));
            Assert.Equal(119, trust.All().Count);
        }

        using (var db2 = new SqliteDatabase(dbPath, clock))
        {
            var trust = new SqliteTrustStore(db2);
            Assert.Equal(119, trust.All().Count);
            Assert.Equal("新名称", trust.Get(ids[0])!.DisplayName);
            Assert.Null(trust.Get(ids[1]));
        }
    }

    [Fact]
    public void Database_refuses_downgrade_without_modifying_file()
    {
        var dbPath = Path.Combine(_tempDir, "future.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        using var db = new SqliteDatabase(dbPath, new FakeClock());
        Assert.False(db.IsHealthy);
        var history = new SqliteHistoryStore(db);
        Assert.IsType<ListResult<HistoryRecord>.Failed>(history.List());
    }

    // Feature: lan-file-transfer, Property 50: Windows 剪贴板读回
    // Validates: Requirements 13.3, 13.10
    [PbtProperty(1000)]
    public void Property50_WindowsClipboardReadbackSelection(UnicodeText first, UnicodeText other)
    {
        var s = first.Value;
        var nul = s.IndexOf('\0');
        var cf = nul >= 0 ? s[..nul] : s;
        // 当 CF_UNICODETEXT 与私有格式在第一个 NUL 前一致时，还原完整原文（含 NUL 之后）
        Assert.Equal(s, Win32Clipboard.SelectReadbackText(cf, s));
        // 当外部程序覆盖了 CF_UNICODETEXT（不再匹配私有格式前缀）时，以 CF_UNICODETEXT 为准
        var extCf = other.Value + "_ext";
        if (!string.Equals(cf, extCf, StringComparison.Ordinal))
        {
            Assert.Equal(extCf, Win32Clipboard.SelectReadbackText(extCf, s));
        }
    }

    [Fact]
    public void DpapiIdentityStore_round_trips_and_resets_on_corruption()
    {
        var settings = new JsonSettingsStore(Path.Combine(_tempDir, "settings.json"));
        var idPath = Path.Combine(_tempDir, "identity.bin");
        var rng = new SeededRng(42);

        var store1 = new DpapiIdentityStore(idPath, settings, rng);
        var created = Assert.IsType<IdentityLoad.Created>(store1.LoadOrCreate());
        Assert.IsType<StoreResult.Ok>(store1.SetDisplayName("我的工作站"));

        var store2 = new DpapiIdentityStore(idPath, settings, rng);
        var loaded = Assert.IsType<IdentityLoad.Loaded>(store2.LoadOrCreate());
        Assert.Equal(created.Identity.DeviceId, loaded.Identity.DeviceId);
        Assert.Equal(created.Identity.KeyPair.PublicKey, loaded.Identity.KeyPair.PublicKey);
        Assert.Equal("我的工作站", loaded.Identity.DisplayName);

        // 破坏 identity.bin：应重置身份并保留显示名称
        File.WriteAllBytes(idPath, [1, 2, 3, 4, 5]);
        var store3 = new DpapiIdentityStore(idPath, settings, rng);
        var reset = Assert.IsType<IdentityLoad.ResetCorrupted>(store3.LoadOrCreate());
        Assert.NotEqual(created.Identity.DeviceId, reset.Identity.DeviceId);
        Assert.Equal("我的工作站", reset.Identity.DisplayName);
    }

    [Fact]
    public void JsonSettingsStore_persists_theme_and_ignores_unknown_keys()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, """{"unknown_key": 123, "auto_copy": true, "theme_mode": "dark"}""");
        var store = new JsonSettingsStore(path);
        Assert.True(store.Current().AutoCopy);
        Assert.Equal(ThemeMode.Dark, store.Theme.Value);

        Assert.IsType<StoreResult.Ok>(store.SetTheme(ThemeMode.Light));
        var reloaded = new JsonSettingsStore(path);
        Assert.Equal(ThemeMode.Light, reloaded.Theme.Value);
    }

    [Fact]
    public void FileSystemReceiveStorage_commits_without_overwriting_and_handles_case_insensitive_collision()
    {
        var recvDir = Path.Combine(_tempDir, "recv");
        Directory.CreateDirectory(recvDir);
        File.WriteAllText(Path.Combine(recvDir, "Report.TXT"), "existing");

        var storage = new FileSystemReceiveStorage(() => [new DirRef.Path(recvDir)]);
        var dirRef = new DirRef.Path(recvDir);
        var tid = new TransferId(Bytes16.Of(Enumerable.Repeat((byte)0x33, 16).ToArray()));
        var temp = storage.CreateTemp(dirRef, tid, 0);
        var w = Assert.IsType<WriteResult<ITempWriter>.Ok>(storage.OpenAppend(temp, 0)).Value;
        Assert.IsType<IoResult.Ok>(w.Write("new-content"u8));
        Assert.IsType<IoResult.Ok>(w.FlushSync());
        w.Close();

        var committed = Assert.IsType<CommitResult.Ok>(storage.Commit(temp, "report.txt"));
        Assert.Equal("report (1).txt", committed.FinalName);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(recvDir, "Report.TXT")));
        Assert.Equal("new-content", File.ReadAllText(committed.FullPath));
    }
}
