using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Span.Services.Assistant;

public enum LlmRole { User, Assistant, Tool }

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

public sealed class LlmMessage
{
    public LlmRole Role { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = Array.Empty<LlmToolCall>();
    /// <summary>Role == Tool 일 때 응답 대상 호출 ID.</summary>
    public string ToolCallId { get; init; } = string.Empty;
    public bool IsError { get; init; }
}

public sealed record LlmReply(string Text, IReadOnlyList<LlmToolCall> ToolCalls);

public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
}

public interface ILlmClient
{
    Task<LlmReply> SendAsync(string system, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDef> tools, CancellationToken ct);
}

public static class LlmEndpoint
{
    /// <summary>사용자 지정 주소 검증: https 만 허용 (localhost 는 http 허용). 비우면 기본 주소.</summary>
    public static bool TryResolveBase(string? custom, string defaultBase, out Uri baseUri, out string error)
    {
        error = string.Empty;
        var text = string.IsNullOrWhiteSpace(custom) ? defaultBase : custom.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri == null)
        {
            baseUri = new Uri(defaultBase);
            error = "base URL is not a valid absolute URL";
            return false;
        }
        bool isLocal = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(isLocal && uri.Scheme == Uri.UriSchemeHttp))
        {
            baseUri = new Uri(defaultBase);
            error = "base URL must use https (http is allowed only for localhost)";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { baseUri = new Uri(defaultBase); error = "base URL must not contain credentials"; return false; }
        baseUri = uri;
        return true;
    }

    /// <summary>base 가 이미 /v1 로 끝나면 중복 없이 이어 붙인다.</summary>
    public static Uri Combine(Uri baseUri, string v1Relative)
    {
        var b = baseUri.AbsoluteUri.TrimEnd('/');
        var rel = b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? v1Relative : "v1/" + v1Relative;
        return new Uri(b + "/" + rel);
    }
}

internal static class LlmHttp
{
    public static async Task<JsonDocument> PostJsonAsync(HttpClient http, Uri url, string body, Action<HttpRequestMessage> auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        auth(req);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new LlmException("request timed out"); }
        catch (HttpRequestException ex) { throw new LlmException($"network error: {ex.Message}"); }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new LlmException($"HTTP {(int)resp.StatusCode}: {ExtractError(text)}");
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { throw new LlmException("the server returned an invalid response"); }
        }
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return Trim(e.GetString());
                if (e.TryGetProperty("message", out var m)) return Trim(m.GetString());
            }
        }
        catch { /* fall through */ }
        return Trim(body);
    }

    private static string Trim(string? s)
    {
        s ??= string.Empty;
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}

/// <summary>Anthropic Messages API.</summary>
public sealed class ClaudeClient : ILlmClient
{
    public const string DefaultBase = "https://api.anthropic.com";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly Uri _url;

    public ClaudeClient(HttpClient http, string apiKey, string model, Uri baseUri)
    {
        _http = http; _apiKey = apiKey; _model = model;
        _url = LlmEndpoint.Combine(baseUri, "messages");
    }

    public async Task<LlmReply> SendAsync(string system, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDef> tools, CancellationToken ct)
    {
        var body = BuildRequest(system, messages, tools);
        using var doc = await LlmHttp.PostJsonAsync(_http, _url, body, r =>
        {
            r.Headers.Add("x-api-key", _apiKey);
            r.Headers.Add("anthropic-version", "2023-06-01");
        }, ct).ConfigureAwait(false);
        return ParseResponse(doc.RootElement);
    }

    internal string BuildRequest(string system, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDef> tools)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", _model);
            w.WriteNumber("max_tokens", 4096);
            w.WriteString("system", system);

            w.WriteStartArray("tools");
            foreach (var t in tools)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WritePropertyName("input_schema");
                using (var schema = JsonDocument.Parse(t.SchemaJson)) schema.RootElement.WriteTo(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("messages");
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m.Role == LlmRole.User)
                {
                    w.WriteStartObject(); w.WriteString("role", "user"); w.WriteString("content", m.Text); w.WriteEndObject();
                }
                else if (m.Role == LlmRole.Assistant)
                {
                    w.WriteStartObject(); w.WriteString("role", "assistant");
                    w.WriteStartArray("content");
                    if (!string.IsNullOrEmpty(m.Text))
                    {
                        w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", m.Text); w.WriteEndObject();
                    }
                    foreach (var c in m.ToolCalls)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "tool_use"); w.WriteString("id", c.Id); w.WriteString("name", c.Name);
                        w.WritePropertyName("input");
                        using (var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson)) args.RootElement.WriteTo(w);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray(); w.WriteEndObject();
                }
                else
                {
                    // 연속된 tool 결과는 하나의 user 메시지로 묶는다
                    w.WriteStartObject(); w.WriteString("role", "user");
                    w.WriteStartArray("content");
                    int j = i;
                    while (j < messages.Count && messages[j].Role == LlmRole.Tool)
                    {
                        var r = messages[j];
                        w.WriteStartObject();
                        w.WriteString("type", "tool_result"); w.WriteString("tool_use_id", r.ToolCallId);
                        w.WriteString("content", r.Text);
                        if (r.IsError) w.WriteBoolean("is_error", true);
                        w.WriteEndObject();
                        j++;
                    }
                    i = j - 1;
                    w.WriteEndArray(); w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static LlmReply ParseResponse(JsonElement root)
    {
        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new LlmException("unexpected response format");

        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "text" && block.TryGetProperty("text", out var tx)) text.Append(tx.GetString());
            else if (type == "tool_use")
            {
                var id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                calls.Add(new LlmToolCall(id, name, input));
            }
        }
        return new LlmReply(text.ToString(), calls);
    }
}

/// <summary>OpenAI Chat Completions API (function calling). OpenAI 호환 서버도 base URL 로 사용 가능.</summary>
public sealed class OpenAiClient : ILlmClient
{
    public const string DefaultBase = "https://api.openai.com";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly Uri _url;

    public OpenAiClient(HttpClient http, string apiKey, string model, Uri baseUri)
    {
        _http = http; _apiKey = apiKey; _model = model;
        _url = LlmEndpoint.Combine(baseUri, "chat/completions");
    }

    public async Task<LlmReply> SendAsync(string system, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDef> tools, CancellationToken ct)
    {
        var body = BuildRequest(system, messages, tools);
        using var doc = await LlmHttp.PostJsonAsync(_http, _url, body, r =>
        {
            r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }, ct).ConfigureAwait(false);
        return ParseResponse(doc.RootElement);
    }

    internal string BuildRequest(string system, IReadOnlyList<LlmMessage> messages, IReadOnlyList<ToolDef> tools)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", _model);

            w.WriteStartArray("tools");
            foreach (var t in tools)
            {
                w.WriteStartObject();
                w.WriteString("type", "function");
                w.WriteStartObject("function");
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WritePropertyName("parameters");
                using (var schema = JsonDocument.Parse(t.SchemaJson)) schema.RootElement.WriteTo(w);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("messages");
            w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", system); w.WriteEndObject();
            foreach (var m in messages)
            {
                w.WriteStartObject();
                switch (m.Role)
                {
                    case LlmRole.User:
                        w.WriteString("role", "user"); w.WriteString("content", m.Text);
                        break;
                    case LlmRole.Assistant:
                        w.WriteString("role", "assistant");
                        if (string.IsNullOrEmpty(m.Text)) w.WriteNull("content"); else w.WriteString("content", m.Text);
                        if (m.ToolCalls.Count > 0)
                        {
                            w.WriteStartArray("tool_calls");
                            foreach (var c in m.ToolCalls)
                            {
                                w.WriteStartObject();
                                w.WriteString("id", c.Id); w.WriteString("type", "function");
                                w.WriteStartObject("function");
                                w.WriteString("name", c.Name);
                                w.WriteString("arguments", string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson);
                                w.WriteEndObject();
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                        break;
                    default:
                        w.WriteString("role", "tool"); w.WriteString("tool_call_id", m.ToolCallId); w.WriteString("content", m.Text);
                        break;
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static LlmReply ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new LlmException("unexpected response format");
        var msg = choices[0].GetProperty("message");

        var text = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        var calls = new List<LlmToolCall>();
        if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var fn = tc.GetProperty("function");
                var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var args = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                calls.Add(new LlmToolCall(id, name, args));
            }
        }
        return new LlmReply(text, calls);
    }
}
