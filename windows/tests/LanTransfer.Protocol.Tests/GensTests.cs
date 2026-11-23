using System.Text;
using FsCheck.Fluent;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>共享生成器与 Pbt 辅助的自检：生成结果满足各自的约定，且覆盖全部字符类与非法 UTF‑8 类别。</summary>
public sealed class GensTests
{
    [Fact]
    public void UnicodeText_has_no_unpaired_surrogate_and_covers_every_char_class()
    {
        var samples = Gens.UnicodeText().Generator.Draw(3000).Select(t => t.Value).ToArray();
        Assert.All(samples, s => Assert.False(Gens.HasUnpairedSurrogate(s)));
        var covered = TextCharClass.All.Where(c => samples.Any(s => c.Samples.Any(x => s.Contains(x, StringComparison.Ordinal)))).Select(c => c.Label);
        Assert.Equal(TextCharClass.Labels.Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
        Assert.Contains(samples, s => s.StartsWith('\uFEFF'));
        Assert.Contains(samples, s => s.Length > 0 && TextCharClass.EdgeWhitespace.Samples.Any(w => s.EndsWith(w, StringComparison.Ordinal)));
    }

    [Fact]
    public void Char_class_labels_are_unique_and_match_kotlin_order()
    {
        Assert.Equal(TextCharClass.All.Count, TextCharClass.Labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [
                "lf", "cr", "crlf", "tab", "consecutive-spaces", "edge-whitespace", "nbsp", "ideographic-space", "nul", "c0-control",
                "del", "c1-control", "zwj-sequence", "skin-tone-modifier", "variation-selector-16", "zero-width", "bom",
                "combining-mark", "supplementary-plane", "private-use", "unassigned", "noncharacter",
            ],
            TextCharClass.Labels);
        Assert.All(TextCharClass.All, c => Assert.All(c.Samples, s => Assert.False(Gens.HasUnpairedSurrogate(s))));
    }

    [Fact]
    public void Utf16WithUnpairedSurrogate_always_contains_one()
    {
        Assert.All(Gens.Utf16WithUnpairedSurrogate().Generator.Draw(2000), t => Assert.True(Gens.HasUnpairedSurrogate(t.Value)));
    }

    [Fact]
    public void InvalidUtf8_is_rejected_by_strict_decoder_and_covers_every_kind()
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var samples = Gens.InvalidUtf8().Generator.Draw(2000);
        Assert.All(samples, s => Assert.ThrowsAny<DecoderFallbackException>(() => strict.GetString(s.Bytes)));
        Assert.Equal(InvalidUtf8.Kinds.Order(StringComparer.Ordinal), samples.Select(s => s.Kind).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void HasUnpairedSurrogate_detects_lone_and_reversed_surrogates()
    {
        Assert.False(Gens.HasUnpairedSurrogate(""));
        Assert.False(Gens.HasUnpairedSurrogate(TextCharClass.Cps(0x1F600)));
        Assert.True(Gens.HasUnpairedSurrogate("\uD83D"));
        Assert.True(Gens.HasUnpairedSurrogate("\uDE00"));
        Assert.True(Gens.HasUnpairedSurrogate("\uDE00\uD83D"));
        Assert.True(Gens.HasUnpairedSurrogate("a\uD83Db"));
    }

    [Fact]
    public void Pbt_parses_scale_and_seed()
    {
        Assert.Equal(1, Pbt.ParseScale(null));
        Assert.Equal(1, Pbt.ParseScale(" "));
        Assert.Equal(10, Pbt.ParseScale("10"));
        Assert.Throws<ArgumentException>(() => Pbt.ParseScale("0"));
        Assert.Throws<ArgumentException>(() => Pbt.ParseScale("x"));
        Assert.Null(Pbt.ParseReplay(null));
        Assert.Equal("(123,456)", Pbt.ParseReplay(" (123,456) "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pbt.Iterations(99));
        Assert.Equal(1000 * Pbt.Scale, Pbt.Iterations(1000));
        Assert.Equal(100 * Pbt.Scale, new PbtPropertyAttribute(100).MaxTest);
    }

    /// <summary>工具链检查：[PbtProperty] 能被 xUnit v3 发现并执行，且使用 Gens 中注册的生成器。</summary>
    [PbtProperty(100)]
    public bool PbtProperty_is_discovered_and_uses_registered_arbitraries(UnicodeText text) => !Gens.HasUnpairedSurrogate(text.Value);
}
