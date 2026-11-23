using LanTransfer.Protocol;

namespace LanTransfer.Domain;

/// <summary>一条发出的文字由（对方设备 ID, 消息 ID）确定。</summary>
public readonly record struct TextKey(DeviceId Peer, MessageId MessageId);

/// <summary>
/// 发出的文字及其发送状态（M3.1），与 Kotlin 侧 <c>OutgoingText</c> 对应（Kotlin 的 <c>text</c> 在这里是 <see cref="Body"/>，与 <c>Text.Body</c> 一致）。
/// Deadline 只在 Sending 时非空：从点击起计时 10 s（11.6、11.11）。
/// </summary>
public sealed record OutgoingText(TextKey Key, Bytes Body, long SentAtUtcMs, TextStatus Status, long? Deadline)
{
    /// <summary>Failed 时始终显示“重试”（11.6、11.7）。</summary>
    public bool ShowRetry => Status == TextStatus.Failed;
}

public sealed record TextState(ValueMap<TextKey, OutgoingText> Outgoing)
{
    public TextState()
        : this(ValueMap<TextKey, OutgoingText>.Empty)
    {
    }
}

public abstract record TextEvent
{
    /// <summary>发送前检查已通过、记录已写入 History_Store（C4.4）。</summary>
    public sealed record Send(TextKey Key, Bytes Body, long SentAtUtcMs, long Now) : TextEvent;

    public sealed record Retry(TextKey Key, long Now) : TextEvent;

    public sealed record AckReceived(TextKey Key) : TextEvent;

    public sealed record CorruptReceived(TextKey Key) : TextEvent;

    public sealed record Tick(long Now) : TextEvent;

    /// <summary>应用启动时从 History_Store 载入的发出记录：Sending 的计时已丢失，改为 Failed（M3.1）。</summary>
    public sealed record Restored(ValueList<TextRecord> Records) : TextEvent;

    /// <summary>记录被删除或清空（20.4、20.5）：之后迟到的应答不再影响任何记录。</summary>
    public sealed record Forget(ValueSet<TextKey> Keys) : TextEvent;
}

public abstract record TextEffect
{
    /// <summary>需要时打开 Session，以 P1 优先级入队 TEXT（C4.4）。</summary>
    public sealed record Transmit(DeviceId Peer, Text Msg) : TextEffect;

    public sealed record SaveStatus(TextKey Key, TextStatus Status) : TextEffect;

    public sealed record ArmTimer(long AtMono) : TextEffect;
}

/// <summary>文字发送状态机（M3.1，需求 11.5–11.7、12.12），与 Kotlin 侧 <c>TextReducer</c> 对应。Delivered 之后不再变化；未知消息的应答一律忽略。</summary>
public static class TextReducer
{
    public const long AckTimeoutMs = 10_000;

    public static Step<TextState, TextEffect> Reduce(TextState s, TextEvent e)
    {
        switch (e)
        {
            case TextEvent.Send x:
            {
                if (s.Outgoing.ContainsKey(x.Key))
                {
                    return Of(s);
                }

                var t = new OutgoingText(x.Key, x.Body, x.SentAtUtcMs, TextStatus.Sending, x.Now + AckTimeoutMs);
                return Of(With(s, t), Transmit(t), new TextEffect.ArmTimer(x.Now + AckTimeoutMs));
            }

            case TextEvent.Retry x:
            {
                if (!s.Outgoing.TryGetValue(x.Key, out var t) || t.Status != TextStatus.Failed)
                {
                    return Of(s);
                }

                var next = t with { Status = TextStatus.Sending, Deadline = x.Now + AckTimeoutMs };
                return Of(With(s, next), new TextEffect.SaveStatus(x.Key, TextStatus.Sending), Transmit(next), new TextEffect.ArmTimer(x.Now + AckTimeoutMs));
            }

            case TextEvent.AckReceived x:
                return Change(s, x.Key, TextStatus.Delivered, st => st is TextStatus.Sending or TextStatus.Failed);

            case TextEvent.CorruptReceived x:
                return Change(s, x.Key, TextStatus.Failed, st => st == TextStatus.Sending);

            case TextEvent.Tick x:
            {
                var expired = s.Outgoing.Values.Where(t => t.Status == TextStatus.Sending && t.Deadline is { } d && x.Now >= d).ToList();
                if (expired.Count == 0)
                {
                    return Of(s);
                }

                var map = s.Outgoing;
                foreach (var t in expired)
                {
                    map = map.SetItem(t.Key, t with { Status = TextStatus.Failed, Deadline = null });
                }

                return Of(new TextState(map), [.. expired.Select(t => new TextEffect.SaveStatus(t.Key, TextStatus.Failed))]);
            }

            case TextEvent.Restored x:
            {
                var map = s.Outgoing;
                var effects = new List<TextEffect>();
                foreach (var r in x.Records.Where(rec => rec.Direction == Direction.Send))
                {
                    var key = new TextKey(r.Peer, r.MessageId);
                    var status = r.Status == TextStatus.Sending ? TextStatus.Failed : r.Status;
                    if (status != r.Status)
                    {
                        effects.Add(new TextEffect.SaveStatus(key, status));
                    }

                    map = map.SetItem(key, new OutgoingText(key, r.TextUtf8, r.SentAtUtcMs, status, null));
                }

                return Of(new TextState(map), [.. effects]);
            }

            case TextEvent.Forget x:
                return Of(new TextState(s.Outgoing.RemoveRange(x.Keys)));

            default:
                throw new ArgumentOutOfRangeException(nameof(e));
        }
    }

    private static Step<TextState, TextEffect> Of(TextState s, params TextEffect[] effects) => new(s, new ValueList<TextEffect>(effects));

    private static TextState With(TextState s, OutgoingText t) => new(s.Outgoing.SetItem(t.Key, t));

    private static TextEffect.Transmit Transmit(OutgoingText t) => new(t.Key.Peer, new Text(t.Key.MessageId, t.SentAtUtcMs, t.Body));

    private static Step<TextState, TextEffect> Change(TextState s, TextKey key, TextStatus to, Func<TextStatus, bool> from)
    {
        if (!s.Outgoing.TryGetValue(key, out var t) || !from(t.Status))
        {
            return Of(s);
        }

        return Of(With(s, t with { Status = to, Deadline = null }), new TextEffect.SaveStatus(key, to));
    }
}
