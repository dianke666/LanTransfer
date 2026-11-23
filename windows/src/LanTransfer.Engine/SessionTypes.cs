using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// SessionManager 的对外类型（设计 C4.1、C4.2、E1、E2），与 Kotlin 侧 SessionTypes.kt 同名镜像。

/// <summary>连接目标。</summary>
public abstract record Target
{
    private Target()
    {
    }

    /// <summary>地址取设备列表；不在列表中时取 TrustStore 记录的最近成功地址。</summary>
    public sealed record Device(DeviceId Id) : Target;

    /// <summary>手动连接。</summary>
    public sealed record Address(Ipv4 Ip, int Port) : Target;

    /// <summary>候选地址按二维码中的顺序逐个尝试，每个 5 s（8.5）。</summary>
    public sealed record Qr(QrPayload Payload) : Target;
}

/// <summary>连接建立失败（E1）。提示文案由 UI 层按类型查表。</summary>
public abstract record OpenFailure
{
    private OpenFailure()
    {
    }

    public sealed record NotLocal : OpenFailure;

    public sealed record SelfConnection : OpenFailure;

    public sealed record BindFailed(bool VpnSuspected) : OpenFailure;

    public sealed record Unreachable : OpenFailure;

    public sealed record Refused : OpenFailure;

    public sealed record Timeout : OpenFailure;

    public sealed record NoVersionResponse : OpenFailure;

    public sealed record ProtocolError : OpenFailure;

    public sealed record VersionIncompatible(ProtocolVersion Local, ProtocolVersion Remote) : OpenFailure;

    public sealed record HandshakeAuthFailed : OpenFailure;

    /// <summary>对方是 Trusted_Device，但出示的公钥与保存的不一致（9.4）；Name 为保存的设备名称。</summary>
    public sealed record KeyMismatch(string Name) : OpenFailure;

    public sealed record FingerprintMismatch : OpenFailure;

    /// <summary>收到 SESSION_RESULT(NOT_TRUSTED)（9.5）。</summary>
    public sealed record NotTrusted : OpenFailure;

    /// <summary>收到 SESSION_RESULT(KEY_MISMATCH)：对方保存的本机公钥与本机出示的不一致（设计补充，Q13）。</summary>
    public sealed record PeerKeyMismatch(string Name) : OpenFailure;
}

/// <summary>握手中认证过的对端身份；PublicKey 是对方在 Noise 握手中证明持有私钥的静态公钥。</summary>
public sealed record PeerIdentity(DeviceId DeviceId, string DisplayName, DeviceType DeviceType, Bytes PublicKey);

public abstract record OpenResult
{
    private OpenResult()
    {
    }

    public sealed record Ok(Session Session) : OpenResult;

    /// <summary>intent 为 PAIR / QR_PAIR：握手已完成，连接处于 Pending，交给 PairingService。</summary>
    public sealed record Pairing(PendingConnection Connection) : OpenResult;

    public sealed record Failed(OpenFailure Failure) : OpenResult;
}

public abstract record ListenResult
{
    private ListenResult()
    {
    }

    public sealed record Ok(int Port) : ListenResult;

    /// <summary>42580、42590–42599 与系统分配端口全部失败（5.9）。</summary>
    public sealed record Failed : ListenResult;
}

/// <summary>连接关闭的原因。</summary>
public abstract record CloseReason
{
    private CloseReason()
    {
    }

    /// <summary>本机主动关闭，关闭前已尝试发送 SESSION_CLOSE(Reason)。</summary>
    public sealed record Local(SessionCloseReason Reason) : CloseReason;

    /// <summary>对方发来 SESSION_CLOSE(Reason)，或读到 EOF / RST（Reason 为 null）。</summary>
    public sealed record PeerClosed(SessionCloseReason? Reason) : CloseReason;

    /// <summary>连续 15 s 没有收到任何帧（18.6）。</summary>
    public sealed record PeerUnresponsive : CloseReason;

    /// <summary>连接所在的本地接口消失（18.8）。</summary>
    public sealed record LocalNetworkLost : CloseReason;

    /// <summary>记录层完整性校验失败（10.5）。</summary>
    public sealed record IntegrityFailed : CloseReason;

    /// <summary>FrameDecoder 报错或对方违反协议（22.6）。</summary>
    public sealed record ProtocolError(string Detail) : CloseReason;

    /// <summary>Pending 阶段收到白名单以外的消息，已回复 SESSION_RESULT(NOT_TRUSTED)（10.4）。</summary>
    public sealed record PendingViolation : CloseReason;

    /// <summary>同一对端的新连接取代了这条连接（对方已丢失旧连接）。</summary>
    public sealed record Superseded : CloseReason;
}

public static class CloseReasonExtensions
{
    /// <summary>
    /// 关闭原因 → 进行中任务的中断原因（C4.2、E2、M3.2）；null 表示不影响任务（DUPLICATE：该连接上没有应用消息；
    /// 本机解除配对：相关任务已由解除配对流程取消）。对方因退出发来的 SESSION_CLOSE 在本机记为 PeerClosed。
    /// 与 Kotlin 侧扩展属性 <c>interruptReason</c> 对应。
    /// </summary>
    public static InterruptReason? ToInterruptReason(this CloseReason reason) => reason switch
    {
        CloseReason.Local { Reason: SessionCloseReason.Duplicate or SessionCloseReason.Unpaired } => null,
        CloseReason.Local => InterruptReason.AppExit,
        CloseReason.PeerClosed { Reason: SessionCloseReason.Duplicate } => null,
        CloseReason.PeerClosed => InterruptReason.PeerClosed,
        CloseReason.PeerUnresponsive => InterruptReason.PeerUnresponsive,
        CloseReason.LocalNetworkLost => InterruptReason.LocalNetworkLost,
        CloseReason.IntegrityFailed => InterruptReason.IntegrityFailed,
        CloseReason.ProtocolError or CloseReason.PendingViolation => InterruptReason.ProtocolError,
        CloseReason.Superseded => InterruptReason.PeerClosed,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

/// <summary>已注册 Session 的快照（供界面显示）。</summary>
public sealed record SessionInfo(PeerIdentity Peer, SocketAddr Remote, bool InitiatedLocally, ProtocolVersion Version);

/// <summary>Session 的注册与关闭事件（供文字、文件服务订阅）。</summary>
public abstract record SessionEvent
{
    private SessionEvent()
    {
    }

    public sealed record Opened(Session Session) : SessionEvent;

    public sealed record Closed(Session Session, CloseReason Reason) : SessionEvent;
}

/// <summary>接收方一侧需要提示的握手失败（E1：VersionIncompatible、HandshakeAuthFailed、KeyMismatch），去抖由 UI 层负责（E7）。</summary>
public sealed record InboundFailure(SocketAddr Remote, DeviceId? Peer, OpenFailure Failure);

/// <summary>时限（需求规定的数值；只为测试注入而做成参数），单位毫秒。</summary>
public sealed record SessionTimings
{
    /// <summary>Device / Address 目标从调用起共用的预算（6.5、7.11、9.8）。</summary>
    public long OpenMs { get; init; } = 10_000;

    /// <summary>扫码时每个候选地址的预算（8.5）。</summary>
    public long QrPerAddressMs { get; init; } = 5_000;

    /// <summary>入站连接完成前导与握手的时限。</summary>
    public long InboundHandshakeMs { get; init; } = 10_000;

    /// <summary>连续这么久没有发送任何帧时发送 HEARTBEAT（18.5）。</summary>
    public long HeartbeatMs { get; init; } = 5_000;

    /// <summary>连续这么久没有收到任何帧时以“对方无响应”关闭（18.6）。</summary>
    public long UnresponsiveMs { get; init; } = 15_000;

    /// <summary>主动关闭时等待 SESSION_CLOSE 写出的最长时间。</summary>
    public long CloseGraceMs { get; init; } = 1_000;

    public static SessionTimings Default { get; } = new();
}
