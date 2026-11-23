using System.Text.Json;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;

namespace LanTransfer.Platform.Windows.Settings;

/// <summary>界面主题模式（支持浅色 / 深色 / 跟随系统）。</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Windows JSON 设置存储（设计 C7.1、M5）：原子替换 settings.json，未知键忽略，非法值回退默认。
/// 同时持久化设备显示名称（M5 display_name）与界面主题模式（theme_mode）。
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ObservableState<Domain.Settings> _changes;
    private readonly ObservableState<ThemeMode> _themeMode;
    private string? _displayName;

    public JsonSettingsStore(string settingsPath)
    {
        _path = settingsPath;
        var (settings, name, theme) = LoadFromDisk(settingsPath);
        _displayName = name;
        _changes = new ObservableState<Domain.Settings>(settings);
        _themeMode = new ObservableState<ThemeMode>(theme);
    }

    public IObservableState<Domain.Settings> Changes => _changes;

    public IObservableState<ThemeMode> Theme => _themeMode;

    public Domain.Settings Current() => _changes.Value;

    public StoreResult Update(Func<Domain.Settings, Domain.Settings> transform)
    {
        lock (_gate)
        {
            var next = transform(_changes.Value);
            var err = SaveToDisk(next, _displayName, _themeMode.Value);
            if (err is not null)
            {
                return new StoreResult.Failed(err);
            }

            _changes.Value = next;
            return new StoreResult.Ok();
        }
    }

    public string? GetDisplayName()
    {
        lock (_gate)
        {
            return _displayName;
        }
    }

    public StoreResult SetDisplayName(string name)
    {
        lock (_gate)
        {
            var err = SaveToDisk(_changes.Value, name, _themeMode.Value);
            if (err is not null)
            {
                return new StoreResult.Failed(err);
            }

            _displayName = name;
            return new StoreResult.Ok();
        }
    }

    public StoreResult SetTheme(ThemeMode mode)
    {
        lock (_gate)
        {
            var err = SaveToDisk(_changes.Value, _displayName, mode);
            if (err is not null)
            {
                return new StoreResult.Failed(err);
            }

            _themeMode.Value = mode;
            return new StoreResult.Ok();
        }
    }

    private static (Domain.Settings Settings, string? DisplayName, ThemeMode Theme) LoadFromDisk(string path)
    {
        var def = new Domain.Settings();
        if (!File.Exists(path))
        {
            return (def, null, ThemeMode.System);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (def, null, ThemeMode.System);
            }

            var root = doc.RootElement;
            DirRef dir = DirRef.Default.Instance;
            if (root.TryGetProperty("receive_directory", out var rdProp) && rdProp.ValueKind == JsonValueKind.String)
            {
                dir = DirRef.Parse(rdProp.GetString() ?? "default") ?? DirRef.Default.Instance;
            }

            var autoCopy = root.TryGetProperty("auto_copy", out var acProp) && acProp.ValueKind == JsonValueKind.True;
            var name = root.TryGetProperty("display_name", out var dnProp) && dnProp.ValueKind == JsonValueKind.String
                ? dnProp.GetString()
                : null;
            if (name is not null && DisplayNameRules.ValidateUserInput(name) is not NameCheck.Ok)
            {
                name = null;
            }

            var theme = ThemeMode.System;
            if (root.TryGetProperty("theme_mode", out var tmProp) && tmProp.ValueKind == JsonValueKind.String &&
                Enum.TryParse<ThemeMode>(tmProp.GetString(), ignoreCase: true, out var parsedTheme))
            {
                theme = parsedTheme;
            }

            return (new Domain.Settings(dir, autoCopy, BackgroundReceive: false), name, theme);
        }
        catch
        {
            return (def, null, ThemeMode.System);
        }
    }

    private string? SaveToDisk(Domain.Settings s, string? displayName, ThemeMode theme)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new Dictionary<string, object?>
            {
                ["display_name"] = displayName,
                ["receive_directory"] = s.ReceiveDirectory.Serialize(),
                ["auto_copy"] = s.AutoCopy,
                ["theme_mode"] = theme.ToString().ToLowerInvariant(),
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
            var tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(json, 0, json.Length);
                fs.Flush(true);
            }

            if (File.Exists(_path))
            {
                File.Replace(tmp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, _path);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
