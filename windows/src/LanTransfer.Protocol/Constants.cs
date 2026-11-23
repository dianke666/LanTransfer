namespace LanTransfer.Protocol;

/// <summary>协议常量（规范第 1 节），与 Kotlin 侧 <c>ProtocolConstants</c> 对应。</summary>
public static class ProtocolConstants
{
    public const int DefaultPort = 42580;
    public const int DiscoveryPort = 42581;
    public const string MulticastGroup = "239.255.42.58";

    public const ushort FrameMagic = 0x4C54;
    public const int FrameHeaderSize = 7;
    public const int MaxPayload = 2 * 1024 * 1024;

    public const int MaxTextBytes = 1_048_576;
    public const int MaxChunk = 65_536;
    public const int MaxFilesPerOffer = 1000;
    public const ulong MaxFileSize = 1UL << 40;
    public const int MaxMimeBytes = 255;

    public const int RecordMinCiphertext = 17;
    public const int RecordMaxCiphertext = 65_535;
    public const int RecordMaxPlaintext = RecordMaxCiphertext - 16;

    public const int MaxAnnouncement = 512;
    public const int MaxQrText = 512;
    public const int MaxQrIps = 4;
    public const int MinNameCodePoints = 1;
    public const int MaxNameCodePoints = 32;
}

/// <summary>Protocol_Version：主版本号.次版本号。</summary>
public readonly record struct ProtocolVersion(byte Major, byte Minor) : IComparable<ProtocolVersion>
{
    public static ProtocolVersion V1_0 { get; } = new(1, 0);

    /// <summary>本实现支持的最新版本。</summary>
    public static ProtocolVersion Current => V1_0;

    public int CompareTo(ProtocolVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    /// <summary>在 <paramref name="negotiated"/> 中是否已定义以本版本引入的内容（同一主版本，次版本不晚于协商版本）。</summary>
    public bool DefinedIn(ProtocolVersion negotiated) => Major == negotiated.Major && Minor <= negotiated.Minor;

    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>设备类型。</summary>
public enum DeviceType : byte
{
    Android = 1,
    Windows = 2,
    Ios = 3,
}
