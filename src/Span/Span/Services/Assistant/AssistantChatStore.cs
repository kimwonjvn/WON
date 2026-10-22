using System.Text.Json;
using System.Text.RegularExpressions;

namespace Span.Services.Assistant;

public sealed class StoredChatMessage
{
    /// <summary>"user" | "assistant"</summary>
    public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
    /// <summary>첨부했던 파일 이름만 기록한다 (내용은 저장하지 않는다).</summary>
    public List<string> Attachments { get; set; } = new();
}

public sealed class StoredChat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<StoredChatMessage> Messages { get; set; } = new();
}

public sealed record StoredChatSummary(string Id, string Title, DateTime Updated, int MessageCount);

/// <summary>
/// 어시스턴트 대화 기록: %LOCALAPPDATA%\WON\assistant-chats\{id}.json (대화 하나당 파일 하나).
/// 사용자가 보낸 글과 어시스턴트의 답만 저장한다. 도구 호출 결과·첨부 파일 내용·API 키는 저장하지 않는다.
/// </summary>
public sealed class AssistantChatStore
{
    public const int MaxChats = 200;
    public const int MaxMessagesPerChat = 200;
    public const int MaxTextLength = 20_000;
    public const int MaxTitleLength = 48;

    private static readonly Regex IdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _dir;

    public AssistantChatStore(string directory) { _dir = directory; }

    public static bool IsValidId(string? id) => id != null && IdPattern.IsMatch(id);

    private string PathFor(string id) => System.IO.Path.Combine(_dir, id + ".json");

    public static string MakeTitle(string firstUserText)
    {
        var t = Regex.Replace(firstUserText ?? string.Empty, @"\s+", " ").Trim();
        if (t.Length == 0) return "New chat";
        return t.Length <= MaxTitleLength ? t : t[..(MaxTitleLength - 1)] + "…";
    }

    public IReadOnlyList<StoredChatSummary> List()
    {
        var result = new List<StoredChatSummary>();
        try
        {
            if (!Directory.Exists(_dir)) return result;
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(file);
                if (!IsValidId(id)) continue;
                var chat = TryRead(file);
                if (chat == null || chat.Id != id) continue;
                result.Add(new StoredChatSummary(chat.Id, chat.Title, chat.Updated, chat.Messages.Count));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result.OrderByDescending(c => c.Updated).ToList();
    }

    public StoredChat? Load(string id)
    {
        if (!IsValidId(id)) return null;
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        var chat = TryRead(path);
        return chat != null && chat.Id == id ? chat : null;
    }

    public bool Save(StoredChat chat)
    {
        if (!IsValidId(chat.Id)) return false;
        try
        {
            Directory.CreateDirectory(_dir);
            Normalize(chat);
            var tmp = PathFor(chat.Id) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(chat, Json), System.Text.Encoding.UTF8);
            File.Move(tmp, PathFor(chat.Id), overwrite: true);
            Prune();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id)) return false;
        try
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>모든 대화 기록을 지운다. 지운 개수를 돌려준다.</summary>
    public int DeleteAll()
    {
        int count = 0;
        try
        {
            if (!Directory.Exists(_dir)) return 0;
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                if (!IsValidId(System.IO.Path.GetFileNameWithoutExtension(file))) continue;
                try { File.Delete(file); count++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return count;
    }

    private static void Normalize(StoredChat chat)
    {
        if (chat.Messages.Count > MaxMessagesPerChat)
            chat.Messages = chat.Messages.Skip(chat.Messages.Count - MaxMessagesPerChat).ToList();
        foreach (var m in chat.Messages)
        {
            if (m.Text.Length > MaxTextLength) m.Text = m.Text[..MaxTextLength];
            m.Role = m.Role == "assistant" ? "assistant" : "user";
        }
        if (string.IsNullOrWhiteSpace(chat.Title))
            chat.Title = MakeTitle(chat.Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? string.Empty);
    }

    private void Prune()
    {
        var all = List();
        for (int i = MaxChats; i < all.Count; i++) Delete(all[i].Id);
    }

    private static StoredChat? TryRead(string path)
    {
        try { return JsonSerializer.Deserialize<StoredChat>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
