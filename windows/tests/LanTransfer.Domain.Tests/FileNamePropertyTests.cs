using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>Property 17–19 的 C# 实现，与 Kotlin 侧 FileNamePropertiesTest 对应。</summary>
public sealed class FileNamePropertyTests
{
    private const string Illegal = "\\/:*?\"<>|";

    private static readonly Gen<FsProfile> ProfileGen = Gen.Elements(FsProfile.Android, FsProfile.Windows, FsProfile.Ios);

    /// <summary>Property 17 的输出约束。</summary>
    private static bool SatisfiesConstraints(string r, FsProfile profile) =>
        r.Length > 0 && r is not "." and not ".." && !Gens.HasUnpairedSurrogate(r) &&
        !r.Any(c => Illegal.Contains(c, StringComparison.Ordinal) || c <= 0x1F || c == 0x7F) && NameText.Fits(r, profile) &&
        (profile != FsProfile.Windows || (!r.EndsWith(' ') && !r.EndsWith('.') && !NameText.IsReserved(r)));

    private static readonly Gen<string> Alnum =
        Gen.Choose(1, 12).SelectMany(n => Gen.Elements("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray()).ArrayOf(n)).Select(c => new string(c));

    /// <summary>不含分隔符、非法字符与 Unpaired_Surrogate 的名称片段。</summary>
    private static readonly Gen<string> PlainPart = Gen.Frequency((3, Alnum), (2, Gen.Elements("报告", "照片", "😀", "é", "数据 2025", "a b", "x.y")));

    // Feature: lan-file-transfer, Property 17: 文件名处理的输出约束与幂等
    // Validates: Requirements 16.4, 16.5, 16.6, 16.7, 16.8, 16.9, 16.10
    [PbtProperty(1000)]
    public Property Property17_SanitizeOutputAndIdempotence() =>
        Prop.ForAll(
            Arb.From(from raw in Gens.RawFileNameGen() from p in ProfileGen from now in Gens.LocalDateTimeGen() from now2 in Gens.LocalDateTimeGen() select (raw, p, now, now2)),
            x =>
            {
                var r = FileNameSanitizer.Sanitize(x.raw, x.p, x.now);
                Assert.True(SatisfiesConstraints(r, x.p), Gens.Escape(r));
                Assert.Equal(r, FileNameSanitizer.Sanitize(r, x.p, x.now2));
            });

    // Feature: lan-file-transfer, Property 18: 合法名称保留、去除路径、截断保留扩展名
    // Validates: Requirements 16.4, 16.5, 16.8
    [PbtProperty(1000)]
    public Property Property18_ValidNamesKeptPathsStrippedExtensionsPreserved()
    {
        var ext = Gen.Choose(1, 31).SelectMany(n => Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray()).ArrayOf(n)).Select(c => "." + new string(c));
        var longStem = from parts in Gen.Choose(1, 8).SelectMany(n => PlainPart.ArrayOf(n)) from n in Gen.Choose(20, 120) select "x" + string.Concat(Enumerable.Repeat(string.Concat(parts), n));
        var gen =
            from candidate in Gens.RawFileNameGen()
            from p in ProfileGen
            from now in Gens.LocalDateTimeGen()
            from prefix in Gens.RawFileNameGen()
            from x in PlainPart
            from stem in longStem
            from e in ext
            select (candidate, p, now, prefix, x, stem, e);
        return Prop.ForAll(Arb.From(gen), t =>
        {
            // 已满足输出约束的名称原样保留
            if (SatisfiesConstraints(t.candidate, t.p))
            {
                Assert.Equal(t.candidate, FileNameSanitizer.Sanitize(t.candidate, t.p, t.now));
            }

            // 去除路径：任意前缀加分隔符不影响结果
            var expected = FileNameSanitizer.Sanitize(t.x, t.p, t.now);
            Assert.Equal(expected, FileNameSanitizer.Sanitize(t.prefix + "/" + t.x, t.p, t.now));
            Assert.Equal(expected, FileNameSanitizer.Sanitize(t.prefix + "\\" + t.x, t.p, t.now));

            // 超长名称：扩展名不变，主干为原主干按码点的前缀
            var name = t.stem + t.e;
            if (!NameText.Fits(name, t.p))
            {
                var r = FileNameSanitizer.Sanitize(name, t.p, t.now);
                var i = NameText.ExtensionStart(r);
                Assert.Equal(t.e, r[i..]);
                Assert.StartsWith(r[..i], t.stem, StringComparison.Ordinal);
                Assert.False(Gens.HasUnpairedSurrogate(r));
            }
        });
    }

    // Feature: lan-file-transfer, Property 19: 重名取最小序号
    // Validates: Requirements 16.11
    [PbtProperty(1000)]
    public Property Property19_CollisionTakesSmallestIndex()
    {
        var gen =
            from raw in Gens.RawFileNameGen()
            from p in ProfileGen
            from now in Gens.LocalDateTimeGen()
            from taken in Gen.Choose(0, 6)
            from k in Gen.Choose(0, 4)
            from others in Gens.RawFileNameGen().ArrayOf(k)
            from nameMode in Gen.Choose(0, 3)
            select (raw, p, now, taken, others, nameMode);
        return Prop.ForAll(Arb.From(gen), t =>
        {
            var name = FileNameSanitizer.Sanitize(t.raw, t.p, t.now);
            var e = NameText.ExtensionStart(name);
            var stem = name[..e];
            var ext = name[e..];
            var existing = new HashSet<string>();
            if (t.nameMode != 0)
            {
                existing.Add(name);
            }

            for (var n = 1; n <= t.taken; n++)
            {
                existing.Add(NameCollisionResolver.Candidate(stem, ext, n, t.p));
            }

            foreach (var o in t.others)
            {
                existing.Add(FileNameSanitizer.Sanitize(o, t.p, t.now));
            }

            var comparer = t.p == FsProfile.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var keys = new HashSet<string>(existing, comparer);
            var r = NameCollisionResolver.Resolve(name, t.p, keys.Contains);
            Assert.DoesNotContain(r, keys);
            Assert.True(NameText.Fits(r, t.p));
            if (!keys.Contains(name))
            {
                Assert.Equal(name, r);
            }
            else
            {
                var idx = Enumerable.Range(1, t.taken + t.others.Length + 1).First(n => NameCollisionResolver.Candidate(stem, ext, n, t.p) == r);
                for (var j = 1; j < idx; j++)
                {
                    Assert.Contains(NameCollisionResolver.Candidate(stem, ext, j, t.p), keys);
                }
            }
        });
    }
}
