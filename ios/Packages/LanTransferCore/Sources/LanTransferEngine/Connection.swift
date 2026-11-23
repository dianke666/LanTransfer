import Foundation
import LanTransferDomain
import LanTransferProtocol

/// 出站消息优先级：P0 控制、P1 文字、P2 文件分片。
public enum SendPriority: Sendable {
    case control
    case text
    case fileChunk
}

public enum ConnectionState: Hashable, Sendable {
    case pending
    case active
    case closed(CloseReason)
}

/// 握手完成后的加密会话连接驱动：管理 P0/P1/P2 优先级单写循环、记录层解密+帧解码读循环、5 s 心跳与 15 s 无响应超时。
public actor Connection {
    private static let pendingAllowed: Set<MessageType> = [
        .sessionResult, .sessionClose, .heartbeat,
        .pairRequest, .pairNonce, .pairReveal, .pairDecision, .pairCancel, .pairComplete,
        .qrPairRequest, .qrPairResult,
    ]

    public nonisolated let peer: PeerIdentity
    public nonisolated let role: HandshakeRole
    public nonisolated let version: ProtocolVersion

    private let sock: any TcpConn
    private let reader: ConnReader
    private let sendCipher: RecordCipher
    private let recvStream: RecordReader
    private let decoder: FrameDecoder
    private let clock: any ClockPort
    private let timings: SessionTimings

    private var state: ConnectionState = .pending
    private var incomingQueue: [ProtocolMessage] = []
    private var incomingWaiter: CheckedContinuation<ProtocolMessage?, Never>?
    private var incomingFinished: Bool = false
    private var p0Queue: [ProtocolMessage] = []
    private var p1Queue: [ProtocolMessage] = []
    private var p2Queue: [ProtocolMessage] = []
    private var writerWaiter: CheckedContinuation<Void, Never>?
    private var lastSentMs: Int64
    private var lastReceivedMs: Int64
    private var readTask: Task<Void, Never>?
    private var writeTask: Task<Void, Never>?
    private var timerTask: Task<Void, Never>?
    private var closeCallbacks: [@Sendable (CloseReason) -> Void] = []

    public init(
        sock: any TcpConn,
        reader: ConnReader,
        peer: PeerIdentity,
        role: HandshakeRole,
        version: ProtocolVersion,
        sendCipher: RecordCipher,
        receiveCipher: RecordCipher,
        clock: any ClockPort,
        timings: SessionTimings = .default
    ) {
        self.sock = sock
        self.reader = reader
        self.peer = peer
        self.role = role
        self.version = version
        self.sendCipher = sendCipher
        self.recvStream = receiveCipher.openStream()
        self.decoder = FrameDecoder(version: version)
        self.clock = clock
        self.timings = timings
        let now = clock.monotonicMs()
        self.lastSentMs = now
        self.lastReceivedMs = now
    }

    /// 按到达顺序取出下一条消息；可在配对升级为会话时无缝交接给新消费者，不丢失任何在途消息。
    public func nextMessage() async -> ProtocolMessage? {
        if !incomingQueue.isEmpty {
            return incomingQueue.removeFirst()
        }
        if incomingFinished {
            return nil
        }
        return await withCheckedContinuation { cont in
            self.incomingWaiter = cont
        }
    }

    private func deliverIncoming(_ msg: ProtocolMessage) {
        if let waiter = incomingWaiter {
            incomingWaiter = nil
            waiter.resume(returning: msg)
        } else {
            incomingQueue.append(msg)
        }
    }

    private func finishIncoming() {
        incomingFinished = true
        if let waiter = incomingWaiter {
            incomingWaiter = nil
            waiter.resume(returning: nil)
        }
    }

    public func start() {
        guard readTask == nil else { return }
        readTask = Task { await self.runReadLoop() }
        writeTask = Task { await self.runWriteLoop() }
        timerTask = Task { await self.runTimerLoop() }
    }

    public func currentState() -> ConnectionState {
        state
    }

    public func onClose(_ callback: @escaping @Sendable (CloseReason) -> Void) {
        if case .closed(let reason) = state {
            callback(reason)
        } else {
            closeCallbacks.append(callback)
        }
    }

    public func promoteToActive() {
        if case .pending = state {
            state = .active
        }
    }

    public func send(_ message: ProtocolMessage, priority: SendPriority = .control) {
        if case .closed = state { return }
        switch priority {
        case .control: p0Queue.append(message)
        case .text: p1Queue.append(message)
        case .fileChunk: p2Queue.append(message)
        }
        wakeWriter()
    }

    public func sendAndClose(_ lastMessage: ProtocolMessage, reason: CloseReason) async {
        send(lastMessage, priority: .control)
        try? await clock.sleep(milliseconds: 50)
        await close(reason: reason)
    }

    public func closeGracefully(reason: SessionCloseReason) async {
        await sendAndClose(.sessionClose(reason: reason), reason: .local(reason))
    }

    public func close(reason: CloseReason) async {
        if case .closed = state { return }
        state = .closed(reason)
        decoder.close()
        wakeWriter()
        readTask?.cancel()
        writeTask?.cancel()
        timerTask?.cancel()
        await sock.close()
        finishIncoming()
        let callbacks = closeCallbacks
        closeCallbacks.removeAll()
        for cb in callbacks {
            cb(reason)
        }
    }

    // MARK: - Loops

    private func runReadLoop() async {
        while !Task.isCancelled {
            guard let encrypted = await reader.readSome(maxBytes: 64 * 1024), !encrypted.isEmpty else {
                await close(reason: .peerClosed(nil))
                return
            }
            lastReceivedMs = clock.monotonicMs()
            let fed = recvStream.feed(encrypted)
            if fed.integrityError {
                await close(reason: .integrityFailed)
                return
            }
            for plain in fed.chunks {
                let batch = decoder.feed(plain)
                for msg in batch.messages {
                    if !acceptIncoming(msg) {
                        return
                    }
                }
                if batch.error != nil {
                    await close(reason: .protocolError)
                    return
                }
            }
        }
    }

    private func acceptIncoming(_ msg: ProtocolMessage) -> Bool {
        if case .pending = state, !Self.pendingAllowed.contains(msg.type) {
            Task { await self.close(reason: .pendingViolation) }
            return false
        }
        // 原子升级：防止对方紧随 SESSION_RESULT(ACCEPTED) / PAIR_COMPLETE 后的业务帧被误判为 Pending 违规
        switch msg {
        case .sessionResult(let res) where res == .accepted:
            state = .active
        case .pairComplete:
            state = .active
        case .sessionClose(let reason):
            deliverIncoming(msg)
            Task { await self.close(reason: .peerClosed(reason)) }
            return false
        case .heartbeat:
            return true
        default:
            break
        }
        deliverIncoming(msg)
        return true
    }

    private func runWriteLoop() async {
        while !Task.isCancelled {
            guard let msg = dequeueNext() else {
                if case .closed = state { return }
                await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                    self.writerWaiter = cont
                }
                continue
            }
            if msg == .heartbeat && (clock.monotonicMs() - lastSentMs) < timings.heartbeatMs {
                continue
            }
            let encoded: Data
            switch FrameEncoder.encode(msg, version: version) {
            case .ok(let data):
                encoded = data
            case .payloadTooLarge:
                continue
            }
            let sealed = sendCipher.seal(encoded)
            guard await sock.write(sealed) else {
                await close(reason: .peerClosed(nil))
                return
            }
            lastSentMs = clock.monotonicMs()
        }
    }

    private func runTimerLoop() async {
        while !Task.isCancelled {
            do {
                try await clock.sleep(milliseconds: 1_000)
            } catch {
                return
            }
            if case .closed = state { return }
            let now = clock.monotonicMs()
            if now - lastReceivedMs >= timings.idleTimeoutMs {
                await close(reason: .peerUnresponsive)
                return
            }
            if now - lastSentMs >= timings.heartbeatMs {
                send(.heartbeat, priority: .control)
            }
        }
    }

    private func dequeueNext() -> ProtocolMessage? {
        if !p0Queue.isEmpty { return p0Queue.removeFirst() }
        if !p1Queue.isEmpty { return p1Queue.removeFirst() }
        if !p2Queue.isEmpty { return p2Queue.removeFirst() }
        return nil
    }

    private func wakeWriter() {
        if let waiter = writerWaiter {
            writerWaiter = nil
            waiter.resume()
        }
    }
}
