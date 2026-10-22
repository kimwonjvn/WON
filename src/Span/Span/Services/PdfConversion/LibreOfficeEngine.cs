using System.Diagnostics;

namespace Span.Services.PdfConversion;

/// <summary>
/// 오픈소스 엔진: LibreOffice(<c>soffice --headless --convert-to pdf</c>). 설치돼 있을 때만 쓴다.
/// HWP/HWPX 는 LibreOffice 에 H2Orestart 확장(GPL, github.com/ebandal/H2Orestart)이 설치돼 있어야 열린다.
/// 사용자의 LibreOffice 설정과 섞이지 않도록 WON 전용 프로필 폴더를 쓴다.
/// </summary>
public sealed class LibreOfficeEngine : IPdfEngine
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly Func<string, bool> _exists;
    private readonly Func<string, string?> _env;
    private readonly string _profileDir;
    private string? _cached;
    private bool _searched;

    public LibreOfficeEngine(string? profileDir = null, Func<string, bool>? exists = null, Func<string, string?>? env = null)
    {
        _exists = exists ?? File.Exists;
        _env = env ?? Environment.GetEnvironmentVariable;
        _profileDir = profileDir ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WON", "libreoffice-profile");
    }

    public string Name => "LibreOffice";

    public bool Supports(PdfSourceKind kind) =>
        kind is PdfSourceKind.Word or PdfSourceKind.Excel or PdfSourceKind.PowerPoint or PdfSourceKind.Hwp or PdfSourceKind.Other;

    public bool IsAvailable(PdfSourceKind kind) => FindSoffice() != null;

    public static IEnumerable<string> CandidatePaths(Func<string, string?> env)
    {
        foreach (var root in new[] { env("ProgramFiles"), env("ProgramW6432"), env("ProgramFiles(x86)") })
            if (!string.IsNullOrEmpty(root)) yield return System.IO.Path.Combine(root, "LibreOffice", "program", "soffice.exe");
        var local = env("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(local)) yield return System.IO.Path.Combine(local, "Programs", "LibreOffice", "program", "soffice.exe");
        foreach (var dir in (env("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return System.IO.Path.Combine(dir.Trim().Trim('"'), "soffice.exe");
    }

    public string? FindSoffice()
    {
        if (_searched && (_cached == null || _exists(_cached))) return _cached;
        _searched = true;
        _cached = CandidatePaths(_env).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(_exists);
        return _cached;
    }

    internal static IReadOnlyList<string> BuildArguments(string profileDir, string outDir, string source) => new[]
    {
        "--headless", "--norestore", "--nolockcheck", "--nodefault", "--nofirststartwizard",
        $"-env:UserInstallation={new Uri(profileDir.TrimEnd('\\') + "\\").AbsoluteUri.TrimEnd('/')}",
        "--convert-to", "pdf",
        "--outdir", outDir,
        source,
    };

    public async Task ConvertAsync(string source, string outputPdf, CancellationToken ct)
    {
        var soffice = FindSoffice() ?? throw new FileNotFoundException("LibreOffice (soffice.exe) was not found");
        Directory.CreateDirectory(_profileDir);
        var outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WON-lo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in BuildArguments(_profileDir, outDir, source)) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start LibreOffice");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try { await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException("LibreOffice took longer than 3 minutes");
            }

            var produced = System.IO.Path.Combine(outDir, System.IO.Path.GetFileNameWithoutExtension(source) + ".pdf");
            if (!File.Exists(produced))
            {
                var err = (await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                throw new InvalidOperationException(
                    PdfSourceClassifier.Classify(source) == PdfSourceKind.Hwp
                        ? "LibreOffice could not open this HWP file (is the H2Orestart extension installed?)"
                        : (err.Length > 0 ? err : "LibreOffice produced no PDF"));
            }
            File.Move(produced, outputPdf, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { }
        }
    }
}
