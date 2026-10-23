using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Storage;

namespace Span.Helpers;

/// <summary>
/// 앱 설정 저장소. MSIX 패키지로 설치된 경우에는 Windows 의 <c>ApplicationData.LocalSettings</c> 를,
/// 설치 프로그램으로 패키지 없이 설치된 경우(관리자 권한·인증서가 필요 없는 방식)에는
/// <c>%LOCALAPPDATA%\WON\localsettings.json</c> 을 쓴다. 호출하는 쪽은 둘의 차이를 알 필요가 없다.
/// </summary>
public sealed class AppLocalSettings
{
    public static AppLocalSettings Current { get; } = new();

    private AppLocalSettings() { }

    public ISettingsValues Values => PackageIdentity.IsPackaged
        ? new PackagedSettingsValues(ApplicationData.Current.LocalSettings.Values)
        : FileSettingsValues.Instance;

    /// <summary>
    /// MSIX 로 실행 중일 때 현재 설정을 <c>%LOCALAPPDATA%\WON\localsettings.json</c> 에도 복사해 둔다.
    /// 나중에 패키지 없는 설치 프로그램으로 옮길 때 설정(테마, 언어, 보기 방식 등)이 그대로 이어지게 하기 위한 것이다.
    /// 앱 동작에는 쓰이지 않는다.
    /// </summary>
    public static void MirrorPackagedToFile()
    {
        if (!PackageIdentity.IsPackaged) return;
        try
        {
            var snapshot = new Dictionary<string, object>();
            foreach (var kv in ApplicationData.Current.LocalSettings.Values)
            {
                if (kv.Value is ApplicationDataCompositeValue c)
                {
                    var sc = new SettingsComposite();
                    foreach (var inner in c) if (inner.Value != null) sc[inner.Key] = inner.Value;
                    snapshot[kv.Key] = sc;
                }
                else if (kv.Value != null)
                {
                    snapshot[kv.Key] = kv.Value;
                }
            }
            FileSettingsValues.Instance.ReplaceAll(snapshot);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[AppLocalSettings] mirror failed: {ex.Message}");
        }
    }
}

/// <summary>패키지 ID 유무. 패키지 없이 실행하면 ApplicationData.Current 등이 예외를 던진다.</summary>
public static class PackageIdentity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    private const int AppModelErrorNoPackage = 15700;

    private static bool? _packaged;

    public static bool IsPackaged => _packaged ??= Detect();

    private static bool Detect()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>ApplicationDataCompositeValue 대신 쓰는 묶음 값(두 저장소 모두에서 같은 모양으로 읽힌다).</summary>
public sealed class SettingsComposite : Dictionary<string, object>
{
}

public interface ISettingsValues
{
    object? this[string key] { get; set; }
    bool TryGetValue(string key, out object? value);
    bool ContainsKey(string key);
    bool Remove(string key);
    int Count { get; }
    void Clear();
}

/// <summary>MSIX 로 설치된 경우: Windows LocalSettings 에 그대로 위임한다.</summary>
internal sealed class PackagedSettingsValues : ISettingsValues
{
    private readonly IDictionary<string, object> _inner;

    public PackagedSettingsValues(IDictionary<string, object> inner) => _inner = inner;

    public object? this[string key]
    {
        get => TryGetValue(key, out var v) ? v : null;
        set => _inner[key] = ToStored(value)!;
    }

    public bool TryGetValue(string key, out object? value)
    {
        if (_inner.TryGetValue(key, out var raw))
        {
            value = raw is ApplicationDataCompositeValue c ? FromStored(c) : raw;
            return true;
        }
        value = null;
        return false;
    }

    public bool ContainsKey(string key) => _inner.ContainsKey(key);
    public bool Remove(string key) => _inner.Remove(key);
    public int Count => _inner.Count;
    public void Clear() => _inner.Clear();

    private static object? ToStored(object? value)
    {
        if (value is not SettingsComposite sc) return value;
        var c = new ApplicationDataCompositeValue();
        foreach (var kv in sc) c[kv.Key] = kv.Value;
        return c;
    }

    private static SettingsComposite FromStored(ApplicationDataCompositeValue c)
    {
        var sc = new SettingsComposite();
        foreach (var kv in c) sc[kv.Key] = kv.Value;
        return sc;
    }
}

/// <summary>
/// 패키지 없이 설치된 경우: 작은 JSON 파일에 형식(문자열/불리언/정수/실수/묶음)과 함께 저장한다.
/// 파일이 깨졌으면 .bad 로 옮기고 빈 설정으로 시작한다(앱이 죽지 않게).
/// </summary>
internal sealed class FileSettingsValues : ISettingsValues
{
    public static FileSettingsValues Instance { get; } = new();

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, object>? _data;

    private FileSettingsValues()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WON", "localsettings.json"))
    {
    }

    /// <summary>테스트가 임시 파일로 같은 동작을 확인할 수 있게 경로를 받는다.</summary>
    internal FileSettingsValues(string path)
    {
        _path = path;
    }

    public object? this[string key]
    {
        get { lock (_gate) return Data().TryGetValue(key, out var v) ? v : null; }
        set
        {
            lock (_gate)
            {
                if (value == null) Data().Remove(key); else Data()[key] = value;
                Save();
            }
        }
    }

    public bool TryGetValue(string key, out object? value)
    {
        lock (_gate)
        {
            var ok = Data().TryGetValue(key, out var v);
            value = ok ? v : null;
            return ok;
        }
    }

    public bool ContainsKey(string key) { lock (_gate) return Data().ContainsKey(key); }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            var removed = Data().Remove(key);
            if (removed) Save();
            return removed;
        }
    }

    public int Count { get { lock (_gate) return Data().Count; } }

    internal void ReplaceAll(Dictionary<string, object> snapshot)
    {
        lock (_gate)
        {
            _data = new Dictionary<string, object>(snapshot);
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate) { Data().Clear(); Save(); }
    }

    private Dictionary<string, object> Data() => _data ??= Load();

    private Dictionary<string, object> Load()
    {
        var result = new Dictionary<string, object>();
        try
        {
            if (!File.Exists(_path)) return result;
            // ReadAllText 는 UTF-8 BOM(메모장·PowerShell 로 고친 파일)을 알아서 걸러 준다.
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var v = Decode(p.Value);
                if (v != null) result[p.Name] = v;
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[AppLocalSettings] settings file unreadable, starting empty: {ex.Message}");
            try { File.Move(_path, _path + ".bad", overwrite: true); } catch { }
            result.Clear();
        }
        return result;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var kv in _data!)
                {
                    w.WritePropertyName(kv.Key);
                    Encode(w, kv.Value);
                }
                w.WriteEndObject();
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[AppLocalSettings] could not save settings: {ex.Message}");
        }
    }

    private static void Encode(Utf8JsonWriter w, object value)
    {
        w.WriteStartObject();
        switch (value)
        {
            case bool b: w.WriteString("t", "b"); w.WriteBoolean("v", b); break;
            case int i: w.WriteString("t", "i"); w.WriteNumber("v", i); break;
            case long l: w.WriteString("t", "l"); w.WriteNumber("v", l); break;
            case double d: w.WriteString("t", "d"); w.WriteNumber("v", d); break;
            case float f: w.WriteString("t", "d"); w.WriteNumber("v", (double)f); break;
            case string s: w.WriteString("t", "s"); w.WriteString("v", s); break;
            case SettingsComposite c:
                w.WriteString("t", "c");
                w.WriteStartObject("v");
                foreach (var kv in c)
                {
                    if (kv.Value == null) continue;
                    w.WritePropertyName(kv.Key);
                    Encode(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            default: w.WriteString("t", "s"); w.WriteString("v", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""); break;
        }
        w.WriteEndObject();
    }

    private static object? Decode(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("t", out var t) || !e.TryGetProperty("v", out var v)) return null;
        switch (t.GetString())
        {
            case "b": return v.GetBoolean();
            case "i": return v.GetInt32();
            case "l": return v.GetInt64();
            case "d": return v.GetDouble();
            case "s": return v.GetString();
            case "c":
                var c = new SettingsComposite();
                foreach (var p in v.EnumerateObject())
                {
                    var inner = Decode(p.Value);
                    if (inner != null) c[p.Name] = inner;
                }
                return c;
            default: return null;
        }
    }
}
