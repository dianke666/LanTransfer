using LanTransfer.Protocol;

namespace LanTransfer.Domain;

/// <summary>设备列表中的一项（需求 5.3）：显示名称、类型、最近一次有效公告的来源 IP、端口与信任标识。</summary>
public sealed record DeviceEntry(Announcement Announcement, Ipv4 Ip, long LastSeenMono, bool Trusted)
{
    public DeviceId DeviceId => Announcement.DeviceId;
}

/// <summary>设备列表状态：<see cref="Entries"/> 按首次出现的顺序排列，每个设备 ID 至多 1 项。</summary>
public sealed record DeviceListState(DeviceId SelfId, ValueSet<LocalInterface> Interfaces, ValueSet<DeviceId> Trusted, ValueList<DeviceEntry> Entries)
{
    public DeviceListState(DeviceId selfId, IEnumerable<LocalInterface> interfaces)
        : this(selfId, new ValueSet<LocalInterface>(interfaces), ValueSet<DeviceId>.Empty, ValueList<DeviceEntry>.Empty)
    {
    }
}

public abstract record DeviceListEvent
{
    public sealed record Announced(Announcement A, Ipv4 Src, LocalInterface? Via, long NowMono) : DeviceListEvent;

    /// <summary>每 1 s 一次。</summary>
    public sealed record Tick(long NowMono) : DeviceListEvent;

    public sealed record TrustChanged(ValueSet<DeviceId> Trusted) : DeviceListEvent;
}

/// <summary>设备列表归约（需求 5.2–5.5、5.8，设计 C3.4），与 Kotlin 侧 <c>DeviceListReducer</c> 对应。</summary>
public static class DeviceListReducer
{
    /// <summary>最近接受时间早于 now − 10 s 的项在 Tick 时移除（5.4）。</summary>
    public const long ExpiryMs = 10_000;

    public static DeviceListState Reduce(DeviceListState s, DeviceListEvent e) => e switch
    {
        DeviceListEvent.Announced a => OnAnnounced(s, a),
        DeviceListEvent.Tick t => s.Entries.All(x => x.LastSeenMono >= t.NowMono - ExpiryMs)
            ? s
            : s with { Entries = new ValueList<DeviceEntry>(s.Entries.Where(x => x.LastSeenMono >= t.NowMono - ExpiryMs)) },
        DeviceListEvent.TrustChanged c => s with
        {
            Trusted = c.Trusted,
            Entries = new ValueList<DeviceEntry>(s.Entries.Select(x => x with { Trusted = c.Trusted.Contains(x.DeviceId) })),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    private static DeviceListState OnAnnounced(DeviceListState s, DeviceListEvent.Announced e)
    {
        if (e.A.DeviceId == s.SelfId || !AddressRules.AcceptsInbound(e.Src, e.Via, s.Interfaces))
        {
            return s;
        }

        var entry = new DeviceEntry(e.A, e.Src, e.NowMono, s.Trusted.Contains(e.A.DeviceId));
        var list = s.Entries.ToList();
        var i = list.FindIndex(x => x.DeviceId == e.A.DeviceId);
        if (i < 0)
        {
            list.Add(entry);
        }
        else
        {
            list[i] = entry;
        }

        return s with { Entries = new ValueList<DeviceEntry>(list) };
    }

    /// <summary>发现管线：AcceptsInbound → AnnouncementCodec.Decode → Reduce；无效数据报不改变状态（含过期计时，23.2）。</summary>
    public static DeviceListState OnDatagram(DeviceListState s, byte[] datagram, Ipv4 src, LocalInterface? via, long nowMono)
    {
        if (!AddressRules.AcceptsInbound(src, via, s.Interfaces))
        {
            return s;
        }

        var a = AnnouncementCodec.Decode(datagram);
        return a is null ? s : Reduce(s, new DeviceListEvent.Announced(a, src, via, nowMono));
    }
}
