using PdfSharp.Pdf.IO;
using Span.Services.PdfConversion;

namespace Span.Tests.Unit;

[TestClass]
public class PdfConversionTests
{
    // 8x4 and 4x8 RGB PNG fixtures
    private static readonly byte[] Wide = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAECAIAAAA8r+mnAAAAPklEQVR4nAXBgQAAMADDsGIc4TjDKc5wjjOEJ4AXg2Lx4fAgxBsTjY0vLp4I9dZUa+urq6fCvDPT2fnm5pkfIQ0rgV8PHqsAAAAASUVORK5CYII=");
    private static readonly byte[] Tall = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAQAAAAICAIAAABRUclSAAAARElEQVR4nAXBQQHAIBADsMq55yQgoXIiAwlIQwISliQx0RBJTbVUcszRw5E88/TxJMssXSzJNls3W3LN1cuVfObTj88PMBElgWZE2EQAAAAASUVORK5CYII=");

    private string _dir = "";

    [TestInitialize]
    public void Setup() { _dir = Path.Combine(Path.GetTempPath(), "won-pdf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }

    [TestCleanup]
    public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

    // ── classifier ─────────────────────────────────────────────

    [TestMethod]
    public void Classifier_covers_the_requested_formats()
    {
        Assert.AreEqual(PdfSourceKind.Image, PdfSourceClassifier.Classify("a.PNG"));
        Assert.AreEqual(PdfSourceKind.Image, PdfSourceClassifier.Classify("a.jpg"));
        Assert.AreEqual(PdfSourceKind.Word, PdfSourceClassifier.Classify("a.docx"));
        Assert.AreEqual(PdfSourceKind.Hwp, PdfSourceClassifier.Classify("a.hwp"));
        Assert.AreEqual(PdfSourceKind.Hwp, PdfSourceClassifier.Classify("a.hwpx"));
        Assert.AreEqual(PdfSourceKind.PowerPoint, PdfSourceClassifier.Classify("a.ppt"));
        Assert.AreEqual(PdfSourceKind.Excel, PdfSourceClassifier.Classify("a.xlsx"));
        Assert.AreEqual(PdfSourceKind.Unsupported, PdfSourceClassifier.Classify("a.exe"));
        Assert.AreEqual(PdfSourceKind.Unsupported, PdfSourceClassifier.Classify(null));
    }

    [TestMethod]
    public void Convert_excludes_pdf_but_merge_accepts_it()
    {
        Assert.IsFalse(PdfSourceClassifier.CanConvert("a.pdf"));
        Assert.IsTrue(PdfSourceClassifier.CanMerge("a.pdf"));
        Assert.IsTrue(PdfSourceClassifier.CanConvert("a.docx"));
        Assert.IsFalse(PdfSourceClassifier.CanMerge("a.zip"));
    }

    // ── naming ─────────────────────────────────────────────────

    [TestMethod]
    public void Namer_never_overwrites_and_sanitizes()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { Path.Combine(@"C:\d", "a.pdf"), Path.Combine(@"C:\d", "a (1).pdf") };
        Assert.AreEqual(Path.Combine(@"C:\d", "a (2).pdf"), PdfOutputNamer.Unique(@"C:\d", "a", taken.Contains));
        Assert.AreEqual(Path.Combine(@"C:\d", "b_c.pdf"), PdfOutputNamer.Unique(@"C:\d", "b?c", taken.Contains));
        Assert.AreEqual(Path.Combine(@"C:\d", "document.pdf"), PdfOutputNamer.Unique(@"C:\d", "  ", taken.Contains));
    }

    // ── PDFsharp helpers ───────────────────────────────────────

    [TestMethod]
    public void Images_become_a4_pages_with_orientation_following_the_image()
    {
        var pdf = Path.Combine(_dir, "img.pdf");
        ImagePdfWriter.Write(new[] { Wide, Tall }, pdf);

        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        Assert.AreEqual(2, doc.PageCount);
        Assert.IsTrue(doc.Pages[0].Width.Point > doc.Pages[0].Height.Point, "wide image → landscape");
        Assert.IsTrue(doc.Pages[1].Width.Point < doc.Pages[1].Height.Point, "tall image → portrait");
        Assert.AreEqual(842, Math.Max(doc.Pages[0].Width.Point, doc.Pages[0].Height.Point), 1);
    }

    [TestMethod]
    public void Merge_concatenates_pages_in_order_and_reports_unreadable_files()
    {
        var a = Path.Combine(_dir, "a.pdf"); var b = Path.Combine(_dir, "b.pdf");
        ImagePdfWriter.Write(new[] { Wide }, a);
        ImagePdfWriter.Write(new[] { Tall, Tall }, b);

        var merged = Path.Combine(_dir, "m.pdf");
        Assert.AreEqual(3, PdfMerger.Merge(new[] { a, b }, merged));
        using (var doc = PdfReader.Open(merged, PdfDocumentOpenMode.Import))
            Assert.AreEqual(3, doc.PageCount);

        var bad = Path.Combine(_dir, "bad.pdf");
        File.WriteAllText(bad, "not a pdf");
        var ex = Assert.ThrowsException<InvalidOperationException>(() => PdfMerger.Merge(new[] { a, bad }, Path.Combine(_dir, "x.pdf")));
        StringAssert.Contains(ex.Message, "bad.pdf");
    }

    // ── orchestrator ───────────────────────────────────────────

    private sealed class FakeEngine : IPdfEngine
    {
        public List<string> Seen { get; } = new();
        public bool Available { get; set; } = true;
        public string Name => "Fake";
        public bool Supports(PdfSourceKind kind) => kind is PdfSourceKind.Word or PdfSourceKind.Excel;
        public bool IsAvailable(PdfSourceKind kind) => Available;
        public Task ConvertAsync(string source, string outputPdf, CancellationToken ct)
        {
            Seen.Add(source);
            if (Path.GetFileName(source).StartsWith("boom")) throw new InvalidOperationException("engine failed");
            ImagePdfWriter.Write(new[] { Tall }, outputPdf);
            return Task.CompletedTask;
        }
    }

    private PdfConversionService Service(FakeEngine engine) =>
        new(new IPdfEngine[] { engine }, (_, _) => Task.FromResult(Wide), Path.Combine(_dir, "tmp"));

    private string Touch(string name, byte[]? bytes = null)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes ?? new byte[] { 1, 2, 3 });
        return p;
    }

    [TestMethod]
    public async Task Convert_writes_new_pdfs_next_to_the_sources_and_leaves_originals_untouched()
    {
        var engine = new FakeEngine();
        var doc = Touch("report.docx"); var img = Touch("scan.png", Wide);
        var before = File.ReadAllBytes(doc);

        var result = await Service(engine).RunAsync(new[] { doc, img }, merge: false, progress: null, CancellationToken.None);

        Assert.AreEqual(2, result.Succeeded);
        Assert.IsTrue(File.Exists(Path.Combine(_dir, "report.pdf")));
        Assert.IsTrue(File.Exists(Path.Combine(_dir, "scan.pdf")));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(doc));
        Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "tmp", "x")), "temp work dir is cleaned");
    }

    [TestMethod]
    public async Task Convert_never_overwrites_an_existing_pdf()
    {
        var doc = Touch("a.docx");
        File.WriteAllText(Path.Combine(_dir, "a.pdf"), "keep me");

        var result = await Service(new FakeEngine()).RunAsync(new[] { doc }, false, null, CancellationToken.None);

        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(_dir, "a.pdf")));
        Assert.IsTrue(File.Exists(Path.Combine(_dir, "a (1).pdf")));
        Assert.AreEqual(Path.Combine(_dir, "a (1).pdf"), result.Items[0].Output);
    }

    [TestMethod]
    public async Task One_failure_does_not_stop_the_rest()
    {
        var result = await Service(new FakeEngine()).RunAsync(
            new[] { Touch("boom.docx"), Touch("ok.docx"), Touch("note.exe") }, false, null, CancellationToken.None);

        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(2, result.Failed);
        StringAssert.Contains(result.Items[0].Error, "engine failed");
        StringAssert.Contains(result.Items[2].Error, "unsupported");
    }

    [TestMethod]
    public async Task Missing_engine_gives_an_actionable_message()
    {
        var result = await Service(new FakeEngine { Available = false }).RunAsync(
            new[] { Touch("a.docx") }, false, null, CancellationToken.None);
        StringAssert.Contains(result.Items[0].Error, "Microsoft Office or LibreOffice");
        Assert.IsFalse(File.Exists(Path.Combine(_dir, "a.pdf")));
    }

    [TestMethod]
    public async Task Merge_builds_one_pdf_from_mixed_inputs_including_existing_pdfs()
    {
        var existing = Path.Combine(_dir, "cover.pdf");
        ImagePdfWriter.Write(new[] { Tall }, existing);
        var sources = new[] { existing, Touch("body.docx"), Touch("fig.png", Wide) };

        var result = await Service(new FakeEngine()).RunAsync(sources, merge: true, null, CancellationToken.None);

        Assert.AreEqual(3, result.MergedPages);
        Assert.IsNotNull(result.MergedOutput);
        StringAssert.Contains(Path.GetFileName(result.MergedOutput!), "외 2개 합본");
        using var doc = PdfReader.Open(result.MergedOutput!, PdfDocumentOpenMode.Import);
        Assert.AreEqual(3, doc.PageCount);
        Assert.AreEqual(Path.Combine(_dir), Path.GetDirectoryName(result.MergedOutput));
    }

    [TestMethod]
    public async Task Cancellation_stops_the_batch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            Service(new FakeEngine()).RunAsync(new[] { Touch("a.docx") }, false, null, cts.Token));
    }

    // ── LibreOffice engine (no process started) ────────────────

    [TestMethod]
    public void LibreOffice_is_found_in_program_files_or_path_and_only_when_it_exists()
    {
        var env = new Dictionary<string, string> { ["ProgramFiles"] = @"C:\Program Files", ["PATH"] = @"C:\x;C:\tools" };
        var soffice = @"C:\Program Files\LibreOffice\program\soffice.exe";

        var found = new LibreOfficeEngine(exists: p => p == soffice, env: k => env.GetValueOrDefault(k));
        Assert.AreEqual(soffice, found.FindSoffice());
        Assert.IsTrue(found.IsAvailable(PdfSourceKind.Word));

        var viaPath = new LibreOfficeEngine(exists: p => p == @"C:\tools\soffice.exe", env: k => env.GetValueOrDefault(k));
        Assert.AreEqual(@"C:\tools\soffice.exe", viaPath.FindSoffice());

        var none = new LibreOfficeEngine(exists: _ => false, env: k => env.GetValueOrDefault(k));
        Assert.IsFalse(none.IsAvailable(PdfSourceKind.Word));
    }

    [TestMethod]
    public void LibreOffice_arguments_use_a_private_profile_and_pass_the_path_as_one_argument()
    {
        var args = LibreOfficeEngine.BuildArguments(@"C:\Users\me\AppData\Local\WON\lo profile", @"C:\out", @"C:\docs\my report.docx");
        CollectionAssert.Contains(args.ToList(), "--headless");
        CollectionAssert.Contains(args.ToList(), @"C:\docs\my report.docx");
        var env = args.Single(a => a.StartsWith("-env:UserInstallation="));
        StringAssert.StartsWith(env, "-env:UserInstallation=file:///C:/Users/me/AppData/Local/WON/lo%20profile");
        Assert.IsTrue(args.ToList().IndexOf("--convert-to") + 1 == args.ToList().IndexOf("pdf"));
    }

    [TestMethod]
    public void LibreOffice_supports_documents_but_not_images()
    {
        var lo = new LibreOfficeEngine(exists: _ => false);
        Assert.IsTrue(lo.Supports(PdfSourceKind.Hwp));
        Assert.IsTrue(lo.Supports(PdfSourceKind.PowerPoint));
        Assert.IsFalse(lo.Supports(PdfSourceKind.Image));
    }
}
