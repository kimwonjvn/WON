using Span.Services.Assistant;

namespace Span.Tests.Unit;

[TestClass]
public class AssistantChatStoreTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup() { _dir = Path.Combine(Path.GetTempPath(), "won-chats-" + Guid.NewGuid().ToString("N")); }

    [TestCleanup]
    public void Cleanup() { try { Directory.Delete(_dir, true); } catch { } }

    private static StoredChat Chat(string firstText, DateTime? updated = null) => new()
    {
        Updated = updated ?? DateTime.Now,
        Messages =
        {
            new StoredChatMessage { Role = "user", Text = firstText },
            new StoredChatMessage { Role = "assistant", Text = "answer" },
        }
    };

    [TestMethod]
    public void Save_then_load_round_trips_and_derives_title()
    {
        var store = new AssistantChatStore(_dir);
        var chat = Chat("정리해 줘   이 폴더를\n부탁해");
        Assert.IsTrue(store.Save(chat));

        var loaded = store.Load(chat.Id)!;
        Assert.AreEqual("정리해 줘 이 폴더를 부탁해", loaded.Title);
        Assert.AreEqual(2, loaded.Messages.Count);
        Assert.AreEqual("assistant", loaded.Messages[1].Role);
    }

    [TestMethod]
    public void List_is_newest_first_and_ignores_foreign_files()
    {
        var store = new AssistantChatStore(_dir);
        var older = Chat("old", DateTime.Now.AddHours(-2));
        var newer = Chat("new", DateTime.Now);
        store.Save(older); store.Save(newer);
        File.WriteAllText(Path.Combine(_dir, "notes.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, new string('a', 32) + ".json"), "not json");

        var list = store.List();
        Assert.AreEqual(2, list.Count);
        Assert.AreEqual(newer.Id, list[0].Id);
    }

    [TestMethod]
    public void Ids_that_are_not_guids_are_rejected_so_paths_cannot_escape()
    {
        var store = new AssistantChatStore(_dir);
        Assert.IsFalse(store.Save(new StoredChat { Id = @"..\evil" }));
        Assert.IsNull(store.Load(@"..\evil"));
        Assert.IsFalse(store.Delete(@"..\..\x"));
        Assert.IsFalse(AssistantChatStore.IsValidId("ABC"));
        Assert.IsTrue(AssistantChatStore.IsValidId(Guid.NewGuid().ToString("N")));
    }

    [TestMethod]
    public void Delete_and_delete_all_remove_only_chat_files()
    {
        var store = new AssistantChatStore(_dir);
        var a = Chat("a"); var b = Chat("b");
        store.Save(a); store.Save(b);
        var keep = Path.Combine(_dir, "keep.txt");
        File.WriteAllText(keep, "x");

        Assert.IsTrue(store.Delete(a.Id));
        Assert.IsNull(store.Load(a.Id));
        Assert.AreEqual(1, store.DeleteAll());
        Assert.AreEqual(0, store.List().Count);
        Assert.IsTrue(File.Exists(keep));
    }

    [TestMethod]
    public void Save_caps_message_count_and_text_length()
    {
        var store = new AssistantChatStore(_dir);
        var chat = new StoredChat();
        for (int i = 0; i < AssistantChatStore.MaxMessagesPerChat + 20; i++)
            chat.Messages.Add(new StoredChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Text = new string('x', AssistantChatStore.MaxTextLength + 50) });
        store.Save(chat);

        var loaded = store.Load(chat.Id)!;
        Assert.AreEqual(AssistantChatStore.MaxMessagesPerChat, loaded.Messages.Count);
        Assert.AreEqual(AssistantChatStore.MaxTextLength, loaded.Messages[0].Text.Length);
    }

    [TestMethod]
    public void MakeTitle_truncates_long_text()
    {
        var t = AssistantChatStore.MakeTitle(new string('가', 100));
        Assert.AreEqual(AssistantChatStore.MaxTitleLength, t.Length);
        Assert.IsTrue(t.EndsWith("…"));
        Assert.AreEqual("New chat", AssistantChatStore.MakeTitle("   "));
    }
}

[TestClass]
public class AssistantRestoreHistoryTests
{
    private sealed class SeenClient : ILlmClient
    {
        public List<LlmMessage> Last { get; private set; } = new();
        public Task<LlmReply> SendAsync(string system, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDef> tools, CancellationToken ct)
        {
            Last = messages.ToList();
            return Task.FromResult(new LlmReply("ok", Array.Empty<LlmToolCall>()));
        }
    }

    [TestMethod]
    public async Task Restored_conversation_is_sent_before_the_new_question()
    {
        var client = new SeenClient();
        var agent = new AssistantAgent(client, null);
        agent.RestoreHistory(new[] { (true, "first"), (false, "reply") });

        await agent.RunAsync("second", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "first", "reply", "second" }, client.Last.Select(m => m.Text).ToArray());
        CollectionAssert.AreEqual(new[] { LlmRole.User, LlmRole.Assistant, LlmRole.User }, client.Last.Select(m => m.Role).ToArray());
    }

    [TestMethod]
    public void Restore_drops_leading_assistant_dangling_user_and_merges_same_roles()
    {
        var agent = new AssistantAgent(new SeenClient(), null);
        agent.RestoreHistory(new[]
        {
            (false, "orphan reply"), (true, "q1"), (true, "q1b"), (false, "a1"), (true, "dangling"),
        });
        Assert.AreEqual(2, agent.HistoryCount); // "q1\n\nq1b" + "a1"
    }
}
