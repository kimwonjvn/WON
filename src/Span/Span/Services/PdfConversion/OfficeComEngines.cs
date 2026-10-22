using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Span.Services.PdfConversion;

/// <summary>
/// 이미 설치된 Microsoft Office / 한컴오피스를 COM 자동화로 불러 문서를 PDF 로 내보낸다 (사용자 PC 에 있을 때만).
/// 원본은 읽기 전용으로 열고, 저장하지 않고 닫는다. 이미 실행 중인 프로그램의 창/설정은 건드리지 않는다.
/// 참고: github.com/cognidox/OfficeToPDF (Apache-2.0) 와 같은 방식(Office 내장 PDF 내보내기).
/// </summary>
internal static class ComHost
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static bool IsRegistered(string progId)
    {
        try { return Type.GetTypeFromProgID(progId, throwOnError: false) != null; }
        catch { return false; }
    }

    private static HashSet<int> Snapshot(string processName)
    {
        try { return Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet(); }
        catch { return new HashSet<int>(); }
    }

    public static bool IsRunning(string processName) => Snapshot(processName).Count > 0;

    /// <summary>COM 은 STA 스레드에서 호출한다. 시간 초과/취소 시 이번에 새로 뜬 프로세스만 종료한다.</summary>
    public static async Task RunStaAsync(Action work, string processName, CancellationToken ct)
    {
        var before = Snapshot(processName);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "WON-pdf-com" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(Timeout, ct)).ConfigureAwait(false);
        if (finished == tcs.Task) { await tcs.Task.ConfigureAwait(false); return; }

        KillNewProcesses(processName, before);
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        throw new TimeoutException("the program did not finish in 2 minutes (a password or dialog may be waiting)");
    }

    private static void KillNewProcesses(string processName, HashSet<int> before)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
                if (!before.Contains(p.Id)) { try { p.Kill(); } catch { } }
        }
        catch { }
    }

    public static dynamic Create(string progId)
    {
        var t = Type.GetTypeFromProgID(progId, throwOnError: true)!;
        return Activator.CreateInstance(t)!;
    }

    public static void Release(object? o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
    }
}

public sealed class MsOfficeEngine : IPdfEngine
{
    private const string NoPassword = "WON-no-password";

    public string Name => "Microsoft Office";

    public bool Supports(PdfSourceKind kind) => kind is PdfSourceKind.Word or PdfSourceKind.Excel or PdfSourceKind.PowerPoint;

    public bool IsAvailable(PdfSourceKind kind) => kind switch
    {
        PdfSourceKind.Word => ComHost.IsRegistered("Word.Application"),
        PdfSourceKind.Excel => ComHost.IsRegistered("Excel.Application"),
        PdfSourceKind.PowerPoint => ComHost.IsRegistered("PowerPoint.Application"),
        _ => false,
    };

    public Task ConvertAsync(string source, string outputPdf, CancellationToken ct) => PdfSourceClassifier.Classify(source) switch
    {
        PdfSourceKind.Word => ComHost.RunStaAsync(() => Word(source, outputPdf), "WINWORD", ct),
        PdfSourceKind.Excel => ComHost.RunStaAsync(() => Excel(source, outputPdf), "EXCEL", ct),
        PdfSourceKind.PowerPoint => ComHost.RunStaAsync(() => PowerPoint(source, outputPdf), "POWERPNT", ct),
        _ => throw new NotSupportedException("not an Office file"),
    };

    private static void Word(string src, string pdf)
    {
        bool wasRunning = ComHost.IsRunning("WINWORD");
        dynamic? app = null, doc = null;
        try
        {
            app = ComHost.Create("Word.Application");
            if (!wasRunning) { app.Visible = false; app.DisplayAlerts = 0; app.AutomationSecurity = 3; } // 3 = 매크로 모두 차단
            doc = app.Documents.Open(FileName: src, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                PasswordDocument: NoPassword, Visible: false, NoEncodingDialog: true);
            doc.ExportAsFixedFormat(pdf, 17); // wdExportFormatPDF
        }
        finally
        {
            try { doc?.Close(0); } catch { }          // wdDoNotSaveChanges
            try { if (!wasRunning) app?.Quit(0); } catch { }
            ComHost.Release(doc); ComHost.Release(app);
        }
    }

    private static void Excel(string src, string pdf)
    {
        bool wasRunning = ComHost.IsRunning("EXCEL");
        dynamic? app = null, wb = null;
        try
        {
            app = ComHost.Create("Excel.Application");
            if (!wasRunning) { app.Visible = false; app.DisplayAlerts = false; app.AutomationSecurity = 3; app.ScreenUpdating = false; }
            wb = app.Workbooks.Open(Filename: src, UpdateLinks: 0, ReadOnly: true, Password: NoPassword,
                IgnoreReadOnlyRecommended: true, AddToMru: false);
            wb.ExportAsFixedFormat(0, pdf); // xlTypePDF
        }
        finally
        {
            try { wb?.Close(false); } catch { }
            try { if (!wasRunning) app?.Quit(); } catch { }
            ComHost.Release(wb); ComHost.Release(app);
        }
    }

    private static void PowerPoint(string src, string pdf)
    {
        bool wasRunning = ComHost.IsRunning("POWERPNT");
        dynamic? app = null, pres = null;
        try
        {
            app = ComHost.Create("PowerPoint.Application");
            if (!wasRunning) { try { app.AutomationSecurity = 3; } catch { } }
            pres = app.Presentations.Open(FileName: src, ReadOnly: -1, Untitled: 0, WithWindow: 0);
            pres.SaveAs(pdf, 32); // ppSaveAsPDF
        }
        finally
        {
            try { pres?.Close(); } catch { }
            try { if (!wasRunning) app?.Quit(); } catch { }
            ComHost.Release(pres); ComHost.Release(app);
        }
    }
}

/// <summary>한컴오피스(한글)가 설치돼 있으면 HWP/HWPX 를 한글 자체 PDF 저장으로 변환한다.</summary>
public sealed class HancomEngine : IPdfEngine
{
    public string Name => "Hancom Office";

    public bool Supports(PdfSourceKind kind) => kind == PdfSourceKind.Hwp;

    public bool IsAvailable(PdfSourceKind kind) => kind == PdfSourceKind.Hwp && ComHost.IsRegistered("HWPFrame.HwpObject");

    public Task ConvertAsync(string source, string outputPdf, CancellationToken ct) =>
        ComHost.RunStaAsync(() => Convert(source, outputPdf), "Hwp", ct);

    private static void Convert(string src, string pdf)
    {
        dynamic? hwp = null;
        try
        {
            hwp = ComHost.Create("HWPFrame.HwpObject");
            // 파일 접근 보안 확인 창이 뜨지 않도록 보안 모듈을 등록한다 (등록돼 있지 않으면 무시).
            try { hwp.RegisterModule("FilePathCheckDLL", "FilePathCheckerModule"); } catch { }
            try { hwp.XHwpWindows.Item(0).Visible = false; } catch { }
            if (!(bool)hwp.Open(src, "", "forceopen:true;suspendpassword:true"))
                throw new InvalidOperationException("Hancom Office could not open the file");
            if (!(bool)hwp.SaveAs(pdf, "PDF", ""))
                throw new InvalidOperationException("Hancom Office could not save a PDF");
        }
        finally
        {
            try { hwp?.Clear(1); } catch { }   // 1 = 변경 내용 버리고 닫기
            try { hwp?.Quit(); } catch { }
            ComHost.Release(hwp);
        }
    }
}
