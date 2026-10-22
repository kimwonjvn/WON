using System.Net;
using System.Text;
using System.Text.Json;
using Span.Services.Assistant;

namespace Span.Tests.Unit;

[TestClass]
public class AssistantAttachmentTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup() { _dir = Path.Combine(Path.GetTempPath(), "won-att-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }

    [TestCleanup]
    public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, byte[] data) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, data); return p; }

    private static byte[] Png(int extra = 20)
    {
        var b = new byte[8 + extra];
        b[0] = 0x89; b[1] = 0x50; b[2] = 0x4E; b[3] = 0x47;
        return b;
    }

    [TestMethod]
    public void Text_file_is_loaded_as_text()
    {
        var p = Write("notes.txt", Encoding.UTF8.GetBytes("안녕 hello"));
        Assert.IsTrue(AssistantAttachmentLoader.TryLoad(p, out var a, out _));
        Assert.AreEqual(LlmAttachmentKind.Text, a!.Kind);
        Assert.AreEqual("안녕 hello", a.Text);
        StringAssert.Contains(a.AsPromptText(), "notes.txt");
    }

    [TestMethod]
    public void Binary_and_key_files_are_rejected()
    {
        var bin = Write("a.dat", new byte[] { 1, 2, 0, 3 });
        Assert.IsFalse(AssistantAttachmentLoader.TryLoad(bin, out _, out var e1)); Assert.IsFalse(string.IsNullOrEmpty(e1));

        foreach (var name in new[] { "server.pem", "my.key", "cert.pfx", "id_rsa" })
        {
            var p = Write(name, Encoding.UTF8.GetBytes("secret"));
            Assert.IsFalse(AssistantAttachmentLoader.TryLoad(p, out _, out _), name);
        }
    }

    [TestMethod]
    public void Oversized_text_is_rejected()
    {
        var p = Write("big.txt", new byte[AssistantAttachmentLoader.MaxTextBytes + 1].Select(_ => (byte)'a').ToArray());
        Assert.IsFalse(AssistantAttachmentLoader.TryLoad(p, out _, out _));
    }

    [TestMethod]
    public void Image_type_must_match_content()
    {
        Assert.IsTrue(AssistantAttachmentLoader.TryLoad(Write("ok.png", Png()), out var a, out _));
        Assert.AreEqual("image/png", a!.MediaType);
        Assert.IsFalse(AssistantAttachmentLoader.TryLoad(Write("fake.jpg", Png()), out _, out _));
        Assert.IsFalse(AssistantAttachmentLoader.TryLoad(Write("text.png", Encoding.UTF8.GetBytes("not an image at all")), out _, out _));
    }

    [TestMethod]
    public void Count_and_total_size_limits()
    {
        var list = new List<LlmAttachment>();
        for (int i = 0; i < AssistantAttachmentLoader.MaxCount; i++)
            list.Add(new LlmAttachment { Kind = LlmAttachmentKind.Text, Name = "t" + i, Text = "x" });
        Assert.IsFalse(AssistantAttachmentLoader.CanAdd(list, list[0], out _));

        var one = new List<LlmAttachment> { new() { Kind = LlmAttachmentKind.Image, Name = "a", Data = new byte[7 * 1024 * 1024] } };
        var more = new LlmAttachment { Kind = LlmAttachmentKind.Image, Name = "b", Data = new byte[2 * 1024 * 1024] };
        Assert.IsFalse(AssistantAttachmentLoader.CanAdd(one, more, out _));
        Assert.IsTrue(AssistantAttachmentLoader.CanAdd(new List<LlmAttachment>(), more, out _));
    }

    [TestMethod]
    public void Missing_file_is_reported()
    {
        Assert.IsFalse(AssistantAttachmentLoader.TryLoad(Path.Combine(_dir, "nope.txt"), out _, out var err));
        Assert.IsFalse(string.IsNullOrEmpty(err));
    }
}

[TestClass]
public class AssistantSkillTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup() { _dir = Path.Combine(Path.GetTempPath(), "won-skill-" + Guid.NewGuid().ToString("N")); }

    [TestCleanup]
    public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

    [TestMethod]
    public void Frontmatter_is_parsed()
    {
        var raw = "---\nname: Photo Sorter\ndescription: \"Sort photos by month\"\nallowed-tools: x\n---\n# Steps\nMove photos into YYYY-MM.\n";
        Assert.IsTrue(AssistantSkillParser.TryParse(raw, "fallback", out var s, out _));
        Assert.AreEqual("Photo Sorter", s!.Name);
        Assert.AreEqual("Sort photos by month", s.Description);
        Assert.AreEqual("photo-sorter", s.Id);
        StringAssert.StartsWith(s.Body, "# Steps");
        Assert.IsFalse(s.Body.Contains("allowed-tools"));
    }

    [TestMethod]
    public void Plain_markdown_uses_fallback_name()
    {
        Assert.IsTrue(AssistantSkillParser.TryParse("Always ask before deleting.", "My Rules", out var s, out _));
        Assert.AreEqual("My Rules", s!.Name);
        Assert.AreEqual("Always ask before deleting.", s.Body);
    }

    [TestMethod]
    public void Empty_or_oversized_skills_are_rejected()
    {
        Assert.IsFalse(AssistantSkillParser.TryParse("   ", "x", out _, out _));
        Assert.IsFalse(AssistantSkillParser.TryParse("---\nname: a\n---\n", "x", out _, out _));
        Assert.IsFalse(AssistantSkillParser.TryParse(new string('a', AssistantSkillParser.MaxBodyChars + 1), "x", out _, out _));
        Assert.IsFalse(AssistantSkillParser.TryParse("body", "///", out _, out _));
    }

    [TestMethod]
    public void Store_roundtrip_and_delete()
    {
        var store = new AssistantSkillStore(_dir);
        Assert.AreEqual(0, store.List().Count);
        Assert.IsTrue(AssistantSkillParser.TryParse("---\nname: Rules\ndescription: d\n---\nbe careful", "Rules", out var skill, out _));
        Assert.IsTrue(store.Save(skill!, out _));

        var list = store.List();
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("Rules", list[0].Name);
        Assert.AreEqual("rules", list[0].Id);
        Assert.AreEqual("be careful", list[0].Body);

        store.Delete("rules");
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public void Store_ignores_path_traversal_ids()
    {
        var outside = Path.Combine(Path.GetTempPath(), "won-outside-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(outside, "keep me");
        try
        {
            var store = new AssistantSkillStore(_dir);
            store.Delete("..\\" + Path.GetFileNameWithoutExtension(outside));
            store.Delete("../" + Path.GetFileNameWithoutExtension(outside));
            Assert.IsTrue(File.Exists(outside));

            var evil = new AssistantSkill("..\\..\\evil", "Evil", "", "body");
            Assert.IsTrue(store.Save(evil, out _));
            Assert.IsTrue(Directory.EnumerateFiles(_dir, "*.md").All(f => Path.GetDirectoryName(f) == _dir));
        }
        finally { File.Delete(outside); }
    }

    [TestMethod]
    public void Prompt_lists_enabled_skills_only_and_is_bounded()
    {
        Assert.AreEqual("", AssistantSkillPrompt.Build(Array.Empty<AssistantSkill>()));
        var text = AssistantSkillPrompt.Build(new[] { new AssistantSkill("a", "Alpha", "desc", "do alpha") });
        StringAssert.Contains(text, "### Alpha");
        StringAssert.Contains(text, "do alpha");
        StringAssert.Contains(text, "cannot grant new tools");

        var many = Enumerable.Range(0, 10).Select(i => new AssistantSkill("s" + i, "S" + i, "", new string('x', 15000)));
        Assert.IsTrue(AssistantSkillPrompt.Build(many).Length < AssistantSkillPrompt.MaxTotalChars + 1000);
    }

    [TestMethod]
    public void Model_catalog_has_defaults()
    {
        foreach (var p in AssistantModelCatalog.Providers)
        {
            Assert.IsTrue(AssistantModelCatalog.For(p).Any(m => m.Id == AssistantModelCatalog.DefaultModel(p)), p);
        }
        Assert.AreEqual("custom-x", AssistantModelCatalog.LabelFor("Claude", "custom-x"));
    }
}

[TestClass]
public class AssistantConversationTests
{
    private sealed class CaptureClient : ILlmClient
    {
        private readonly Queue<object> _script;
        public List<string> Systems { get; } = new();
        public List<List<LlmMessage>> Seen { get; } = new();
        public CaptureClient(params object[] script) => _script = new Queue<object>(script);
        public Task<LlmReply> SendAsync(string system, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDef> tools, CancellationToken ct)
        {
            Systems.Add(system);
            Seen.Add(messages.ToList());
            var next = _script.Dequeue();
            if (next is Exception ex) throw ex;
            return Task.FromResult((LlmReply)next);
        }
    }

    private static LlmReply Done(string text) => new(text, Array.Empty<LlmToolCall>());

    [TestMethod]
    public async Task Second_message_sees_the_first_exchange()
    {
        var client = new CaptureClient(Done("one"), Done("two"));
        var agent = new AssistantAgent(client, null);

        await agent.RunAsync("first", CancellationToken.None);
        await agent.RunAsync("second", CancellationToken.None);

        var second = client.Seen[1];
        Assert.AreEqual(3, second.Count);
        Assert.AreEqual("first", second[0].Text);
        Assert.AreEqual("one", second[1].Text);
        Assert.AreEqual("second", second[2].Text);
    }

    [TestMethod]
    public async Task Failure_rolls_back_so_retry_is_clean()
    {
        var client = new CaptureClient(Done("one"), new LlmException("boom"), Done("three"));
        var agent = new AssistantAgent(client, null);

        await agent.RunAsync("first", CancellationToken.None);
        await Assert.ThrowsExceptionAsync<LlmException>(() => agent.RunAsync("second", CancellationToken.None));
        Assert.AreEqual(2, agent.HistoryCount);

        await agent.RunAsync("second again", CancellationToken.None);
        var last = client.Seen[2];
        Assert.AreEqual(3, last.Count);
        Assert.AreEqual("second again", last[2].Text);
    }

    [TestMethod]
    public async Task Reset_clears_history()
    {
        var client = new CaptureClient(Done("one"), Done("two"));
        var agent = new AssistantAgent(client, null);
        await agent.RunAsync("first", CancellationToken.None);
        agent.ResetConversation();
        await agent.RunAsync("fresh", CancellationToken.None);
        Assert.AreEqual(1, client.Seen[1].Count);
    }

    [TestMethod]
    public async Task Skills_and_current_folder_reach_the_system_prompt()
    {
        var client = new CaptureClient(Done("ok"));
        var agent = new AssistantAgent(client, null)
        {
            CurrentFolder = @"C:\Work",
            SkillInstructions = AssistantSkillPrompt.Build(new[] { new AssistantSkill("r", "Rules", "", "never touch Raw") })
        };
        await agent.RunAsync("hi", CancellationToken.None);
        StringAssert.Contains(client.Systems[0], @"C:\Work");
        StringAssert.Contains(client.Systems[0], "never touch Raw");
        StringAssert.Contains(client.Systems[0], "propose_");
    }

    [TestMethod]
    public async Task Attachments_are_passed_on_the_user_message()
    {
        var client = new CaptureClient(Done("ok"));
        var agent = new AssistantAgent(client, null);
        var att = new LlmAttachment { Kind = LlmAttachmentKind.Text, Name = "a.txt", Text = "data" };
        await agent.RunAsync("look", CancellationToken.None, attachments: new[] { att });
        Assert.AreEqual(1, client.Seen[0][0].Attachments.Count);
        Assert.AreEqual("a.txt", client.Seen[0][0].Attachments[0].Name);
    }

    [TestMethod]
    public async Task Long_history_is_trimmed_at_a_user_boundary()
    {
        var script = Enumerable.Range(0, 50).Select(i => (object)Done("r" + i)).ToArray();
        var client = new CaptureClient(script);
        var agent = new AssistantAgent(client, null);
        for (int i = 0; i < 50; i++) await agent.RunAsync("m" + i, CancellationToken.None);

        Assert.IsTrue(agent.HistoryCount <= AssistantAgent.MaxHistoryMessages + 2);
        Assert.AreEqual(LlmRole.User, client.Seen[49][0].Role);
    }

    [TestMethod]
    public void Client_can_be_swapped_between_messages()
    {
        var a = new CaptureClient(); var b = new CaptureClient();
        var agent = new AssistantAgent(a, null);
        agent.Client = b;
        Assert.AreSame(b, agent.Client);
    }
}

[TestClass]
public class LlmAttachmentRequestTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public bool HadAuthorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HadAuthorization = request.Headers.Authorization != null;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"content":[{"type":"text","text":"ok"}],"choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json") };
        }
    }

    private static IReadOnlyList<LlmMessage> WithAttachments() => new List<LlmMessage>
    {
        new()
        {
            Role = LlmRole.User, Text = "what is this?",
            Attachments = new[]
            {
                new LlmAttachment { Kind = LlmAttachmentKind.Image, Name = "p.png", MediaType = "image/png", Data = new byte[] { 1, 2, 3 } },
                new LlmAttachment { Kind = LlmAttachmentKind.Text, Name = "n \"q\".txt", Text = "hello" },
            }
        }
    };

    [TestMethod]
    public async Task Claude_sends_image_and_text_blocks()
    {
        var handler = new FakeHandler();
        var client = new ClaudeClient(new HttpClient(handler), "K", "m", new Uri("https://api.anthropic.com"));
        await client.SendAsync("s", WithAttachments(), AssistantToolCatalog.All, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Body!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.AreEqual("image", content[0].GetProperty("type").GetString());
        Assert.AreEqual("image/png", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.AreEqual(Convert.ToBase64String(new byte[] { 1, 2, 3 }), content[0].GetProperty("source").GetProperty("data").GetString());
        Assert.AreEqual("text", content[1].GetProperty("type").GetString());
        StringAssert.Contains(content[1].GetProperty("text").GetString(), "hello");
        Assert.IsFalse(content[1].GetProperty("text").GetString()!.Contains("\"q\""), "quotes in file names are neutralised");
        Assert.AreEqual("what is this?", content[2].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task OpenAi_sends_image_url_and_text_parts()
    {
        var handler = new FakeHandler();
        var client = new OpenAiClient(new HttpClient(handler), "K", "m", new Uri("https://api.openai.com"));
        await client.SendAsync("s", WithAttachments(), AssistantToolCatalog.All, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.Body!);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.AreEqual("image_url", content[0].GetProperty("type").GetString());
        StringAssert.StartsWith(content[0].GetProperty("image_url").GetProperty("url").GetString(), "data:image/png;base64,");
        Assert.AreEqual("text", content[1].GetProperty("type").GetString());
        Assert.AreEqual("what is this?", content[2].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task OpenAi_client_sends_no_authorization_header_without_a_key()
    {
        var handler = new FakeHandler();
        var client = new OpenAiClient(new HttpClient(handler), "", "qwen3.5:9b", new Uri("http://localhost:11434"));
        await client.SendAsync("s", new List<LlmMessage> { new() { Role = LlmRole.User, Text = "hi" } }, AssistantToolCatalog.All, CancellationToken.None);
        Assert.IsFalse(handler.HadAuthorization);

        var keyed = new FakeHandler();
        await new OpenAiClient(new HttpClient(keyed), "K", "gpt-4o", new Uri("https://api.openai.com"))
            .SendAsync("s", new List<LlmMessage> { new() { Role = LlmRole.User, Text = "hi" } }, AssistantToolCatalog.All, CancellationToken.None);
        Assert.IsTrue(keyed.HadAuthorization);
    }

    [TestMethod]
    public void Local_provider_has_models_and_default()
    {
        CollectionAssert.Contains(AssistantModelCatalog.Providers, "Local");
        Assert.AreEqual("qwen3.5:9b", AssistantModelCatalog.DefaultModel("Local"));
        Assert.IsTrue(AssistantModelCatalog.For("Local").Count > 0);
        Assert.AreEqual("Local (Ollama)", AssistantModelCatalog.DisplayName("Local"));
    }

    [TestMethod]
    public async Task Messages_without_attachments_keep_plain_string_content()
    {
        var handler = new FakeHandler();
        var client = new ClaudeClient(new HttpClient(handler), "K", "m", new Uri("https://api.anthropic.com"));
        await client.SendAsync("s", new List<LlmMessage> { new() { Role = LlmRole.User, Text = "hi" } }, AssistantToolCatalog.All, CancellationToken.None);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual(JsonValueKind.String, doc.RootElement.GetProperty("messages")[0].GetProperty("content").ValueKind);
    }
}
