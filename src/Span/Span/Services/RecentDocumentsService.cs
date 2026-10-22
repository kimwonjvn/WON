namespace Span.Services;

public enum RecentDocKind { Excel, PowerPoint, Document }

public sealed record RecentDocument(string Path, string Name, DateTime LastUsed, RecentDocKind Kind);

/// <summary>
/// 사이드바 "최근 문서": Windows 의 최근 항목(Recent) 바로가기에서 엑셀/파워포인트/문서를 골라낸다.
/// 파일 내용은 읽지 않고, 바로가기 이름·수정 시각·대상 경로만 사용한다.
/// </summary>
public static class RecentDocumentsService
{
    private static readonly HashSet<string> ExcelExt = new(StringComparer.OrdinalIgnoreCase)
        { ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv" };
    private static readonly HashSet<string> PowerPointExt = new(StringComparer.OrdinalIgnoreCase)
        { ".ppt", ".pptx", ".pptm", ".pps", ".ppsx" };
    private static readonly HashSet<string> DocumentExt = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".docm", ".rtf", ".odt", ".hwp", ".hwpx", ".pdf" };

    public static RecentDocKind? Classify(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var ext = System.IO.Path.GetExtension(fileName);
        if (ExcelExt.Contains(ext)) return RecentDocKind.Excel;
        if (PowerPointExt.Contains(ext)) return RecentDocKind.PowerPoint;
        if (DocumentExt.Contains(ext)) return RecentDocKind.Document;
        return null;
    }

    /// <summary>Recent 폴더의 .lnk 를 최근 수정 순으로 나열한다 (접근 불가면 빈 목록).</summary>
    public static IReadOnlyList<(string LnkPath, DateTime LastWrite)> EnumerateRecentLinks(string recentFolder, int max = 600)
    {
        try
        {
            if (!System.IO.Directory.Exists(recentFolder)) return Array.Empty<(string, DateTime)>();
            return new System.IO.DirectoryInfo(recentFolder)
                .EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTime)
                .Take(max)
                .Select(f => (f.FullName, f.LastWriteTime))
                .ToList();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return Array.Empty<(string, DateTime)>();
        }
    }

    /// <param name="links">최근 사용 순으로 정렬된 (lnk 경로, 시각)</param>
    /// <param name="resolve">.lnk → 대상 경로</param>
    /// <param name="exists">대상 파일 존재 여부 (네트워크 경로는 호출자가 true 로 두어도 됨)</param>
    public static Dictionary<RecentDocKind, List<RecentDocument>> Collect(
        IEnumerable<(string LnkPath, DateTime LastWrite)> links,
        Func<string, string?> resolve,
        Func<string, bool> exists,
        int perKind = 15)
    {
        var result = new Dictionary<RecentDocKind, List<RecentDocument>>
        {
            [RecentDocKind.Excel] = new(),
            [RecentDocKind.PowerPoint] = new(),
            [RecentDocKind.Document] = new(),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (lnk, time) in links)
        {
            // 'report.xlsx.lnk' → 'report.xlsx' : 확장자만 보고 먼저 거른다 (대상 해석 비용 절약)
            var shortcutName = System.IO.Path.GetFileNameWithoutExtension(lnk);
            var kind = Classify(shortcutName);
            if (kind == null || result[kind.Value].Count >= perKind) continue;

            string? target;
            try { target = resolve(lnk); } catch { continue; }
            if (string.IsNullOrWhiteSpace(target)) continue;

            var name = System.IO.Path.GetFileName(target);
            if (name.StartsWith("~$", StringComparison.Ordinal)) continue; // Office 임시 잠금 파일
            if (Classify(name) != kind) continue;                          // 대상이 바뀐/다른 형식인 경우
            if (!seen.Add(target)) continue;
            if (!exists(target)) continue;

            result[kind.Value].Add(new RecentDocument(target, name, time, kind.Value));

            if (result.Values.All(l => l.Count >= perKind)) break;
        }
        return result;
    }
}
