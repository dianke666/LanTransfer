import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import Security
import UIKit

/// Keychain 本机身份存储：X25519 私钥仅存本机 Keychain（`AfterFirstUnlockThisDeviceOnly`，禁用 iCloud 同步），显示名存于设置中且在身份重置后保留。
public actor KeychainIdentityStore: IdentityStore {
    private static let service = "com.lantransfer.app.identity"
    private static let account = "device_identity_v1"
    private static let displayNameKey = "lan.deviceDisplayName"
    private static let blobSize = 16 + 32 + 32

    private let rng: any RngPort
    private let suiteName: String?

    public init(rng: any RngPort = SystemRng(), suiteName: String? = AppGroupConfig.groupId) {
        self.rng = rng
        self.suiteName = suiteName
    }

    private func defaults() -> UserDefaults {
        if let suiteName {
            return UserDefaults(suiteName: suiteName) ?? .standard
        }
        return .standard
    }

    public func displayName() async -> String {
        if let saved = defaults().string(forKey: Self.displayNameKey), !saved.isEmpty {
            return saved
        }
        let deviceName = await MainActor.run { UIDevice.current.name }
        let fallback = DisplayNameRules.defaultName(modelOrComputerName: deviceName, type: .ios)
        defaults().set(fallback, forKey: Self.displayNameKey)
        return fallback
    }

    public func setDisplayName(_ name: String) async -> StoreResult {
        defaults().set(name, forKey: Self.displayNameKey)
        return .ok
    }

    public func loadOrCreate() async -> IdentityLoad {
        let name = await displayName()
        let (status, existingData) = readKeychainBlob()

        if status == errSecSuccess, let blob = existingData {
            if let decoded = decodeIdentity(blob, displayName: name) {
                return .loaded(decoded)
            }
            // 已保存身份损坏：重新生成并覆盖保存，通知 UI 提示需重新配对
            let fresh = createFreshIdentity(displayName: name)
            if let err = writeKeychainBlob(encodeIdentity(fresh)) {
                return .saveFailed(detail: err, corrupted: true)
            }
            return .resetCorrupted(fresh)
        }

        if status == errSecItemNotFound {
            let fresh = createFreshIdentity(displayName: name)
            if let err = writeKeychainBlob(encodeIdentity(fresh)) {
                return .saveFailed(detail: err, corrupted: false)
            }
            return .created(fresh)
        }

        // 其他读取异常，尝试重建
        let fresh = createFreshIdentity(displayName: name)
        if let err = writeKeychainBlob(encodeIdentity(fresh)) {
            return .saveFailed(detail: err, corrupted: true)
        }
        return .resetCorrupted(fresh)
    }

    private func createFreshIdentity(displayName: String) -> DeviceIdentity {
        let id = DeviceId(bytes: rng.nextBytes(count: 16))!
        let keyPair = X25519KeyPair.generate()
        return DeviceIdentity(deviceId: id, keyPair: keyPair, displayName: displayName)
    }

    private func encodeIdentity(_ id: DeviceIdentity) -> Data {
        var data = Data(capacity: Self.blobSize)
        data.append(id.deviceId.bytes)
        data.append(id.keyPair.privateKeyBytes())
        data.append(id.keyPair.publicKey)
        return data
    }

    private func decodeIdentity(_ data: Data, displayName: String) -> DeviceIdentity? {
        guard data.count == Self.blobSize else { return nil }
        let bytes = [UInt8](data)
        guard let deviceId = DeviceId(bytes: Data(bytes[0..<16])),
              let keyPair = try? X25519KeyPair.fromPrivateKey(Data(bytes[16..<48])) else {
            return nil
        }
        let storedPub = Data(bytes[48..<80])
        guard keyPair.publicKey == storedPub else { return nil }
        return DeviceIdentity(deviceId: deviceId, keyPair: keyPair, displayName: displayName)
    }

    private func readKeychainBlob() -> (OSStatus, Data?) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
            kSecAttrSynchronizable as String: false,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        return (status, item as? Data)
    }

    private func writeKeychainBlob(_ data: Data) -> String? {
        let baseQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
            kSecAttrSynchronizable as String: false,
        ]
        SecItemDelete(baseQuery as CFDictionary)

        var addQuery = baseQuery
        addQuery[kSecValueData as String] = data
        addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(addQuery as CFDictionary, nil)
        return status == errSecSuccess ? nil : "Keychain OSStatus=\(status)"
    }
}
