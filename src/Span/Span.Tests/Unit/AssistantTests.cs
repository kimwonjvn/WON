using System.Net;
using System.Text;
using System.Text.Json;
using Span.Services.Assistant;

namespace Span.Tests.Unit;

[TestClass]
public class AssistantPolicyTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("relative\\path")]
    [DataRow("ftp://host/x")]
    [DataRow("archive://C:\\a.zip/x")]
    [DataRow(@"\\?\C:\x")]
    [DataRow(@"\\.\PhysicalDrive0")]
    public void Normalize_rejects_unsafe_paths(string path)
        => Assert.IsFalse(AssistantPathPolicy.TryNormalize(path, out _, out _));

    [TestMethod]
    public void Normalize_collapses_dotdot()
    {
        Assert.IsTrue(AssistantPathPolicy.TryNormalize(@"C:\Temp\a\..\b", out var full, out _));
        Assert.AreEqual(@"C:\Temp\b", full);
    }

    [TestMethod]
    public void Protected_locations_cannot_be_mutated()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.IsFalse(AssistantPathPolicy.TryValidateMutationPath(win, out _, out _));
        Assert.IsFalse(AssistantPathPolicy.TryValidateMutationPath(Path.Combine(win, "System32", "x.dll"), out _, out _));
        Assert.IsFalse(AssistantPathPolicy.TryValidateMutationPath(@"C:\", out _, out _));
        Assert.IsFalse(AssistantPathPolicy.TryValidateMutationPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _, out _));
        Assert.IsFalse(AssistantPathPolicy.TryValidateMutationPath(@"\\server", out _, out _));
        Assert.IsFalse(AssistantPathPolicy.TryValidateMutationPath(@"\\server\share", out _, out _));
    }

    [TestMethod]
    public void Ordinary_subfolders_are_allowed()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.IsTrue(AssistantPathPolicy.TryValidateMutationPath(Path.Combine(docs, "x", "y.txt"), out _, out _));
    }

    [TestMethod]
    [DataRow("a.txt", true)]
    [DataRow("한글 이름.txt", true)]
    [DataRow("..", false)]
    [DataRow("a\\b", false)]
    [DataRow("a/b", false)]
    [DataRow("trailing.", false)]
    [DataRow(" lead", false)]
    [DataRow("", false)]
    public void Name_validation(string name, bool ok)
        => Assert.AreEqual(ok, AssistantPathPolicy.IsValidName(name, out _));

    [TestMethod]
    [DataRow("https://api.anthropic.com", true)]
    [DataRow("http://localhost:11434", true)]
    [DataRow("http://example.com", false)]
    [DataRow("https://user:pw@example.com", false)]
    [DataRow("not a url", false)]
    public void Base_url_validation(string url, bool ok)
        => Assert.AreEqual(ok, LlmEndpoint.TryResolveBase(url, "https://api.anthropic.com", out _, out _));

    [TestMethod]
    public void Combine_does_not_duplicate_v1()
    {
        Assert.AreEqual("https://x.test/v1/messages", LlmEndpoint.Combine(new Uri("https://x.test"), "messages").ToString());
        Assert.AreEqual("https://x.test/v1/messages", LlmEndpoint.Combine(new Uri("https://x.test/v1"), "messages").ToString());
    }
}

[TestClass]
public class AssistantAgentTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "won-asst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.log"), "x");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "a.log"), DateTime.UtcNow.AddDays(-200));
    }

    [TestCleanup]
    public void Cleanup() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class ScriptedClient : ILlmClient
    {
        private readonly Queue<LlmReply> _replies;
        public List<IReadOnlyList<LlmMessage>> Seen { get; } = new();
        public ScriptedClient(params LlmReply[] replies) => _replies = new Queue<LlmReply>(replies);
        public Task<LlmReply> SendAsync(string system, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDef> tools, CancellationToken ct)
        {
            Seen.Add(messages.ToList());
            return Task.FromResult(_replies.Dequeue());
        }
    }

    private static LlmReply Call(string name, object args, string id = "c1")
        => new("", new[] { new LlmToolCall(id, name, JsonSerializer.Serialize(args)) });

    private static LlmReply Done(string text = "ok") => new(text, Array.Empty<LlmToolCall>());

    [TestMethod]
    public async Task Read_tools_run_and_mutations_are_only_queued()
    {
        var client = new ScriptedClient(
            Call("search_files", new { root = _root, pattern = "*.log", older_than_days = 90 }),
            Call("propose_create_folder", new { path = Path.Combine(_root, "old") }, "c2"),
            Call("propose_move", new { source = Path.Combine(_root, "a.log"), destination_directory = Path.Combine(_root, "old") }, "c3"),
            Done("done"));

        var result = await new AssistantAgent(client, _root).RunAsync("move old logs", CancellationToken.None);

        Assert.AreEqual("done", result.Text);
        Assert.AreEqual(2, result.Plan.Count);
        Assert.AreEqual(PlannedOperationKind.CreateFolder, result.Plan[0].Kind);
        Assert.AreEqual(PlannedOperationKind.Move, result.Plan[1].Kind);

        // nothing was changed on disk
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "old")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "a.log")));

        // the search result (first tool result) contains a.log but not b.txt
        var toolResult = client.Seen[1].Last(m => m.Role == LlmRole.Tool).Text;
        StringAssert.Contains(toolResult, "a.log");
        Assert.IsFalse(toolResult.Contains("b.txt"));
    }

    [TestMethod]
    public void Older_than_filter_excludes_recent_files()
    {
        var json = new AssistantReadTools().SearchFiles(_root, "*", 90, null);
        StringAssert.Contains(json, "a.log");
        Assert.IsFalse(json.Contains("b.txt"));
    }

    [TestMethod]
    public async Task Protected_or_invalid_proposals_are_rejected_with_error_to_model()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var client = new ScriptedClient(
            Call("propose_delete", new { path = win }),
            Call("propose_move", new { source = Path.Combine(_root, "missing.txt"), destination_directory = _root }, "c2"),
            Call("propose_rename", new { path = Path.Combine(_root, "b.txt"), new_name = "..\\evil" }, "c3"),
            Done());

        var result = await new AssistantAgent(client, _root).RunAsync("x", CancellationToken.None);

        Assert.AreEqual(0, result.Plan.Count);
        foreach (var seen in client.Seen.Skip(1))
            Assert.IsTrue(seen.Last(m => m.Role == LlmRole.Tool).IsError);
    }

    [TestMethod]
    public async Task Cannot_overwrite_existing_target()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dst"));
        File.WriteAllText(Path.Combine(_root, "dst", "b.txt"), "other");
        var client = new ScriptedClient(
            Call("propose_move", new { source = Path.Combine(_root, "b.txt"), destination_directory = Path.Combine(_root, "dst") }),
            Done());

        var result = await new AssistantAgent(client, _root).RunAsync("x", CancellationToken.None);
        Assert.AreEqual(0, result.Plan.Count);
    }

    [TestMethod]
    public async Task Turn_limit_stops_runaway_loops()
    {
        var replies = Enumerable.Range(0, 20).Select(i => Call("list_directory", new { path = _root }, "c" + i)).ToArray();
        var result = await new AssistantAgent(new ScriptedClient(replies), _root, maxTurns: 3).RunAsync("x", CancellationToken.None);
        Assert.IsTrue(result.HitTurnLimit);
    }

    [TestMethod]
    public void List_directory_reports_entries_and_rejects_remote()
    {
        var tools = new AssistantReadTools();
        StringAssert.Contains(tools.ListDirectory(_root), "a.log");
        StringAssert.Contains(tools.ListDirectory("ftp://host/x"), "error");
        StringAssert.Contains(tools.ListDirectory(Path.Combine(_root, "nope")), "error");
    }
}

[TestClass]
public class LlmClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        private readonly string _response; private readonly HttpStatusCode _status;
        public FakeHandler(string response, HttpStatusCode status = HttpStatusCode.OK) { _response = response; _status = status; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_response, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly IReadOnlyList<LlmMessage> Conversation = new List<LlmMessage>
    {
        new() { Role = LlmRole.User, Text = "hi" },
        new() { Role = LlmRole.Assistant, Text = "", ToolCalls = new[] { new LlmToolCall("t1", "list_directory", "{\"path\":\"C:\\\\x\"}") } },
        new() { Role = LlmRole.Tool, ToolCallId = "t1", Text = "{\"entries\":[]}" },
    };

    [TestMethod]
    public async Task Claude_request_shape_and_tool_use_parsing()
    {
        var handler = new FakeHandler("""{"content":[{"type":"text","text":"hello"},{"type":"tool_use","id":"u1","name":"list_directory","input":{"path":"C:\\x"}}]}""");
        var client = new ClaudeClient(new HttpClient(handler), "KEY123", "claude-test", new Uri("https://api.anthropic.com"));

        var reply = await client.SendAsync("sys", Conversation, AssistantToolCatalog.All, CancellationToken.None);

        Assert.AreEqual("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.AreEqual("KEY123", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.AreEqual("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("claude-test", body.RootElement.GetProperty("model").GetString());
        Assert.AreEqual("sys", body.RootElement.GetProperty("system").GetString());
        var msgs = body.RootElement.GetProperty("messages");
        Assert.AreEqual("tool_use", msgs[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.AreEqual("tool_result", msgs[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.IsFalse(handler.Body!.Contains("KEY123"));

        Assert.AreEqual("hello", reply.Text);
        Assert.AreEqual(1, reply.ToolCalls.Count);
        Assert.AreEqual("u1", reply.ToolCalls[0].Id);
        Assert.AreEqual("list_directory", reply.ToolCalls[0].Name);
    }

    [TestMethod]
    public async Task OpenAi_request_shape_and_tool_call_parsing()
    {
        var handler = new FakeHandler("""{"choices":[{"message":{"content":null,"tool_calls":[{"id":"k1","type":"function","function":{"name":"get_file_info","arguments":"{\"path\":\"C:\\\\a\"}"}}]}}]}""");
        var client = new OpenAiClient(new HttpClient(handler), "KEY123", "gpt-test", new Uri("https://api.openai.com"));

        var reply = await client.SendAsync("sys", Conversation, AssistantToolCatalog.All, CancellationToken.None);

        Assert.AreEqual("https://api.openai.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.AreEqual("Bearer", handler.Request.Headers.Authorization!.Scheme);
        using var body = JsonDocument.Parse(handler.Body!);
        var msgs = body.RootElement.GetProperty("messages");
        Assert.AreEqual("system", msgs[0].GetProperty("role").GetString());
        Assert.AreEqual("tool", msgs[3].GetProperty("role").GetString());
        Assert.IsFalse(handler.Body!.Contains("KEY123"));

        Assert.AreEqual(1, reply.ToolCalls.Count);
        Assert.AreEqual("get_file_info", reply.ToolCalls[0].Name);
        StringAssert.Contains(reply.ToolCalls[0].ArgumentsJson, "C:");
    }

    [TestMethod]
    public async Task Http_errors_surface_provider_message_without_leaking_key()
    {
        var handler = new FakeHandler("""{"error":{"message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized);
        var client = new ClaudeClient(new HttpClient(handler), "SECRETKEY", "m", new Uri("https://api.anthropic.com"));

        var ex = await Assert.ThrowsExceptionAsync<LlmException>(() => client.SendAsync("s", Conversation, AssistantToolCatalog.All, CancellationToken.None));
        StringAssert.Contains(ex.Message, "401");
        Assert.IsFalse(ex.Message.Contains("SECRETKEY"));
    }
}
