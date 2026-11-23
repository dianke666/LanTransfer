using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>Property 29、9 的 C# 实现与示例测试，与 Kotlin 侧 DeviceListPropertiesTest 对应。</summary>
public sealed class DeviceListPropertyTests
{
    private static List<LocalInterface> ToIfaces(IReadOnlyList<InterfaceSpec> specs) =>
        specs.Select(s => new LocalInterface(s.Name, VectorJson.EnumOf<IfaceKind>(s.Kind), new Ipv4(s.Address), s.PrefixLength)).ToList();

    private static bool InSubnet(Ipv4 t, LocalInterface i) => ((t.Bits ^ i.Address.Bits) & AddressRules.SubnetMask(i.PrefixLength)) == 0;

    private static bool RefAccepts(Ipv4 src, LocalInterface? via, IReadOnlyList<LocalInterface> ifaces) =>
        AddressRules.IsPrivate(src) && (via is not null ? InSubnet(src, via) : ifaces.Any(i => InSubnet(src, i)));

    /// <summary>来源地址：偏向各接口子网内的地址。</summary>
    private static Gen<Ipv4> SourceGen(IReadOnlyList<InterfaceSpec> specs) =>
        (specs.Count == 0
            ? Gens.Ipv4Gen()
            : Gen.Frequency((1, Gens.Ipv4Gen()), (3, Gen.Elements(specs.ToArray()).SelectMany(s => Gens.Ipv4InSubnet(s.Address, s.PrefixLength)))))
        .Select(b => new Ipv4(b));

    private static Gen<LocalInterface?> ViaGen(List<LocalInterface> ifaces) => Gen.Elements(new LocalInterface?[] { null }.Concat(ifaces).ToArray());

    private static readonly Gen<long> DtGen = Gen.Frequency(
        (3, Gens.Choose64(0, 2_000)),
        (1, Gens.Choose64(9_000, 11_000)),
        (1, Gen.Elements(0L, 1_000L, 10_000L)));

    private abstract record ListOp;

    private sealed record AnnounceOp(int IdIndex, Announcement A, Ipv4 Src, LocalInterface? Via, long Dt) : ListOp;

    private sealed record TickOp(long Dt) : ListOp;

    private sealed record TrustOp(int Mask) : ListOp;

    /// <summary>设备 ID 取自 4 个的小池子（第 0 个是本机），使同一 ID 的公告反复出现、字段不断变化。</summary>
    private static readonly Gen<(List<LocalInterface> Ifaces, DeviceId[] Ids, ListOp[] Ops)> ListCase =
        Gens.InterfaceSetGen().SelectMany(specs =>
        {
            var ifaces = ToIfaces(specs);
            var op = Gen.Frequency(
                (5, from i in Gen.Choose(0, 3) from a in Gens.AnnouncementGen() from s in SourceGen(specs) from v in ViaGen(ifaces) from dt in DtGen
                    select (ListOp)new AnnounceOp(i, a, s, v, dt)),
                (3, DtGen.Select(dt => (ListOp)new TickOp(dt))),
                (1, Gen.Choose(0, 15).Select(m => (ListOp)new TrustOp(m))));
            return from ids in Gens.DeviceIdGen().ArrayOf(4)
                   from n in Gen.Choose(1, 60)
                   from ops in op.ArrayOf(n)
                   select (ifaces, ids, ops);
        });

    /// <summary>已有若干项的设备列表 + 一个非法数据报（一半取自列表中某项的公告，同一设备 ID）。</summary>
    private static readonly Gen<(DeviceListState State, InvalidAnnouncement Bad, Ipv4 Src, LocalInterface? Via, long Now)> DropCase =
        Gens.InterfaceSetGen().SelectMany(specs =>
        {
            var ifaces = ToIfaces(specs);
            var item = from a in Gens.AnnouncementGen() from s in SourceGen(specs) from t in Gens.Choose64(0, 20_000) select new DeviceEntry(a, s, t, false);
            var stateGen =
                from self in Gens.DeviceIdGen()
                from n in Gen.Choose(0, 5)
                from items in item.ArrayOf(n)
                select new DeviceListState(self, new ValueSet<LocalInterface>(ifaces), ValueSet<DeviceId>.Empty, new ValueList<DeviceEntry>(items.DistinctBy(e => e.DeviceId)));
            return stateGen.SelectMany(state =>
            {
                var @base = state.Entries.Count == 0
                    ? Gens.AnnouncementGen()
                    : Gen.OneOf(Gens.AnnouncementGen(), Gen.Elements(state.Entries.Select(e => e.Announcement).ToArray()));
                return from bad in Gens.InvalidAnnouncementGen(@base)
                       from src in SourceGen(specs)
                       from via in ViaGen(ifaces)
                       from now in Gens.Choose64(20_000, 40_000)
                       select (state, bad, src, via, now);
            });
        });

    // Feature: lan-file-transfer, Property 29: 设备列表归约
    // Validates: Requirements 5.2, 5.3, 5.4, 5.5, 5.8
    [PbtProperty(1000)]
    public Property Property29_DeviceListReduction() =>
        Prop.ForAll(Arb.From(ListCase), x =>
        {
            var self = x.Ids[0];
            var s = new DeviceListState(self, x.Ifaces);
            var model = new List<DeviceEntry>();
            var trusted = ValueSet<DeviceId>.Empty;
            var now = 0L;
            foreach (var op in x.Ops)
            {
                switch (op)
                {
                    case AnnounceOp an:
                    {
                        now += an.Dt;
                        var a = an.A with { DeviceId = x.Ids[an.IdIndex] };
                        var before = s;
                        s = DeviceListReducer.Reduce(s, new DeviceListEvent.Announced(a, an.Src, an.Via, now));
                        if (a.DeviceId == self || !RefAccepts(an.Src, an.Via, x.Ifaces))
                        {
                            Assert.Equal(before, s);
                        }
                        else
                        {
                            var e = new DeviceEntry(a, an.Src, now, trusted.Contains(a.DeviceId));
                            var i = model.FindIndex(m => m.DeviceId == a.DeviceId);
                            if (i < 0)
                            {
                                model.Add(e);
                            }
                            else
                            {
                                model[i] = e;
                            }
                        }

                        break;
                    }

                    case TickOp tick:
                        now += tick.Dt;
                        s = DeviceListReducer.Reduce(s, new DeviceListEvent.Tick(now));
                        model.RemoveAll(m => m.LastSeenMono < now - 10_000);
                        Assert.All(s.Entries, e => Assert.True(e.LastSeenMono >= now - 10_000));
                        break;
                    case TrustOp trust:
                        trusted = new ValueSet<DeviceId>(x.Ids.Where((_, k) => ((trust.Mask >> k) & 1) == 1));
                        s = DeviceListReducer.Reduce(s, new DeviceListEvent.TrustChanged(trusted));
                        for (var k = 0; k < model.Count; k++)
                        {
                            model[k] = model[k] with { Trusted = trusted.Contains(model[k].DeviceId) };
                        }

                        break;
                }

                Assert.Equal(model, s.Entries);
                Assert.Equal(s.Entries.Count, s.Entries.Select(e => e.DeviceId).Distinct().Count());
            }
        });

    // Feature: lan-file-transfer, Property 9: 非法公告被丢弃
    // Validates: Requirements 23.2
    [PbtProperty(1000)]
    public Property Property09_InvalidAnnouncementsAreDropped() =>
        Prop.ForAll(Arb.From(DropCase), c =>
        {
            Assert.Null(AnnouncementCodec.Decode(c.Bad.Bytes));
            var after = DeviceListReducer.OnDatagram(c.State, c.Bad.Bytes, c.Src, c.Via, c.Now);
            Assert.Equal(c.State, after);

            // 过期计时不变：之后的 Tick 与未收到该数据报时结果相同
            Assert.Equal(DeviceListReducer.Reduce(c.State, new DeviceListEvent.Tick(c.Now)), DeviceListReducer.Reduce(after, new DeviceListEvent.Tick(c.Now)));
        });

    [Fact]
    public void InvalidAnnouncementGen_CoversEveryKind()
    {
        var kinds = Gens.InvalidAnnouncementGen().Draw(1000).Select(b => b.Kind).ToHashSet();
        Assert.Equal(InvalidAnnouncement.Kinds.OrderBy(k => k, StringComparer.Ordinal), kinds.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Examples_PipelineAcceptsValidAndIgnoresSelfAndNonLocal()
    {
        var wifi = new LocalInterface("wlan0", IfaceKind.Wifi, Ipv4.Of(192, 168, 1, 5), 24);
        var self = new DeviceId(Bytes16.OfHex(new string('0', 32)));
        var peer = new Announcement(new DeviceId(Bytes16.OfHex(string.Concat(Enumerable.Repeat("11", 16)))), "Pixel 9", DeviceType.Android, ProtocolVersion.V1_0, 42580);
        var s = new DeviceListState(self, [wifi]);
        s = DeviceListReducer.OnDatagram(s, AnnouncementCodec.Encode(peer), Ipv4.Of(192, 168, 1, 9), wifi, 1_000);
        Assert.Equal(new[] { new DeviceEntry(peer, Ipv4.Of(192, 168, 1, 9), 1_000, false) }, s.Entries);
        Assert.Equal(s, DeviceListReducer.OnDatagram(s, AnnouncementCodec.Encode(peer with { DeviceId = self }), Ipv4.Of(192, 168, 1, 9), wifi, 2_000));
        Assert.Equal(s, DeviceListReducer.OnDatagram(s, AnnouncementCodec.Encode(peer), Ipv4.Of(10, 0, 0, 9), wifi, 2_000));
        var renamed = peer with { DisplayName = "小明的手机", Port = 42590 };
        s = DeviceListReducer.OnDatagram(s, AnnouncementCodec.Encode(renamed), Ipv4.Of(192, 168, 1, 10), wifi, 5_000);
        Assert.Equal(new[] { new DeviceEntry(renamed, Ipv4.Of(192, 168, 1, 10), 5_000, false) }, s.Entries);
        Assert.Single(DeviceListReducer.Reduce(s, new DeviceListEvent.Tick(15_000)).Entries);
        Assert.Empty(DeviceListReducer.Reduce(s, new DeviceListEvent.Tick(15_001)).Entries);
        Assert.True(DeviceListReducer.Reduce(s, new DeviceListEvent.TrustChanged(new ValueSet<DeviceId>([peer.DeviceId]))).Entries.Single().Trusted);
    }
}
