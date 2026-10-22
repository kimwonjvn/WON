using Span.Services;

namespace Span.Tests.Unit;

[TestClass]
public class RecentDocumentsServiceTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 10, 0, 0);

    [TestMethod]
    public void Classify_groups_office_and_document_extensions()
    {
        Assert.AreEqual(RecentDocKind.Excel, RecentDocumentsService.Classify("a.XLSX"));
        Assert.AreEqual(RecentDocKind.Excel, RecentDocumentsService.Classify("a.csv"));
        Assert.AreEqual(RecentDocKind.PowerPoint, RecentDocumentsService.Classify("deck.pptx"));
        Assert.AreEqual(RecentDocKind.Document, RecentDocumentsService.Classify("a.docx"));
        Assert.AreEqual(RecentDocKind.Document, RecentDocumentsService.Classify("a.hwp"));
        Assert.AreEqual(RecentDocKind.Document, RecentDocumentsService.Classify("a.pdf"));
        Assert.IsNull(RecentDocumentsService.Classify("a.exe"));
        Assert.IsNull(RecentDocumentsService.Classify("noext"));
        Assert.IsNull(RecentDocumentsService.Classify(null));
    }

    private static List<(string, DateTime)> Links(params string[] names) =>
        names.Select((n, i) => ($@"C:\Recent\{n}.lnk", T0.AddMinutes(-i))).ToList();

    [TestMethod]
    public void Collect_filters_by_kind_and_keeps_recency_order()
    {
        var links = Links("one.xlsx", "talk.pptx", "two.xlsx", "memo.docx", "app.exe", "dir");
        var result = RecentDocumentsService.Collect(links,
            lnk => $@"D:\work\{Path.GetFileNameWithoutExtension(lnk)}", _ => true);

        CollectionAssert.AreEqual(new[] { "one.xlsx", "two.xlsx" }, result[RecentDocKind.Excel].Select(d => d.Name).ToArray());
        Assert.AreEqual("talk.pptx", result[RecentDocKind.PowerPoint].Single().Name);
        Assert.AreEqual("memo.docx", result[RecentDocKind.Document].Single().Name);
    }

    [TestMethod]
    public void Collect_skips_missing_lock_files_duplicates_and_unresolved()
    {
        var links = Links("a.xlsx", "~$a.xlsx", "a.xlsx", "gone.xlsx", "bad.xlsx");
        var result = RecentDocumentsService.Collect(links,
            lnk =>
            {
                var n = Path.GetFileNameWithoutExtension(lnk);
                return n == "bad.xlsx" ? null : $@"D:\work\{n}";
            },
            p => !p.EndsWith("gone.xlsx"));

        Assert.AreEqual(1, result[RecentDocKind.Excel].Count);
        Assert.AreEqual(@"D:\work\a.xlsx", result[RecentDocKind.Excel][0].Path);
    }

    [TestMethod]
    public void Collect_respects_per_kind_limit_and_survives_resolver_errors()
    {
        var links = Links(Enumerable.Range(0, 30).Select(i => $"f{i}.xlsx").Append("boom.pptx").ToArray());
        var result = RecentDocumentsService.Collect(links,
            lnk => Path.GetFileNameWithoutExtension(lnk) == "boom.pptx"
                ? throw new IOException("unreadable")
                : $@"D:\w\{Path.GetFileNameWithoutExtension(lnk)}",
            _ => true, perKind: 5);

        Assert.AreEqual(5, result[RecentDocKind.Excel].Count);
        Assert.AreEqual(0, result[RecentDocKind.PowerPoint].Count);
    }

    [TestMethod]
    public void EnumerateRecentLinks_returns_empty_for_missing_folder()
    {
        Assert.AreEqual(0, RecentDocumentsService.EnumerateRecentLinks(@"Z:\definitely\not\here").Count);
    }
}
