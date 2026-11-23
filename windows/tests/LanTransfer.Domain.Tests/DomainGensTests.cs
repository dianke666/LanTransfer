using System.Text;
using FsCheck.Fluent;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>领域层共享生成器的自检：覆盖设计“共享生成器”列出的各类文件名与地址情形。</summary>
public sealed class DomainGensTests
{
    [Fact]
    public void RawFileName_covers_the_listed_cases()
    {
        var names = Gens.RawFileName().Generator.Draw(3000).Select(n => n.Value).ToArray();
        Assert.Contains(names, n => n.Length == 0);
        Assert.Contains(names, n => n is "." or "..");
        Assert.Contains(names, n => n.Contains('/', StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains('\\', StringComparison.Ordinal));
        Assert.Contains(names, n => n.Split('.')[0].TrimEnd().Equals("con", StringComparison.OrdinalIgnoreCase) && n != n.ToUpperInvariant());
        Assert.Contains(names, n => !Gens.HasUnpairedSurrogate(n) && Encoding.UTF8.GetByteCount(n) > 255);
        Assert.Contains(names, n => n.EndsWith(' ') || n.EndsWith('.'));
        Assert.Contains(names, n => n.Contains('\0', StringComparison.Ordinal));
        Assert.Contains(names, Gens.HasUnpairedSurrogate);
    }

    [Fact]
    public void Ipv4_covers_private_ranges_and_public_addresses()
    {
        var addresses = Gens.Ipv4().Generator.Draw(3000).Select(a => a.Value).ToArray();
        Assert.Contains(addresses, a => a >> 24 == 10);
        Assert.Contains(addresses, a => a >> 20 == ((172u << 4) | 1));
        Assert.Contains(addresses, a => a >> 16 == ((192u << 8) | 168));
        Assert.Contains(addresses, a => a == Gens.Ipv4Of(8, 8, 8, 8));
    }

    [Fact]
    public void Ipv4InSubnet_stays_inside_the_subnet()
    {
        var baseAddress = Gens.Ipv4Of(192, 168, 1, 77);
        Assert.All(Gens.Ipv4InSubnet(baseAddress, 24).Draw(500), a => Assert.Equal(baseAddress & Gens.SubnetMask(24), a & Gens.SubnetMask(24)));
        Assert.All(Gens.Ipv4InSubnet(baseAddress, 32).Draw(50), a => Assert.Equal(baseAddress, a));
    }

    [Fact]
    public void InterfaceSet_has_at_most_four_interfaces_and_overlapping_subnets()
    {
        var sets = Gens.InterfaceSet().Generator.Draw(2000).Select(s => s.Interfaces).ToArray();
        Assert.All(sets, set =>
        {
            Assert.InRange(set.Count, 0, 4);
            Assert.All(set, i => Assert.InRange(i.PrefixLength, 8, 32));
        });
        Assert.Contains(sets, set => set.Count == 0);
        Assert.Contains(sets, set => set.Any(a => set.Any(b =>
            !ReferenceEquals(a, b) && (a.Address & Gens.SubnetMask(b.PrefixLength)) == (b.Address & Gens.SubnetMask(b.PrefixLength)))));
    }

    [Fact]
    public void LocalDateTime_is_within_range_and_whole_seconds()
    {
        Assert.All(Gens.LocalDateTime().Generator.Draw(500), t =>
        {
            Assert.InRange(t.Year, 2000, 2099);
            Assert.Equal(0, t.Millisecond);
        });
    }

    [Fact]
    public void ToDottedQuad_formats_big_endian()
    {
        Assert.Equal("192.168.0.255", Gens.ToDottedQuad(Gens.Ipv4Of(192, 168, 0, 255)));
        Assert.Equal("255.255.255.255", Gens.ToDottedQuad(uint.MaxValue));
    }
}
