using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Span.Services.Assistant;

/// <summary>
/// 어시스턴트가 다룰 수 있는 경로의 안전 규칙.
/// 모델이 만든 경로는 신뢰하지 않고, 읽기/변경 모두 이 규칙을 통과해야 한다.
/// </summary>
public static class AssistantPathPolicy
{
    /// <summary>로컬/UNC 절대 경로만 허용. 원격(ftp://), 아카이브(archive://), 장치 경로(\\?\, \\.\)는 거부.</summary>
    public static bool TryNormalize(string? path, out string full, out string error)
    {
        full = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path)) { error = "path is empty"; return false; }
        if (path.Contains('\0')) { error = "path contains an invalid character"; return false; }
        if (path.Contains("://", StringComparison.Ordinal)) { error = "remote and archive paths are not supported"; return false; }
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        { error = "device paths are not allowed"; return false; }
        if (!Path.IsPathFullyQualified(path)) { error = "an absolute path is required"; return false; }

        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { error = $"invalid path: {ex.Message}"; return false; }
        return true;
    }

    /// <summary>변경 작업이 금지된 위치: 드라이브 루트, 사용자 프로필 루트·주요 폴더 자체, Windows/Program Files/ProgramData 하위 전체.</summary>
    public static bool IsProtected(string fullPath)
    {
        var p = TrimEnd(fullPath);

        // 드라이브 루트(C:\) 또는 UNC 루트(\\server, \\server\share)
        var root = TrimEnd(Path.GetPathRoot(fullPath) ?? string.Empty);
        if (p.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith(@"\\", StringComparison.Ordinal) && p.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length <= 2) return true;

        foreach (var tree in ProtectedTrees())
            if (IsSameOrUnder(p, tree)) return true;

        foreach (var exact in ProtectedExact())
            if (p.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>변경 작업용 경로 검증: 정규화 + 보호 위치 거부.</summary>
    public static bool TryValidateMutationPath(string? path, out string full, out string error)
    {
        if (!TryNormalize(path, out full, out error)) return false;
        if (IsProtected(full)) { error = "this location is protected and cannot be modified"; return false; }
        return true;
    }

    /// <summary>이름 변경/생성용 단일 경로 구성요소 검증.</summary>
    public static bool IsValidName(string? name, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name)) { error = "name is empty"; return false; }
        if (name != name.Trim() || name.EndsWith('.')) { error = "name has leading/trailing spaces or a trailing dot"; return false; }
        if (name is "." or "..") { error = "invalid name"; return false; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { error = "name contains invalid characters"; return false; }
        return true;
    }

    public static bool IsSameOrUnder(string path, string folder)
    {
        var p = TrimEnd(path);
        var f = TrimEnd(folder);
        if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimEnd(string p) =>
        p.Length > 3 ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IEnumerable<string> ProtectedTrees()
    {
        foreach (var f in new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
        })
        {
            var s = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(s)) yield return TrimEnd(s);
        }
    }

    private static IEnumerable<string> ProtectedExact()
    {
        foreach (var f in new[]
        {
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
        })
        {
            var s = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(s)) yield return TrimEnd(s);
        }
    }
}

public enum PlannedOperationKind { Move, Copy, Rename, CreateFolder, Delete }

/// <summary>모델이 제안한, 사용자 승인을 기다리는 변경 작업 하나.</summary>
public sealed record PlannedOperation(PlannedOperationKind Kind, string Source, string? Target)
{
    public string Describe()
    {
        bool ko = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
        return Kind switch
        {
            PlannedOperationKind.Move => $"{(ko ? "이동" : "Move")}: {Source} → {Target}",
            PlannedOperationKind.Copy => $"{(ko ? "복사" : "Copy")}: {Source} → {Target}",
            PlannedOperationKind.Rename => $"{(ko ? "이름 변경" : "Rename")}: {Source} → {Target}",
            PlannedOperationKind.CreateFolder => $"{(ko ? "폴더 만들기" : "Create folder")}: {Source}",
            PlannedOperationKind.Delete => $"{(ko ? "휴지통으로 삭제" : "Delete (Recycle Bin)")}: {Source}",
            _ => Source
        };
    }
}
