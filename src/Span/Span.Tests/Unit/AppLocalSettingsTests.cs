using Span.Helpers;

namespace Span.Tests.Unit;

/// <summary>패키지 없이 설치한 WON 의 설정 저장소(JSON 파일)가 형식과 값을 그대로 돌려주는지 확인한다.</summary>
[TestClass]
public class AppLocalSettingsTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init() => _dir = Path.Combine(Path.GetTempPath(), "won-settings-test-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private string FilePath => Path.Combine(_dir, "localsettings.json");

    [TestMethod]
    public void Values_keep_their_types_after_a_restart()
    {
        var a = new FileSettingsValues(FilePath);
        a["Theme"] = "dark";
        a["IsSplit"] = true;
        a["ViewMode"] = 3;
        a["Big"] = 5_000_000_000L;
        a["SidebarWidth"] = 231.5;

        var b = new FileSettingsValues(FilePath); // 새 프로세스처럼 파일에서 다시 읽는다
        Assert.AreEqual("dark", b["Theme"]);
        Assert.AreEqual(true, b["IsSplit"]);
        Assert.AreEqual(3, b["ViewMode"]);
        Assert.AreEqual(5_000_000_000L, b["Big"]);
        Assert.AreEqual(231.5, b["SidebarWidth"]);
        Assert.AreEqual(5, b.Count);
    }

    [TestMethod]
    public void Missing_key_reads_as_null_and_TryGetValue_is_false()
    {
        var s = new FileSettingsValues(FilePath);
        Assert.IsNull(s["Nope"]);
        Assert.IsFalse(s.TryGetValue("Nope", out var v));
        Assert.IsNull(v);
        Assert.IsFalse(s.ContainsKey("Nope"));
    }

    [TestMethod]
    public void Composite_values_round_trip_with_their_inner_types()
    {
        var a = new FileSettingsValues(FilePath);
        a["WindowPlacement"] = new SettingsComposite { ["X"] = 474, ["Y"] = 144, ["Width"] = 1440, ["Height"] = 753 };
        a["DetailsViewSort"] = new SettingsComposite { ["SortColumn"] = "Name", ["SortAscending"] = true };

        var b = new FileSettingsValues(FilePath);
        var win = b["WindowPlacement"] as SettingsComposite;
        Assert.IsNotNull(win);
        Assert.IsTrue(win.TryGetValue("X", out var x) && x is int xi && xi == 474);
        Assert.IsTrue(win.TryGetValue("Width", out var w) && w is int wi && wi == 1440);
        var sort = b["DetailsViewSort"] as SettingsComposite;
        Assert.IsNotNull(sort);
        Assert.AreEqual("Name", sort["SortColumn"]);
        Assert.AreEqual(true, sort["SortAscending"]);
    }

    [TestMethod]
    public void Remove_and_Clear_are_saved()
    {
        var a = new FileSettingsValues(FilePath);
        a["A"] = 1; a["B"] = 2;
        Assert.IsTrue(a.Remove("A"));
        Assert.IsFalse(a.Remove("A"));

        var b = new FileSettingsValues(FilePath);
        Assert.IsFalse(b.ContainsKey("A"));
        Assert.IsTrue(b.ContainsKey("B"));

        b.Clear();
        Assert.AreEqual(0, new FileSettingsValues(FilePath).Count);
    }

    [TestMethod]
    public void Setting_null_removes_the_value()
    {
        var a = new FileSettingsValues(FilePath);
        a["A"] = "x";
        a["A"] = null;
        Assert.IsFalse(new FileSettingsValues(FilePath).ContainsKey("A"));
    }

    [TestMethod]
    public void A_damaged_file_is_set_aside_and_the_app_starts_with_empty_settings()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");

        var s = new FileSettingsValues(FilePath);
        Assert.AreEqual(0, s.Count);
        Assert.IsTrue(File.Exists(FilePath + ".bad"), "the damaged file is kept for inspection");

        s["Theme"] = "light"; // and it can be written again
        Assert.AreEqual("light", new FileSettingsValues(FilePath)["Theme"]);
    }

    [TestMethod]
    public void Unknown_entries_are_ignored_instead_of_failing_the_whole_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ \"Good\": {\"t\":\"i\",\"v\":7}, \"Odd\": {\"t\":\"zzz\",\"v\":1}, \"Plain\": 5 }");
        var s = new FileSettingsValues(FilePath);
        Assert.AreEqual(7, s["Good"]);
        Assert.IsFalse(s.ContainsKey("Odd"));
        Assert.IsFalse(s.ContainsKey("Plain"));
    }

    [TestMethod]
    public void A_file_saved_with_a_utf8_BOM_is_still_read()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ \"OnboardingCompleted\": {\"t\":\"b\",\"v\":true} }", new System.Text.UTF8Encoding(true));
        var s = new FileSettingsValues(FilePath);
        Assert.AreEqual(true, s["OnboardingCompleted"]);
        Assert.IsFalse(File.Exists(FilePath + ".bad"));
    }

    [TestMethod]
    public void ReplaceAll_overwrites_everything()
    {
        var a = new FileSettingsValues(FilePath);
        a["Old"] = 1;
        a.ReplaceAll(new Dictionary<string, object> { ["New"] = "v" });
        var b = new FileSettingsValues(FilePath);
        Assert.IsFalse(b.ContainsKey("Old"));
        Assert.AreEqual("v", b["New"]);
    }

    [TestMethod]
    public void Parallel_writes_do_not_corrupt_the_file()
    {
        var s = new FileSettingsValues(FilePath);
        Parallel.For(0, 40, i => s["Key" + i] = i);
        var reread = new FileSettingsValues(FilePath);
        Assert.AreEqual(40, reread.Count);
        for (int i = 0; i < 40; i++) Assert.AreEqual(i, reread["Key" + i]);
    }
}
