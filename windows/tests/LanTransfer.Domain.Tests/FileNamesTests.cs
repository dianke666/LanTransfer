using System.Globalization;
using System.Text;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>文件名处理的示例测试与 filename-sanitizer.json 一致性测试（任务 8.3），与 Kotlin 侧 FileNamesTest 对应。</summary>
public sealed class FileNamesTests
{
    private static readonly DateTime Now = new(2025, 1, 1, 9, 30, 5, DateTimeKind.Unspecified);

    private static string A(string s) => FileNameSanitizer.Sanitize(s, FsProfile.Android, Now);

    private static string W(string s) => FileNameSanitizer.Sanitize(s, FsProfile.Windows, Now);

    [Fact]
    public void Boundary_examples()
    {
        Assert.Equal("_con.txt", W("con.txt"));
        Assert.Equal("con.txt", A("con.txt"));
        Assert.Equal("_Nul.tar.gz", W("Nul.tar.gz"));
        Assert.Equal("_COM1", W("COM1"));
        Assert.Equal("_CON .txt", W("CON .txt"));
        Assert.Equal("Com0.txt", W("Com0.txt"));
        Assert.Equal("file_20250101_093005", A(".."));
        Assert.Equal("b", W("a/../b"));
        Assert.Equal("report", W("report."));
        Assert.Equal("report.", A("report."));
        Assert.Equal(new string('中', 85), A(new string('中', 85)));
        var longName = A(new string('中', 86) + ".txt");
        Assert.EndsWith(".txt", longName, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(longName) <= 255);
        Assert.Equal("file_20250101_093005.txt", W("file_20250101_093005.txt"));
        Assert.Equal("a_b_c_d_e_f_g_h.txt", W("a<b>c:d\"e|f?g*h.txt"));
        Assert.Equal("a_b.txt", A("a\uD800b.txt"));
        Assert.Equal("file_20250101_093005", W("..."));
    }

    [Fact]
    public void Collisions_take_the_smallest_index_and_truncate_the_stem()
    {
        var taken = new HashSet<string> { "a.txt", "a (1).txt", "a (2).txt" };
        Assert.Equal("a (3).txt", NameCollisionResolver.Resolve("a.txt", FsProfile.Android, taken.Contains));
        Assert.Equal("b.txt", NameCollisionResolver.Resolve("b.txt", FsProfile.Android, taken.Contains));
        Assert.Equal("a (5).txt", NameCollisionResolver.Resolve("a.txt", FsProfile.Android, taken.Contains, startAt: 5));
        var longName = new string('x', 251) + ".txt";
        var r = NameCollisionResolver.Resolve(longName, FsProfile.Windows, s => s == longName);
        Assert.Equal(new string('x', 247) + " (1).txt", r);
        Assert.Equal(255, r.Length);
    }

    [Fact]
    public void Vectors_match_both_profiles_and_cover_every_step()
    {
        var vectors = TestVectors.Vectors("filename-sanitizer.json");
        var steps = new HashSet<string>();
        foreach (var v in vectors)
        {
            var input = v.Optional("input")?.GetString() ?? string.Concat(v.Str("input_utf16").Chunk(4).Select(c => (char)int.Parse(new string(c), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            var now = DateTime.ParseExact(v.Str("now"), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            Assert.Equal(v.Str("android"), FileNameSanitizer.Sanitize(input, FsProfile.Android, now));
            Assert.Equal(v.Str("windows"), FileNameSanitizer.Sanitize(input, FsProfile.Windows, now));
            foreach (var profile in v.GetProperty("steps").EnumerateObject())
            {
                steps.UnionWith(profile.Value.EnumerateArray().Select(e => e.GetString()!));
            }
        }

        Assert.Subset(steps, new HashSet<string> { "S0", "S1", "S2", "L1", "L2", "L3", "L4" });
        var inputs = vectors.Select(v => v.Optional("input")?.GetString()).OfType<string>().ToHashSet();
        Assert.Subset(inputs, new HashSet<string> { "con.txt", "Nul.tar.gz", "COM1", "CON .txt", "..", "a/../b", "report.", new string('中', 85), "file_20250101_093005.txt" });
    }
}
