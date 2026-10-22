using System.Net;
using System.Text;
using Span.Services.Assistant;

namespace Span.Tests.Unit;

[TestClass]
public class LocalAiHelperTests
{
    private sealed class FakeOllama : HttpMessageHandler
    {
        public string TagsJson { get; set; } = """{"models":[{"name":"qwen3.5:9b"},{"name":"llama3.2:latest"}]}""";
        public string ShowJson { get; set; } = """{"capabilities":["completion","tools","thinking"]}""";
        public bool Down { get; set; }
        public string PullBody { get; set; } = "";
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (Down) throw new HttpRequestException("No connection could be made because the target machine actively refused it");
            var path = request.RequestUri.AbsolutePath;
            string body = path switch { "/api/tags" => TagsJson, "/api/show" => ShowJson, "/api/pull" => PullBody, _ => "{}" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static readonly Uri Base = new("http://localhost:11434");

    [TestMethod]
    public void Root_strips_the_openai_v1_suffix()
    {
        Assert.AreEqual("http://localhost:11434/", LocalAiHelper.Root(new Uri("http://localhost:11434/v1")).AbsoluteUri);
        Assert.AreEqual("http://localhost:11434/", LocalAiHelper.Root(new Uri("http://localhost:11434")).AbsoluteUri);
    }

    [DataTestMethod]
    [DataRow("qwen3.5:9b", "qwen3.5:9b", true)]
    [DataRow("Qwen3.5:9B", "qwen3.5:9b", true)]
    [DataRow("llama3.2:latest", "llama3.2", true)]
    [DataRow("qwen3.5:9b", "qwen3.5:4b", false)]
    [DataRow("qwen3.5:9b", "qwen3.5", false)]
    public void Model_names_match_like_ollama_does(string installed, string wanted, bool expected)
        => Assert.AreEqual(expected, LocalAiHelper.ModelMatches(installed, wanted));

    [TestMethod]
    public async Task Ready_when_running_installed_and_tool_capable()
    {
        var s = await LocalAiHelper.CheckAsync(new HttpClient(new FakeOllama()), Base, "qwen3.5:9b", CancellationToken.None);
        Assert.AreEqual(LocalAiState.Ready, s.State);
        CollectionAssert.Contains(s.InstalledModels.ToList(), "llama3.2:latest");
    }

    [TestMethod]
    public async Task Not_reachable_when_ollama_is_not_running()
    {
        var s = await LocalAiHelper.CheckAsync(new HttpClient(new FakeOllama { Down = true }), Base, "qwen3.5:9b", CancellationToken.None);
        Assert.AreEqual(LocalAiState.NotReachable, s.State);
    }

    [TestMethod]
    public async Task Model_missing_lists_what_is_installed()
    {
        var s = await LocalAiHelper.CheckAsync(new HttpClient(new FakeOllama()), Base, "gemma4:e4b", CancellationToken.None);
        Assert.AreEqual(LocalAiState.ModelMissing, s.State);
        Assert.AreEqual(2, s.InstalledModels.Count);
    }

    [TestMethod]
    public async Task Model_without_tool_capability_is_flagged()
    {
        var fake = new FakeOllama { ShowJson = """{"capabilities":["completion"]}""" };
        var s = await LocalAiHelper.CheckAsync(new HttpClient(fake), Base, "llama3.2", CancellationToken.None);
        Assert.AreEqual(LocalAiState.NoToolSupport, s.State);
    }

    [TestMethod]
    public async Task Older_ollama_without_capabilities_is_not_blocked()
    {
        var fake = new FakeOllama { ShowJson = """{"details":{}}""" };
        var s = await LocalAiHelper.CheckAsync(new HttpClient(fake), Base, "qwen3.5:9b", CancellationToken.None);
        Assert.AreEqual(LocalAiState.Ready, s.State);
    }

    [TestMethod]
    public async Task Garbage_response_is_reported_as_error_not_a_crash()
    {
        var s = await LocalAiHelper.CheckAsync(new HttpClient(new FakeOllama { TagsJson = "<html>nope</html>" }), Base, "qwen3.5:9b", CancellationToken.None);
        Assert.AreEqual(LocalAiState.Error, s.State);
    }

    [TestMethod]
    public async Task Pull_reports_progress_and_surfaces_errors()
    {
        var ok = new FakeOllama
        {
            PullBody = "{\"status\":\"pulling manifest\"}\n{\"status\":\"pulling abc\",\"total\":100,\"completed\":25}\n{\"status\":\"pulling abc\",\"total\":100,\"completed\":100}\n{\"status\":\"success\"}\n"
        };
        var seen = new List<PullProgress>();
        await LocalAiHelper.PullAsync(new HttpClient(ok), Base, "qwen3.5:9b", new Progress<PullProgress>(seen.Add), CancellationToken.None);
        await Task.Delay(50); // Progress<T> posts asynchronously
        Assert.IsTrue(seen.Any(p => p.Fraction == 0.25));
        Assert.IsTrue(seen.Any(p => p.Status == "success"));

        var bad = new FakeOllama { PullBody = "{\"error\":\"pull model manifest: file does not exist\"}\n" };
        var ex = await Assert.ThrowsExceptionAsync<LlmException>(() =>
            LocalAiHelper.PullAsync(new HttpClient(bad), Base, "nope:1b", null, CancellationToken.None));
        StringAssert.Contains(ex.Message, "does not exist");
    }

    [TestMethod]
    public void Beginner_messages_explain_what_to_do_in_both_languages()
    {
        var down = new LocalAiStatus(LocalAiState.NotReachable, "x", Array.Empty<string>());
        StringAssert.Contains(LocalAiHelper.Describe(down, "qwen3.5:9b", korean: true), "Ollama");
        StringAssert.Contains(LocalAiHelper.Describe(down, "qwen3.5:9b", korean: false), "Download Ollama");
        StringAssert.Contains(LocalAiHelper.Describe(new LocalAiStatus(LocalAiState.ModelMissing, "m", Array.Empty<string>()), "m", true), "모델 받기");

        StringAssert.Contains(LocalAiHelper.ExplainError("network error: connection refused", "m", true)!, "Ollama");
        StringAssert.Contains(LocalAiHelper.ExplainError("HTTP 404: model 'm' not found, try pulling it first", "m", false)!, "ollama pull m");
        StringAssert.Contains(LocalAiHelper.ExplainError("HTTP 400: m does not support tools", "m", true)!, "도구 호출");
        Assert.IsNull(LocalAiHelper.ExplainError("something unrelated", "m", true));
    }
}
