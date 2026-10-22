using Span.Services.Assistant;

namespace Span.Tests.Integration;

/// <summary>
/// 실제 Ollama 서버로 확인(선택): WON_RUN_OLLAMA_TESTS=1 일 때만 돈다. 작은 모델을 내려받았다가 지운다.
/// WON_OLLAMA_SMALL_MODEL (기본 smollm2:135m) 은 도구 호출을 지원하지 않는 작은 모델이어야 한다.
/// </summary>
[TestClass]
public class LocalAiLiveTests
{
    [TestMethod]
    public async Task Pull_a_small_model_then_see_it_flagged_as_not_tool_capable()
    {
        if (Environment.GetEnvironmentVariable("WON_RUN_OLLAMA_TESTS") != "1") Assert.Inconclusive("set WON_RUN_OLLAMA_TESTS=1 to run");
        var model = Environment.GetEnvironmentVariable("WON_OLLAMA_SMALL_MODEL") ?? "smollm2:135m";
        var baseUri = new Uri("http://localhost:11434");
        using var quick = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var slow = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var before = await LocalAiHelper.CheckAsync(quick, baseUri, model, CancellationToken.None);
        Assert.AreEqual(LocalAiState.ModelMissing, before.State, "start from a clean state");

        var reports = new List<PullProgress>();
        await LocalAiHelper.PullAsync(slow, baseUri, model, new Progress<PullProgress>(p => { lock (reports) reports.Add(p); }), CancellationToken.None);
        await Task.Delay(200);
        lock (reports)
        {
            Assert.IsTrue(reports.Any(p => p.Fraction is > 0 and < 1) || reports.Any(p => p.Fraction == 1), "progress with a fraction was reported");
            Assert.IsTrue(reports.Any(p => p.Status == "success"));
        }

        var after = await LocalAiHelper.CheckAsync(quick, baseUri, model, CancellationToken.None);
        Assert.AreEqual(LocalAiState.NoToolSupport, after.State, after.Detail);

        var missing = await LocalAiHelper.CheckAsync(quick, baseUri, "definitely-not-a-model:1b", CancellationToken.None);
        Assert.AreEqual(LocalAiState.ModelMissing, missing.State);
    }
}
