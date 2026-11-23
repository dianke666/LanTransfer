using LanTransfer.Protocol;

namespace LanTransfer.Domain.Tests;

/// <summary>归约器测试共用的构造函数（Property 35–38 与状态迁移表测试），与 Kotlin 侧 ReducerFixtures.kt 对应。</summary>
internal static class ReducerFixtures
{
    public static readonly TransferId FixtureTransferId = new(Bytes16.Of(Enumerable.Repeat((byte)0x5A, 16).ToArray()));

    public static readonly DateTime FixtureLocalNow = new(2026, 10, 4, 9, 30, 5, DateTimeKind.Unspecified);

    public static DeviceId DeviceIdOf(int b) => new(Bytes16.Of(Enumerable.Repeat(unchecked((byte)b), 16).ToArray()));

    public static Bytes Bytes32(int b) => Bytes.Of(Enumerable.Range(0, 32).Select(i => unchecked((byte)(b + i))).ToArray());

    public static PeerInfo PeerInfoOf(int b, DeviceType type = DeviceType.Android) =>
        new(DeviceIdOf(b), $"设备{b}", type, Bytes32(b * 7), Ipv4.Of(192, 168, 1, unchecked((byte)b)), 42580);

    public static TransferTask MakeTask(Direction direction, TaskState state, params long[] sizes) => new(
        FixtureTransferId,
        DeviceIdOf(9),
        "对方",
        direction,
        state,
        state == TaskState.Interrupted ? new InterruptInfo(InterruptReason.PeerClosed) : null,
        direction == Direction.Receive && state != TaskState.AwaitingUser ? DirRef.Default.Instance : null,
        new RecordTime(1_700_000_000_000, 480),
        1_700_000_000_000,
        new ValueList<TransferFile>(sizes.Select((size, i) =>
            new TransferFile(FixtureTransferId, i, i, $"f{i}.bin", size, "application/octet-stream", 1_600_000_000_000, FileState.Waiting))));

    public static Bytes RandomBytes(this Random r, int n)
    {
        var b = new byte[n];
        r.NextBytes(b);
        return Bytes.Of(b);
    }

    public static long Pick(this Random r, params long[] values) => values[r.Next(values.Length)];
}
