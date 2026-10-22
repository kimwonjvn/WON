using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Span.Services.PdfConversion;

public enum PdfSourceKind { Unsupported, Image, Word, Excel, PowerPoint, Hwp, Other, Pdf }

/// <summary>확장자로 PDF 변환 가능 여부와 종류를 판단한다.</summary>
public static class PdfSourceClassifier
{
    private static readonly Dictionary<string, PdfSourceKind> Map = Build();

    private static Dictionary<string, PdfSourceKind> Build()
    {
        var d = new Dictionary<string, PdfSourceKind>(StringComparer.OrdinalIgnoreCase);
        void Add(PdfSourceKind k, params string[] exts) { foreach (var e in exts) d[e] = k; }
        Add(PdfSourceKind.Image, ".png", ".jpg", ".jpeg", ".jpe", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif");
        Add(PdfSourceKind.Word, ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf");
        Add(PdfSourceKind.Excel, ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv");
        Add(PdfSourceKind.PowerPoint, ".ppt", ".pptx", ".pptm", ".pps", ".ppsx");
        Add(PdfSourceKind.Hwp, ".hwp", ".hwpx");
        Add(PdfSourceKind.Other, ".odt", ".ods", ".odp", ".txt");
        Add(PdfSourceKind.Pdf, ".pdf");
        return d;
    }

    public static PdfSourceKind Classify(string? path)
    {
        if (string.IsNullOrEmpty(path)) return PdfSourceKind.Unsupported;
        return Map.TryGetValue(System.IO.Path.GetExtension(path), out var k) ? k : PdfSourceKind.Unsupported;
    }

    /// <summary>"PDF로 변환" 대상 (이미 PDF 인 파일은 제외).</summary>
    public static bool CanConvert(string? path)
    {
        var k = Classify(path);
        return k != PdfSourceKind.Unsupported && k != PdfSourceKind.Pdf;
    }

    /// <summary>"PDF로 합치기" 대상 (PDF 도 포함).</summary>
    public static bool CanMerge(string? path) => Classify(path) != PdfSourceKind.Unsupported;
}

/// <summary>이미지(PNG/JPEG 바이트)를 A4 PDF 로 만든다. 가로/세로는 이미지 비율을 따르고 페이지에 맞춰 축소·확대한다.</summary>
public static class ImagePdfWriter
{
    public const double MarginPoints = 28;

    public static void Write(IReadOnlyList<byte[]> images, string outputPath)
    {
        if (images.Count == 0) throw new ArgumentException("no images", nameof(images));
        using var doc = new PdfDocument();
        foreach (var bytes in images)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var img = XImage.FromStream(ms);
            var page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            page.Orientation = img.PixelWidth > img.PixelHeight ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;

            double areaW = page.Width.Point - 2 * MarginPoints;
            double areaH = page.Height.Point - 2 * MarginPoints;
            double scale = Math.Min(areaW / img.PixelWidth, areaH / img.PixelHeight);
            double w = img.PixelWidth * scale, h = img.PixelHeight * scale;
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(img, (page.Width.Point - w) / 2, (page.Height.Point - h) / 2, w, h);
        }
        doc.Save(outputPath);
    }
}

public static class PdfMerger
{
    /// <summary>입력 PDF 들을 순서대로 한 파일로 합친다. 암호화된 PDF 등을 열 수 없으면 파일 이름을 담아 예외를 던진다.</summary>
    public static int Merge(IReadOnlyList<string> inputs, string outputPath)
    {
        using var output = new PdfDocument();
        foreach (var file in inputs)
        {
            try
            {
                using var input = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                foreach (PdfPage page in input.Pages) output.AddPage(page);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{System.IO.Path.GetFileName(file)}: {ex.Message}", ex);
            }
        }
        if (output.PageCount == 0) throw new InvalidOperationException("no pages");
        int pages = output.PageCount; // 저장 후에는 PDFsharp 문서를 더 읽을 수 없다
        output.Save(outputPath);
        return pages;
    }
}

public interface IPdfEngine
{
    string Name { get; }
    bool Supports(PdfSourceKind kind);
    bool IsAvailable(PdfSourceKind kind);
    /// <summary>source 를 읽기 전용으로 열어 outputPdf 로 저장한다. 원본은 바꾸지 않는다.</summary>
    Task ConvertAsync(string source, string outputPdf, CancellationToken ct);
}

public sealed record PdfItemResult(string Source, string? Output, string? Error)
{
    public bool Ok => Error == null;
}

public sealed record PdfBatchResult(IReadOnlyList<PdfItemResult> Items, string? MergedOutput, int MergedPages = 0)
{
    public int Succeeded => Items.Count(i => i.Ok);
    public int Failed => Items.Count(i => !i.Ok);
}

public sealed record PdfProgress(int Done, int Total, string CurrentName);

public static class PdfOutputNamer
{
    /// <summary>folder\name.pdf 가 이미 있으면 "name (1).pdf", "name (2).pdf" … 로 비어 있는 이름을 고른다. 덮어쓰지 않는다.</summary>
    public static string Unique(string folder, string baseName, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var safe = string.Concat(baseName.Select(c => Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c)).Trim();
        if (safe.Length == 0) safe = "document";
        var candidate = System.IO.Path.Combine(folder, safe + ".pdf");
        for (int i = 1; exists(candidate) && i < 10_000; i++)
            candidate = System.IO.Path.Combine(folder, $"{safe} ({i}).pdf");
        return candidate;
    }
}

/// <summary>
/// 파일 여러 개를 PDF 로 변환(또는 하나로 합치기)한다. 이미지는 내장 PDFsharp 로, 문서는 사용 가능한 엔진
/// (한컴 / Microsoft Office / LibreOffice) 중 첫 번째로 변환한다. 원본은 절대 바꾸거나 지우지 않고,
/// 결과는 원본 옆에 새 파일로만 만든다(기존 파일을 덮어쓰지 않는다).
/// </summary>
public sealed class PdfConversionService
{
    public const long MaxSourceBytes = 512L * 1024 * 1024;

    private readonly IReadOnlyList<IPdfEngine> _engines;
    private readonly Func<string, CancellationToken, Task<byte[]>> _imageLoader;
    private readonly string _tempRoot;

    /// <param name="engines">우선순위 순서</param>
    /// <param name="imageLoader">이미지 파일 → PNG/JPEG 바이트 (EXIF 회전 적용, 형식 변환)</param>
    public PdfConversionService(IReadOnlyList<IPdfEngine> engines,
        Func<string, CancellationToken, Task<byte[]>> imageLoader, string? tempRoot = null)
    {
        _engines = engines;
        _imageLoader = imageLoader;
        _tempRoot = tempRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WON-pdf");
    }

    public IPdfEngine? PickEngine(PdfSourceKind kind) => _engines.FirstOrDefault(e => e.Supports(kind) && e.IsAvailable(kind));

    public static string MissingEngineMessage(PdfSourceKind kind) => kind switch
    {
        PdfSourceKind.Hwp => "HWP needs Hancom Office, or LibreOffice with the H2Orestart extension",
        PdfSourceKind.Word or PdfSourceKind.Excel or PdfSourceKind.PowerPoint => "needs Microsoft Office or LibreOffice installed",
        _ => "needs LibreOffice installed",
    };

    public async Task<PdfBatchResult> RunAsync(IReadOnlyList<string> sources, bool merge,
        IProgress<PdfProgress>? progress, CancellationToken ct)
    {
        var results = new List<PdfItemResult>();
        var temps = new List<string>();
        var work = System.IO.Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            int done = 0;
            foreach (var src in sources)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new PdfProgress(done, sources.Count, System.IO.Path.GetFileName(src)));
                var target = merge
                    ? System.IO.Path.Combine(work, $"{temps.Count:D3}.pdf")
                    : PdfOutputNamer.Unique(System.IO.Path.GetDirectoryName(src) ?? work, System.IO.Path.GetFileNameWithoutExtension(src));
                try
                {
                    await ConvertOneAsync(src, target, work, merge, ct).ConfigureAwait(false);
                    results.Add(new PdfItemResult(src, merge ? null : target, null));
                    if (merge) temps.Add(target);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results.Add(new PdfItemResult(src, null, ex.Message));
                }
                done++;
            }
            progress?.Report(new PdfProgress(done, sources.Count, string.Empty));

            if (!merge) return new PdfBatchResult(results, null);

            if (temps.Count == 0) return new PdfBatchResult(results, null);
            var first = sources[0];
            var folder = System.IO.Path.GetDirectoryName(first) ?? work;
            var name = sources.Count > 1
                ? $"{System.IO.Path.GetFileNameWithoutExtension(first)} 외 {sources.Count - 1}개 합본"
                : System.IO.Path.GetFileNameWithoutExtension(first);
            var merged = PdfOutputNamer.Unique(folder, name);
            var tmpOut = System.IO.Path.Combine(work, "merged.pdf");
            int pages = await Task.Run(() => PdfMerger.Merge(temps, tmpOut), ct).ConfigureAwait(false);
            File.Move(tmpOut, merged);
            return new PdfBatchResult(results, merged, pages);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 임시 폴더 정리 실패는 무시 */ }
        }
    }

    private async Task ConvertOneAsync(string src, string target, string work, bool toTemp, CancellationToken ct)
    {
        if (!File.Exists(src)) throw new FileNotFoundException("file not found");
        var info = new FileInfo(src);
        if (info.Length > MaxSourceBytes) throw new InvalidOperationException("file is larger than 512 MB");

        var kind = PdfSourceClassifier.Classify(src);
        switch (kind)
        {
            case PdfSourceKind.Unsupported:
                throw new NotSupportedException("unsupported file type");
            case PdfSourceKind.Pdf:
                if (!toTemp) throw new NotSupportedException("already a PDF");
                File.Copy(src, target, overwrite: true);
                return;
            case PdfSourceKind.Image:
            {
                var bytes = await _imageLoader(src, ct).ConfigureAwait(false);
                var staging = System.IO.Path.Combine(work, Guid.NewGuid().ToString("N") + ".pdf");
                ImagePdfWriter.Write(new[] { bytes }, staging);
                Place(staging, target);
                return;
            }
            default:
            {
                var engine = PickEngine(kind) ?? throw new NotSupportedException(MissingEngineMessage(kind));
                var staging = System.IO.Path.Combine(work, Guid.NewGuid().ToString("N") + ".pdf");
                await engine.ConvertAsync(src, staging, ct).ConfigureAwait(false);
                if (!File.Exists(staging) || new FileInfo(staging).Length == 0)
                    throw new InvalidOperationException($"{engine.Name} produced no PDF");
                Place(staging, target);
                return;
            }
        }
    }

    private static void Place(string staging, string target)
    {
        // 결과 파일 이름이 겹치지 않도록 이동 직전에 한 번 더 확인한다 (덮어쓰기 금지).
        if (File.Exists(target)) throw new IOException("output already exists");
        File.Move(staging, target);
    }
}
