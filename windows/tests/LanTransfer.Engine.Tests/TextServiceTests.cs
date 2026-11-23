using System.Text.RegularExpressions;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>
/// TextService 的单元测试（任务 17.4，与 Kotlin 侧 TextServiceTest 对应）。C# 的服务与连接都在线程池上运行：
/// 测试等待可观察的结果（WaitUntilAsync），确认“没有发生”时短暂等待（Settle）；长时间推进 FakeClock 时按小步推进。
/// </summary>
public sealed class TextServiceTests
{
    private static MessageId SentId(SendCheck r) => Assert.IsType<SendCheck.Sent>(r).Id;

    private static SourceRef OkRef(WriteResult<SourceRef> r) => Assert.IsType<WriteResult<SourceRef>.Ok>(r).Value;

    private static byte[] Utf8(string s) => TextKit.Utf8(s);

    // ---- 发送（11.3–11.7、11.11、12.12） ----

    [Fact]
    public async Task Send_ShowsSendingImmediately_ThenDeliveredAfterPeerSaves()
    {
        var w = new TextWorld();
        var statuses = new List<TextStatus>();
        using var sub = w.A.Service.Conversation(w.B.D.Id).Subscribe(items =>
        {
            lock (statuses)
            {
                if (items.Count == 1)
                {
                    statuses.Add(items[0].Status);
                }
            }
        });
        const string body = "你好\r\n世界\0\t 👍🏽 ";
        var id = SentId(await w.A.Service.SendAsync(w.B.D.Id, body));
        Assert.Single(w.A.Items(w.B.D));
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Delivered);
        lock (statuses)
        {
            Assert.Equal(TextStatus.Sending, statuses[0]);
        }

        Assert.False(w.A.Items(w.B.D).Single().ShowRetry);
        await Kit.WaitUntilAsync(() => w.B.Items(w.A.D).Count == 1, "B 显示");
        var got = w.B.Items(w.A.D).Single();
        Assert.Equal(body, got.Body);
        Assert.Equal(new TextKey(w.A.D.Id, id), got.Key);
        Assert.Equal(Direction.Receive, got.Direction);
        Assert.Equal(TextStatus.Delivered, w.A.History.FindText(w.B.D.Id, id)!.Status);
        var rb = w.B.History.FindText(w.A.D.Id, id)!;
        Assert.Equal(TextStatus.Received, rb.Status);
        Assert.Equal(Bytes.Of(Utf8(body)), rb.TextUtf8);
        Assert.Equal("A", rb.PeerName);
        Assert.Equal(480, rb.RecordedAt.OffsetMin);
    }

    [Fact]
    public async Task TenSecondThreshold_FailsAtExactly10000ms_AndLateAckStillDelivers()
    {
        var w = new TextWorld(bDispatch: false);
        var t0 = w.Clock.MonotonicMs();
        var id = SentId(await w.A.Service.SendAsync(w.B.D.Id, "hello"));
        var sb = await w.SessionBAAsync();
        Assert.Equal(id, Assert.IsType<Text>(await TextKit.NextAsync(sb)).MessageId);
        await TextKit.StepToAsync(w.Clock, t0 + 9_999);
        await Kit.Settle();
        Assert.Equal(TextStatus.Sending, w.A.StatusOf(w.B.D, id));
        await TextKit.StepToAsync(w.Clock, t0 + 10_000);
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Failed);
        Assert.True(w.A.Items(w.B.D).Single().ShowRetry);
        Assert.Equal(TextStatus.Failed, w.A.History.FindText(w.B.D.Id, id)!.Status);
        sb.Send(new TextAck(id));
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Delivered);
        Assert.Equal(TextStatus.Delivered, w.A.History.FindText(w.B.D.Id, id)!.Status);
    }

    [Fact]
    public async Task NoSession_AlsoFailsAt10s_AndReportsConnectFailure()
    {
        var w = new TextWorld();
        var stranger = Kit.DeviceIdOf(9);
        var t0 = w.Clock.MonotonicMs();
        var id = SentId(await w.A.Service.SendAsync(stranger, "x"));
        await Kit.WaitUntilAsync(() => w.A.Notices.Contains(new TextNotice.ConnectFailed(stranger, new OpenFailure.Unreachable())), "ConnectFailed");
        await TextKit.StepToAsync(w.Clock, t0 + 9_999);
        await Kit.Settle();
        Assert.Equal(TextStatus.Sending, w.A.Service.Conversation(stranger).Value.Single().Status);
        await TextKit.StepToAsync(w.Clock, t0 + 10_000);
        await Kit.WaitUntilAsync(() => w.A.Service.Conversation(stranger).Value.Single().Status == TextStatus.Failed, "Failed");
        Assert.Equal(TextStatus.Failed, w.A.History.FindText(stranger, id)!.Status);
    }

    [Fact]
    public async Task Retry_SameIdTextAndSentAt_BackToSendingAndRetimed_SingleRecord()
    {
        var w = new TextWorld(bDispatch: false);
        var t0 = w.Clock.MonotonicMs();
        var id = SentId(await w.A.Service.SendAsync(w.B.D.Id, "再试一次"));
        var sb = await w.SessionBAAsync();
        var first = Assert.IsType<Text>(await TextKit.NextAsync(sb));
        await TextKit.StepToAsync(w.Clock, t0 + 10_000);
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Failed);
        await TextKit.StepToAsync(w.Clock, t0 + 13_000);
        w.A.Service.Retry(id);
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Sending);
        Assert.Equal(TextStatus.Sending, w.A.History.FindText(w.B.D.Id, id)!.Status);
        Assert.Equal(first, await TextKit.NextAsync(sb));
        await TextKit.StepToAsync(w.Clock, t0 + 22_999);
        await Kit.Settle();
        Assert.Equal(TextStatus.Sending, w.A.StatusOf(w.B.D, id));
        await TextKit.StepToAsync(w.Clock, t0 + 23_000);
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Failed);
        Assert.Single(w.A.TextRecords());
        Assert.Single(w.A.Items(w.B.D));
    }

    [Fact]
    public async Task TextCorrupt_FailsImmediately_OnlyWhileSending()
    {
        var w = new TextWorld(bDispatch: false);
        var id = SentId(await w.A.Service.SendAsync(w.B.D.Id, "bad?"));
        var sb = await w.SessionBAAsync();
        await TextKit.NextAsync(sb);
        sb.Send(new TextCorrupt(id));
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Failed);
        sb.Send(new TextAck(id));
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Delivered);
        sb.Send(new TextCorrupt(id));
        sb.Send(new TextAck(TextKit.MessageIdOf(99)));
        await Kit.Settle();
        Assert.Equal(TextStatus.Delivered, w.A.StatusOf(w.B.D, id));
    }

    [Fact]
    public async Task LengthBoundary_1048576BytesSent_1048577NotSent()
    {
        var w = new TextWorld();
        var max = "a" + string.Concat(Enumerable.Repeat("中", 349_525));
        Assert.Equal(1_048_576, StrictUtf8.EncodedLength(max));
        var id = SentId(await w.A.Service.SendAsync(w.B.D.Id, max));
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Delivered);
        await Kit.WaitUntilAsync(() => w.B.Items(w.A.D).Count == 1, "B 显示");
        Assert.Equal(max, w.B.Items(w.A.D).Single().Body);
        Assert.Equal(new SendCheck.TooLong(1_048_577), await w.A.Service.SendAsync(w.B.D.Id, max + "a"));
        Assert.Equal(new SendCheck.TooLong(1_048_577), await w.A.Service.SendAsync(w.B.D.Id, string.Concat(Enumerable.Repeat("中", 349_525)) + "ab"));
        Assert.Equal(new SendCheck.Empty(), await w.A.Service.SendAsync(w.B.D.Id, string.Empty));
        Assert.Equal(new SendCheck.HasUnpairedSurrogate(), await w.A.Service.SendAsync(w.B.D.Id, "x\uD800y"));
        await Kit.Settle();
        Assert.Single(w.A.Items(w.B.D));
        Assert.Single(w.A.TextRecords());
        Assert.Single(w.B.Items(w.A.D));
    }

    [Fact]
    public async Task SendStoreFailure_StillSends_StatusOnlyInMemory()
    {
        var w = new TextWorld();
        w.A.Db.FailWrites = _ => true;
        var id = SentId(await w.A.Service.SendAsync(w.B.D.Id, "仍然发送"));
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Delivered);
        Assert.Null(w.A.History.FindText(w.B.D.Id, id));
        Assert.Contains(w.A.Notices, n => n is TextNotice.StoreFailed);
        await Kit.WaitUntilAsync(() => w.B.Items(w.A.D).Count == 1, "B 显示");
        Assert.Equal("仍然发送", w.B.Items(w.A.D).Single().Body);
    }

    // ---- 接收（11.4、11.8、11.12、13.8、20.8、21.6） ----

    [Fact]
    public async Task AutoCopy_SerialInArrivalOrder_LastStays_DuplicatesAndOffNotWritten()
    {
        var w = new TextWorld(aDispatch: false);
        w.B.Settings.Update(x => x with { AutoCopy = true });
        var s = await w.OpenABAsync();
        s.Send(TextKit.RawText(1, Utf8("一")));
        s.Send(TextKit.RawText(2, Utf8("二\r\n")));
        s.Send(TextKit.RawText(3, Utf8(" 三 ")));
        s.Send(TextKit.RawText(1, Utf8("一")));
        var replies = new List<ProtocolMessage>();
        for (var i = 0; i < 4; i++)
        {
            replies.Add(await TextKit.NextAsync(s));
        }

        Assert.Equal([new TextAck(TextKit.MessageIdOf(1)), new TextAck(TextKit.MessageIdOf(2)), new TextAck(TextKit.MessageIdOf(3)), new TextAck(TextKit.MessageIdOf(1))], replies);
        await Kit.WaitUntilAsync(() => w.B.Clipboard.Writes.Count == 3, "剪贴板");
        Assert.Equal(["一", "二\r\n", " 三 "], w.B.Clipboard.Writes);
        Assert.Equal(" 三 ", w.B.Clipboard.Content);
        w.B.Settings.Update(x => x with { AutoCopy = false });
        s.Send(TextKit.RawText(4, Utf8("四")));
        await TextKit.NextAsync(s);
        await Kit.Settle();
        Assert.Equal(3, w.B.Clipboard.Writes.Count);
        Assert.Equal(["一", "二\r\n", " 三 ", "四"], w.B.Items(w.A.D).Select(i => i.Body));
    }

    [Fact]
    public async Task AutoCopyFailure_IsReported()
    {
        var w = new TextWorld(aDispatch: false);
        w.B.Settings.Update(x => x with { AutoCopy = true });
        w.B.Clipboard.FailWrites = true;
        (await w.OpenABAsync()).Send(TextKit.RawText(1, Utf8("x")));
        await Kit.WaitUntilAsync(() => w.B.Notices.Contains(new TextNotice.AutoCopyFailed()), "AutoCopyFailed");
    }

    [Fact]
    public async Task Background_NotifiesOncePerNewMessage_PreviewFirst100CodePoints()
    {
        var w = new TextWorld(aDispatch: false);
        var s = await w.OpenABAsync();
        s.Send(TextKit.RawText(1, Utf8("前台")));
        await TextKit.NextAsync(s);
        Assert.Empty(w.B.Notifier.Events);
        w.B.Lifecycle.SetBackground(true);
        var longText = string.Concat(Enumerable.Repeat("𠀀", 101));
        s.Send(TextKit.RawText(2, Utf8(longText)));
        s.Send(TextKit.RawText(2, Utf8(longText)));
        s.Send(TextKit.RawText(1, Utf8("前台")));
        for (var i = 0; i < 3; i++)
        {
            await TextKit.NextAsync(s);
        }

        Assert.Equal(
            [new NotifyEvent.Text(new NotifyKey.Text(w.A.D.Id, TextKit.MessageIdOf(2)), "A", string.Concat(Enumerable.Repeat("𠀀", 100)) + "…")],
            w.B.Notifier.Events);
        Assert.Equal(longText, w.B.Service.OriginalText(w.A.D.Id, TextKit.MessageIdOf(2)));
    }

    [Fact]
    public async Task ReceiveStoreFailure_NoAckButShownOnce_RetryAfterRecoveryAcked()
    {
        var w = new TextWorld(aDispatch: false);
        w.B.Settings.Update(x => x with { AutoCopy = true });
        w.B.Lifecycle.SetBackground(true);
        w.B.Db.FailWrites = op => op == "appendText";
        var s = await w.OpenABAsync();
        var m = TextKit.RawText(1, Utf8("存不下"));
        s.Send(m);
        await Kit.WaitUntilAsync(() => w.B.Items(w.A.D).Count == 1, "B 显示");
        await Kit.Settle();
        Assert.False(s.Incoming.TryRead(out _));
        Assert.Equal("存不下", w.B.Items(w.A.D).Single().Body);
        Assert.Single(w.B.Notices.OfType<TextNotice.StoreFailed>());
        Assert.Equal("存不下", w.B.Service.OriginalText(w.A.D.Id, m.MessageId));
        s.Send(m);
        await Kit.WaitUntilAsync(() => w.B.Notices.OfType<TextNotice.StoreFailed>().Count() == 2, "第二次写入失败");
        await Kit.Settle();
        Assert.False(s.Incoming.TryRead(out _));
        w.B.Db.FailWrites = _ => false;
        s.Send(m);
        Assert.Equal(new TextAck(m.MessageId), await TextKit.NextAsync(s));
        Assert.NotNull(w.B.History.FindText(w.A.D.Id, m.MessageId));
        s.Send(m);
        Assert.Equal(new TextAck(m.MessageId), await TextKit.NextAsync(s));
        await Kit.Settle();
        Assert.Single(w.B.Items(w.A.D));
        Assert.Single(w.B.Notifier.Events);
        Assert.Equal(["存不下"], w.B.Clipboard.Writes);
    }

    [Fact]
    public async Task InvalidUtf8_OnlyTextCorrupt_NoOtherEffects_ThenSameIdValidIsNew()
    {
        var w = new TextWorld(aDispatch: false);
        w.B.Settings.Update(x => x with { AutoCopy = true });
        w.B.Lifecycle.SetBackground(true);
        var s = await w.OpenABAsync();
        s.Send(TextKit.RawText(1, [0x61, 0xC0, 0x80]));
        Assert.Equal(new TextCorrupt(TextKit.MessageIdOf(1)), await TextKit.NextAsync(s));
        await Kit.Settle();
        Assert.Empty(w.B.Items(w.A.D));
        Assert.Empty(w.B.TextRecords());
        Assert.Empty(w.B.Notifier.Events);
        Assert.Empty(w.B.Clipboard.Writes);
        Assert.True(s.IsOpen);
        s.Send(TextKit.RawText(1, Utf8("ok")));
        Assert.Equal(new TextAck(TextKit.MessageIdOf(1)), await TextKit.NextAsync(s));
        await Kit.WaitUntilAsync(() => w.B.Items(w.A.D).Count == 1, "B 显示");
        Assert.Equal("ok", w.B.Items(w.A.D).Single().Body);
    }

    [Fact]
    public async Task AlreadyInHistory_OnlyAcked_NotShown()
    {
        var w = new TextWorld(aDispatch: false);
        var old = new TextRecord(0, w.A.D.Id, "A", TextKit.MessageIdOf(7), Direction.Receive, Bytes.Of(Utf8("旧")), 1, new RecordTime(1, 480), TextStatus.Received);
        w.B.History.AppendText(old);
        var s = await w.OpenABAsync();
        s.Send(TextKit.RawText(7, Utf8("旧")));
        Assert.Equal(new TextAck(TextKit.MessageIdOf(7)), await TextKit.NextAsync(s));
        await Kit.Settle();
        Assert.Empty(w.B.Items(w.A.D));
        Assert.Single(w.B.TextRecords());
    }

    // ---- 以 .txt 文件发送（11.13） ----

    [Fact]
    public void TextFile_BytesEqualUtf8_NoBom_NameFormat_NoCollisionWithinSecond()
    {
        var w = new TextWorld();
        const string text = "第一行\r\n第二行\n\0\t末尾 ";
        var r1 = OkRef(w.A.Service.WriteTextFile(text));
        var r2 = OkRef(w.A.Service.WriteTextFile(text));
        Assert.NotEqual(r1, r2);
        Assert.Equal(2, w.A.Cache.DirNames.Count);
        foreach (var r in new[] { r1, r2 })
        {
            var e = w.A.Sources.EntryOf(r)!;
            Assert.Equal("文字_20251010_005320.txt", e.Name);
            Assert.Matches(new Regex(@"^文字_\d{8}_\d{6}\.txt$"), e.Name);
            Assert.Equal(Bytes.Of(Utf8(text)), e.Data);
            Assert.Equal(0xE7, e.Data[0]); // “第”，前面没有 EF BB BF
            Assert.Equal(Utf8(text).Length, w.A.Sources.Describe(r)!.Size);
        }

        // 文字本身以 U+FEFF 开头时原样保留，不多也不少
        var r3 = OkRef(w.A.Service.WriteTextFile("\uFEFFabc"));
        Assert.Equal(Bytes.Of([0xEF, 0xBB, 0xBF, 0x61, 0x62, 0x63]), w.A.Sources.EntryOf(r3)!.Data);
        w.A.Cache.FailWrites = true;
        Assert.IsType<WriteResult<SourceRef>.Failed>(w.A.Service.WriteTextFile(text));
    }

    [Fact]
    public void TextFile_AnyText_ContentEqualsUtf8()
    {
        var w = new TextWorld();
        foreach (var fragments in Gens.UnicodeTextFragments().Draw(Pbt.Iterations(100)))
        {
            var text = string.Concat(fragments);
            var r = OkRef(w.A.Service.WriteTextFile(text));
            Assert.Equal(Bytes.Of(Utf8(text)), w.A.Sources.EntryOf(r)!.Data);
        }
    }

    // ---- 启动恢复与清空（M3.1、20.5） ----

    [Fact]
    public async Task Restore_SendingBecomesFailedAndRetriable_ClearIgnoresLateAck()
    {
        var w = new TextWorld();
        var id = TextKit.MessageIdOf(5);
        var sending = new TextRecord(0, w.B.D.Id, "B", id, Direction.Send, Bytes.Of(Utf8("上次没发完")), 1_000, new RecordTime(1_000, 480), TextStatus.Sending);
        w.A.History.AppendText(sending);
        w.A.History.AppendText(sending with { MessageId = TextKit.MessageIdOf(6), Status = TextStatus.Delivered });
        await w.A.Service.RestoreOnStartupAsync();
        Assert.Equal([TextStatus.Failed, TextStatus.Delivered], w.A.Items(w.B.D).Select(i => i.Status));
        Assert.Equal(TextStatus.Failed, w.A.History.FindText(w.B.D.Id, id)!.Status);
        w.A.Service.Retry(id);
        await w.A.WaitStatusAsync(w.B.D, id, TextStatus.Delivered);
        await Kit.WaitUntilAsync(() => w.B.Items(w.A.D).Count == 1, "B 显示");
        Assert.Equal("上次没发完", w.B.Items(w.A.D).Single().Body);
        Assert.Equal(1_000, w.B.Items(w.A.D).Single().SentAtUtcMs);

        w.A.History.ClearAll();
        await w.A.Service.ForgetAllAsync();
        Assert.Empty(w.A.Items(w.B.D));
        (await w.SessionBAAsync()).Send(new TextAck(id));
        await Kit.Settle();
        Assert.Empty(w.A.Items(w.B.D));
        Assert.Null(w.A.History.FindText(w.B.D.Id, id));
    }

    [Fact]
    public async Task Forget_RemovesOnlyGivenMessages()
    {
        var w = new TextWorld();
        var one = SentId(await w.A.Service.SendAsync(w.B.D.Id, "1"));
        var two = SentId(await w.A.Service.SendAsync(w.B.D.Id, "2"));
        await w.A.WaitStatusAsync(w.B.D, two, TextStatus.Delivered);
        await w.A.Service.ForgetAsync(new HashSet<TextKey> { new(w.B.D.Id, one) });
        Assert.Equal([two], w.A.Items(w.B.D).Select(i => i.Key.MessageId));
        await Kit.WaitUntilAsync(() => w.B.Items(w.A.D).Count == 2, "B 显示");
        Assert.Equal(["1", "2"], w.B.Items(w.A.D).Select(i => i.Body));
        Assert.Empty(w.A.Service.Conversation(Kit.DeviceIdOf(8)).Value);
    }
}
