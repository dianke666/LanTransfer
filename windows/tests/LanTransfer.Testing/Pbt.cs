using System.Globalization;
using FsCheck.Xunit;

namespace LanTransfer.Testing;

/// <summary>
/// 属性测试辅助（设计“属性测试约定”），与 Kotlin 侧 <c>com.lantransfer.testing.Pbt</c> 同名：
/// 迭代次数 = base × <c>LT_PBT_SCALE</c>（未设置时为 1，夜间任务设为 10）；
/// 设置 <c>LT_PBT_SEED</c> 后用该值作为 FsCheck 的 Replay 重放（格式如 <c>(123,456)</c>，可直接粘贴 FsCheck 失败输出中的元组）。
/// </summary>
public static class Pbt
{
    public const string ScaleEnv = "LT_PBT_SCALE";
    public const string SeedEnv = "LT_PBT_SEED";

    /// <summary>每个属性至少运行的次数。</summary>
    public const int MinBaseIterations = 100;

    public static int Scale => ParseScale(Environment.GetEnvironmentVariable(ScaleEnv));

    public static string? Replay => ParseReplay(Environment.GetEnvironmentVariable(SeedEnv));

    /// <summary>纯函数属性用 <c>Iterations(1000)</c>，引擎与存储属性用 <c>Iterations(100)</c>。</summary>
    public static int Iterations(int @base)
    {
        if (@base < MinBaseIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(@base), @base, $"属性测试至少运行 {MinBaseIterations} 次");
        }

        return checked(@base * Scale);
    }

    /// <summary>未设置或为空时为 1；其余必须是正整数，否则报错，避免夜间任务悄悄按 1 倍运行。</summary>
    public static int ParseScale(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return 1;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new ArgumentException($"{ScaleEnv} 必须是正整数，实际为 \"{raw}\"", nameof(raw));
        }

        return value;
    }

    /// <summary>未设置或为空时为 null（每次随机）；否则原样作为 Replay 字符串。</summary>
    public static string? ParseReplay(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        return text.Length == 0 ? null : text;
    }
}

/// <summary>
/// 带 Pbt 迭代次数与重放种子、并注册共享生成器 <see cref="Gens"/> 的 FsCheck 属性：
/// <c>[PbtProperty(1000)]</c> 相当于 <c>[Property(MaxTest = 1000 × scale, Arbitrary = [typeof(Gens)], Replay = LT_PBT_SEED)]</c>。
/// </summary>
public sealed class PbtPropertyAttribute : PropertyAttribute
{
    public PbtPropertyAttribute(int baseIterations)
    {
        BaseIterations = baseIterations;
        MaxTest = Pbt.Iterations(baseIterations);
        Arbitrary = [typeof(Gens)];
        if (Pbt.Replay is { } replay)
        {
            Replay = replay;
        }
    }

    public int BaseIterations { get; }
}
