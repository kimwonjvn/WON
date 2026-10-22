using System.Runtime.InteropServices.WindowsRuntime;
using PdfSharp.Pdf.IO;
using Span.Services.PdfConversion;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Span.Tests.Integration;

/// <summary>실제 Windows 이미지 코덱과(선택적으로) 설치된 Office 로 PDF 가 만들어지는지 확인한다.</summary>
[TestClass]
public class PdfConversionIntegrationTests
{
    private static readonly byte[] Wide = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAECAIAAAA8r+mnAAAAPklEQVR4nAXBgQAAMADDsGIc4TjDKc5wjjOEJ4AXg2Lx4fAgxBsTjY0vLp4I9dZUa+urq6fCvDPT2fnm5pkfIQ0rgV8PHqsAAAAASUVORK5CYII=");

    private string _dir = "";

    [TestInitialize]
    public void Setup() { _dir = Path.Combine(Path.GetTempPath(), "won-pdfi-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }

    [TestCleanup]
    public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

    private static async Task Encode(string path, Guid encoderId)
    {
        using var src = new InMemoryRandomAccessStream();
        await src.WriteAsync(Wide.AsBuffer());
        src.Seek(0);
        var dec = await BitmapDecoder.CreateAsync(src);
        var px = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        using var dst = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(encoderId, dst);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, dec.PixelWidth, dec.PixelHeight, 96, 96, px.DetachPixelData());
        await enc.FlushAsync();
        var bytes = new byte[dst.Size];
        dst.Seek(0);
        await dst.ReadAsync(bytes.AsBuffer(), (uint)dst.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }

    [TestMethod]
    public async Task Common_image_formats_all_convert_to_a_readable_pdf()
    {
        var files = new Dictionary<string, Guid>
        {
            ["a.png"] = BitmapEncoder.PngEncoderId,
            ["b.jpg"] = BitmapEncoder.JpegEncoderId,
            ["c.bmp"] = BitmapEncoder.BmpEncoderId,
            ["d.gif"] = BitmapEncoder.GifEncoderId,
            ["e.tif"] = BitmapEncoder.TiffEncoderId,
        };
        var images = new List<byte[]>();
        foreach (var (name, id) in files)
        {
            var path = Path.Combine(_dir, name);
            await Encode(path, id);
            images.Add(await PdfImageLoader.LoadAsync(path, CancellationToken.None));
        }

        var pdf = Path.Combine(_dir, "all.pdf");
        ImagePdfWriter.Write(images, pdf);
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        Assert.AreEqual(files.Count, doc.PageCount);
    }

    [TestMethod]
    public async Task Service_converts_real_image_files_end_to_end()
    {
        var png = Path.Combine(_dir, "scan.png");
        await File.WriteAllBytesAsync(png, Wide);
        var jpg = Path.Combine(_dir, "photo.jpg");
        await Encode(jpg, BitmapEncoder.JpegEncoderId);

        var svc = new PdfConversionService(Array.Empty<IPdfEngine>(), PdfImageLoader.LoadAsync, Path.Combine(_dir, "tmp"));
        var result = await svc.RunAsync(new[] { png, jpg }, merge: false, progress: null, CancellationToken.None);

        Assert.AreEqual(2, result.Succeeded, string.Join("; ", result.Items.Select(i => i.Error)));
        Assert.IsTrue(File.Exists(Path.Combine(_dir, "scan.pdf")));
        Assert.IsTrue(File.Exists(Path.Combine(_dir, "photo.pdf")));
    }

    /// <summary>
    /// 설치된 Office 로 실제 변환. 사용자 PC 의 Office 를 실행하므로 WON_RUN_OFFICE_TESTS=1 일 때만 돈다.
    /// WON_OFFICE_SAMPLE_DIR 에 sample.docx / sample.xlsx / sample.pptx 가 있어야 한다.
    /// </summary>
    [TestMethod]
    public async Task Installed_office_exports_pdf_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("WON_RUN_OFFICE_TESTS") != "1") Assert.Inconclusive("set WON_RUN_OFFICE_TESTS=1 to run");
        var dir = Environment.GetEnvironmentVariable("WON_OFFICE_SAMPLE_DIR")!;
        var engine = new MsOfficeEngine();
        foreach (var name in new[] { "sample.docx", "sample.xlsx", "sample.pptx" })
        {
            var src = Path.Combine(dir, name);
            var kind = PdfSourceClassifier.Classify(src);
            if (!File.Exists(src) || !engine.IsAvailable(kind)) continue;
            var pdf = Path.Combine(_dir, name + ".pdf");
            await engine.ConvertAsync(src, pdf, CancellationToken.None);
            using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
            Assert.IsTrue(doc.PageCount >= 1, name);
        }

        var hwp = Path.Combine(dir, "sample.hwp");
        var hancom = new HancomEngine();
        if (File.Exists(hwp) && hancom.IsAvailable(PdfSourceKind.Hwp))
        {
            var pdf = Path.Combine(_dir, "sample.hwp.pdf");
            await hancom.ConvertAsync(hwp, pdf, CancellationToken.None);
            using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
            Assert.IsTrue(doc.PageCount >= 1, "sample.hwp");
        }
    }
}
