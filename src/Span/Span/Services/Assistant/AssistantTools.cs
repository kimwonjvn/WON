using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Span.Services.Assistant;

/// <summary>모델에 노출하는 도구 정의 (공급자 중립). SchemaJson은 JSON Schema 객체 문자열.</summary>
public sealed record ToolDef(string Name, string Description, string SchemaJson);

public static class AssistantToolCatalog
{
    public const int MaxListEntries = 300;
    public const int MaxSearchResults = 200;

    public static bool IsMutating(string toolName) => toolName.StartsWith("propose_", StringComparison.Ordinal);

    public static readonly IReadOnlyList<ToolDef> All = new[]
    {
        new ToolDef("list_directory",
            "List the files and folders directly inside a directory (read-only, runs immediately).",
            """{"type":"object","properties":{"path":{"type":"string","description":"Absolute directory path"}},"required":["path"]}"""),
        new ToolDef("search_files",
            "Recursively search under a root folder for files/folders matching a wildcard pattern (read-only). Optionally only items not modified for N days.",
            """{"type":"object","properties":{"root":{"type":"string","description":"Absolute folder to search under"},"pattern":{"type":"string","description":"Wildcard such as *.log or report*"},"older_than_days":{"type":"integer","description":"Only items last modified more than this many days ago"},"max_results":{"type":"integer","description":"Default 100, max 200"}},"required":["root","pattern"]}"""),
        new ToolDef("get_file_info",
            "Get size, type and timestamps of one file or folder (read-only).",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        new ToolDef("propose_move",
            "Propose moving a file/folder into a destination directory. NOT executed now: queued for the user to approve.",
            """{"type":"object","properties":{"source":{"type":"string"},"destination_directory":{"type":"string"}},"required":["source","destination_directory"]}"""),
        new ToolDef("propose_copy",
            "Propose copying a file/folder into a destination directory. NOT executed now: queued for the user to approve.",
            """{"type":"object","properties":{"source":{"type":"string"},"destination_directory":{"type":"string"}},"required":["source","destination_directory"]}"""),
        new ToolDef("propose_rename",
            "Propose renaming a file/folder (new_name is the name only, no path). NOT executed now: queued for the user to approve.",
            """{"type":"object","properties":{"path":{"type":"string"},"new_name":{"type":"string"}},"required":["path","new_name"]}"""),
        new ToolDef("propose_create_folder",
            "Propose creating a new folder at an absolute path. NOT executed now: queued for the user to approve.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        new ToolDef("propose_delete",
            "Propose moving a file/folder to the Recycle Bin. NOT executed now: queued for the user to approve. Only use when the user explicitly asked to delete.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
    };
}

/// <summary>읽기 전용 도구 구현. 결과는 모델에 되돌려 줄 JSON 문자열.</summary>
public sealed class AssistantReadTools
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public bool IsReadTool(string name) => name is "list_directory" or "search_files" or "get_file_info";

    public string Execute(string name, JsonElement args)
    {
        try
        {
            return name switch
            {
                "list_directory" => ListDirectory(Str(args, "path")),
                "search_files" => SearchFiles(Str(args, "root"), Str(args, "pattern"), OptInt(args, "older_than_days"), OptInt(args, "max_results")),
                "get_file_info" => GetFileInfo(Str(args, "path")),
                _ => Error($"unknown tool '{name}'")
            };
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    public string ListDirectory(string? path)
    {
        if (!AssistantPathPolicy.TryNormalize(path, out var full, out var err)) return Error(err);
        if (!Directory.Exists(full)) return Error("directory not found");

        var entries = new List<object>();
        bool truncated = false;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos("*", opts))
        {
            if (entries.Count >= AssistantToolCatalog.MaxListEntries) { truncated = true; break; }
            entries.Add(Describe(info));
        }
        return JsonSerializer.Serialize(new { path = full, count = entries.Count, truncated, entries }, Json);
    }

    public string SearchFiles(string? root, string? pattern, int? olderThanDays, int? maxResults)
    {
        if (!AssistantPathPolicy.TryNormalize(root, out var full, out var err)) return Error(err);
        if (!Directory.Exists(full)) return Error("directory not found");
        if (string.IsNullOrWhiteSpace(pattern)) return Error("pattern is empty");

        int max = Math.Clamp(maxResults ?? 100, 1, AssistantToolCatalog.MaxSearchResults);
        DateTime? cutoff = olderThanDays is > 0 ? DateTime.UtcNow.AddDays(-olderThanDays.Value) : null;

        var results = new List<object>();
        bool truncated = false;
        int scanned = 0;
        var sw = Stopwatch.StartNew();
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchType = MatchType.Win32,
            AttributesToSkip = FileAttributes.ReparsePoint, // 링크 순환 방지
        };

        foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos(pattern, opts))
        {
            if (++scanned > 100_000 || sw.Elapsed > TimeSpan.FromSeconds(10)) { truncated = true; break; }
            if (cutoff != null && info.LastWriteTimeUtc > cutoff) continue;
            if (results.Count >= max) { truncated = true; break; }
            results.Add(DescribeFull(info));
        }
        return JsonSerializer.Serialize(new { root = full, pattern, count = results.Count, truncated, results }, Json);
    }

    public string GetFileInfo(string? path)
    {
        if (!AssistantPathPolicy.TryNormalize(path, out var full, out var err)) return Error(err);
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!info.Exists) return Error("not found");
        return JsonSerializer.Serialize(DescribeFull(info), Json);
    }

    private static object Describe(FileSystemInfo i) => new
    {
        name = i.Name,
        type = i is DirectoryInfo ? "dir" : "file",
        size = i is FileInfo f ? f.Length : (long?)null,
        modified = i.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
    };

    private static object DescribeFull(FileSystemInfo i) => new
    {
        path = i.FullName,
        type = i is DirectoryInfo ? "dir" : "file",
        size = i is FileInfo f ? f.Length : (long?)null,
        modified = i.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
        created = i.CreationTime.ToString("yyyy-MM-dd HH:mm"),
    };

    public static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);

    internal static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int? OptInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
