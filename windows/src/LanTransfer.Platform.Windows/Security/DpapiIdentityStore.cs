using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Platform.Windows.Security;

/// <summary>
/// Windows DPAPI 身份存储（设计 C5.3、C7.1、M4）：
/// <c>identity.bin</c> 格式：<c>version:u8(1) ‖ device_id[16] ‖ public_key[32] ‖ DPAPI(CurrentUser, private_key[32])</c>。
/// 显示名称存放在 JsonSettingsStore 中，身份损坏重置时自动保留名称与可信设备列表（4.6）。
/// </summary>
public sealed class DpapiIdentityStore : IIdentityStore
{
    private const byte FormatVersion = 1;
    private const int HeaderSize = 1 + 16 + 32;

    private readonly string _identityPath;
    private readonly Settings.JsonSettingsStore _settings;
    private readonly IRng _rng;
    private readonly Lock _gate = new();
    private DeviceIdentity? _cached;

    public DpapiIdentityStore(string identityPath, Settings.JsonSettingsStore settings, IRng rng)
    {
        _identityPath = identityPath;
        _settings = settings;
        _rng = rng;
    }

    public IdentityLoad LoadOrCreate()
    {
        lock (_gate)
        {
            var name = EnsureDisplayName();
            if (_cached is not null)
            {
                return new IdentityLoad.Loaded(new DeviceIdentity(_cached.DeviceId, _cached.KeyPair, name));
            }

            var existed = File.Exists(_identityPath);
            if (existed)
            {
                if (TryReadIdentity(_identityPath, name) is { } loaded)
                {
                    _cached = loaded;
                    return new IdentityLoad.Loaded(loaded);
                }
            }

            // 首次创建或损坏重置
            var id = new DeviceId(Bytes16.Of(_rng.NextBytes(16)));
            var keyPair = X25519KeyPair.FromPrivateKey(_rng.NextBytes(32));
            var created = new DeviceIdentity(id, keyPair, name);
            var err = TrySaveIdentity(_identityPath, created);
            if (err is not null)
            {
                return new IdentityLoad.SaveFailed(err, existed);
            }

            _cached = created;
            return existed ? new IdentityLoad.ResetCorrupted(created) : new IdentityLoad.Created(created);
        }
    }

    public string DisplayName()
    {
        lock (_gate)
        {
            return EnsureDisplayName();
        }
    }

    public StoreResult SetDisplayName(string name)
    {
        lock (_gate)
        {
            var r = _settings.SetDisplayName(name);
            if (r is StoreResult.Ok && _cached is not null)
            {
                _cached = new DeviceIdentity(_cached.DeviceId, _cached.KeyPair, name);
            }

            return r;
        }
    }

    private string EnsureDisplayName()
    {
        if (_settings.GetDisplayName() is { Length: > 0 } saved)
        {
            return saved;
        }

        var host = GetPhysicalDnsHostname() ?? Environment.MachineName;
        var def = DisplayNameRules.DefaultName(host, DeviceType.Windows);
        _settings.SetDisplayName(def);
        return def;
    }

    private static DeviceIdentity? TryReadIdentity(string path, string displayName)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length <= HeaderSize || data[0] != FormatVersion)
            {
                return null;
            }

            var devId = new DeviceId(Bytes16.Of(data.AsSpan(1, 16)));
            var pubKey = data.AsSpan(17, 32);
            var protectedPriv = data[HeaderSize..];
            var privKey = ProtectedData.Unprotect(protectedPriv, optionalEntropy: null, DataProtectionScope.CurrentUser);
            if (privKey.Length != 32)
            {
                return null;
            }

            var kp = X25519KeyPair.FromPrivateKey(privKey);
            if (!kp.PublicKey.Span.SequenceEqual(pubKey))
            {
                return null;
            }

            return new DeviceIdentity(devId, kp, displayName);
        }
        catch
        {
            return null;
        }
    }

    private static string? TrySaveIdentity(string path, DeviceIdentity identity)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var protectedPriv = ProtectedData.Protect(identity.KeyPair.PrivateKeyBytes(), optionalEntropy: null, DataProtectionScope.CurrentUser);
            var buf = new byte[HeaderSize + protectedPriv.Length];
            buf[0] = FormatVersion;
            identity.DeviceId.Value.ToArray().CopyTo(buf.AsSpan(1, 16));
            identity.KeyPair.PublicKey.Span.CopyTo(buf.AsSpan(17, 32));
            protectedPriv.CopyTo(buf, HeaderSize);

            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(buf, 0, buf.Length);
                fs.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(tmp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, path);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? GetPhysicalDnsHostname()
    {
        var sb = new StringBuilder(128);
        var len = sb.Capacity;
        return GetComputerNameExW(4 /* ComputerNamePhysicalDnsHostname */, sb, ref len) && len > 0
            ? sb.ToString()
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComputerNameExW(int nameType, StringBuilder lpBuffer, ref int nSize);
}
