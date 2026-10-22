using System.Text;
using System.Text.Json;

namespace Span.Services.Assistant;

public enum LocalAiState
{
    /// <summary>Ollama 에 연결할 수 없음 (설치 안 됨 / 실행 중 아님).</summary>
    NotReachable,
    /// <summary>Ollama 는 실행 중인데 선택한 모델이 아직 없음.</summary>
    ModelMissing,
    /// <summary>모델은 있지만 도구 호출(폴더 작업)을 지원하지 않음.</summary>
    NoToolSupport,
    Ready,
    /// <summary>예상하지 못한 응답.</summary>
    Error,
}

public sealed record LocalAiStatus(LocalAiState State, string Detail, IReadOnlyList<string> InstalledModels);

public sealed record PullProgress(string Status, long Completed, long Total)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : null;
}

/// <summary>
/// 로컬 AI(Ollama) 연결 도우미: 실행 여부 / 모델 설치 여부 / 도구 호출 지원 여부를 확인하고,
/// 모델 내려받기(/api/pull)와 초심자용 오류 설명을 제공한다. 이 PC(localhost)와만 통신한다.
/// </summary>
public static class LocalAiHelper
{
    public const string DefaultModel = "qwen3.5:9b";
    public const string DownloadUrl = "https://ollama.com/download";
    public const string GuideUrl = "https://github.com/kimwonjvn/WON/blob/main/docs/AI_ASSISTANT_GUIDE.md";

    /// <summary>OpenAI 호환 주소(.../v1)를 Ollama 기본 주소로 되돌린다.</summary>
    public static Uri Root(Uri baseUri)
    {
        var s = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (s.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) s = s[..^3];
        return new Uri(s + "/");
    }

    /// <summary>"qwen3.5" 처럼 태그가 없으면 ":latest" 로 보고 비교한다 (대소문자 무시).</summary>
    public static bool ModelMatches(string installed, string wanted)
    {
        static string Norm(string m) { m = m.Trim().ToLowerInvariant(); return m.Contains(':') ? m : m + ":latest"; }
        return Norm(installed) == Norm(wanted);
    }

    public static async Task<LocalAiStatus> CheckAsync(HttpClient http, Uri baseUri, string model, CancellationToken ct)
    {
        var root = Root(baseUri);
        List<string> installed;
        try
        {
            using var resp = await http.GetAsync(new Uri(root, "api/tags"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new LocalAiStatus(LocalAiState.Error, $"HTTP {(int)resp.StatusCode}", Array.Empty<string>());
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            installed = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) installed.Add(name);
        }
        catch (HttpRequestException ex) { return new LocalAiStatus(LocalAiState.NotReachable, ex.Message, Array.Empty<string>()); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new LocalAiStatus(LocalAiState.NotReachable, "timeout", Array.Empty<string>()); }
        catch (JsonException) { return new LocalAiStatus(LocalAiState.Error, "unexpected response (is this really Ollama?)", Array.Empty<string>()); }

        var match = installed.FirstOrDefault(i => ModelMatches(i, model));
        if (match == null)
            return new LocalAiStatus(LocalAiState.ModelMissing, model, installed);

        // 도구 호출 지원 여부 (Ollama 0.6+ 는 capabilities 를 알려 준다; 없으면 확인 불가로 보고 통과)
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/show"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model = match }), Encoding.UTF8, "application/json")
            };
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                {
                    var list = caps.EnumerateArray().Select(c => c.GetString()).ToList();
                    if (!list.Contains("tools"))
                        return new LocalAiStatus(LocalAiState.NoToolSupport, match, installed);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { /* 확인 불가: 그대로 진행 */ }

        return new LocalAiStatus(LocalAiState.Ready, match, installed);
    }

    /// <summary>모델을 내려받는다 (스트리밍 진행률). 용량이 크므로 호출하는 쪽에서 사용자 확인을 받아야 한다.</summary>
    public static async Task PullAsync(HttpClient http, Uri baseUri, string model, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Root(baseUri), "api/pull"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, stream = true }), Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new LlmException($"HTTP {(int)resp.StatusCode}: {(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim()}");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new LlmException(err.GetString() ?? "pull failed");
            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            long total = root.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv) ? tv : 0;
            long done = root.TryGetProperty("completed", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
            progress?.Report(new PullProgress(status, done, total));
        }
    }

    /// <summary>초심자가 이해할 수 있는 문장으로 상태를 설명한다.</summary>
    public static string Describe(LocalAiStatus s, string model, bool korean) => s.State switch
    {
        LocalAiState.Ready => korean
            ? $"✓ 준비 완료 — Ollama가 실행 중이고 '{model}' 모델을 쓸 수 있습니다."
            : $"✓ Ready — Ollama is running and the '{model}' model is available.",
        LocalAiState.ModelMissing => korean
            ? $"✗ Ollama는 실행 중이지만 '{model}' 모델이 아직 없습니다. 아래 '모델 받기'를 누르세요."
            : $"✗ Ollama is running, but the '{model}' model is not installed yet. Click \"Get the model\" below.",
        LocalAiState.NoToolSupport => korean
            ? $"✗ '{model}' 모델은 폴더 작업(도구 호출)을 지원하지 않습니다. 모델 메뉴에서 다른 모델(예: qwen3.5:9b)을 고르세요."
            : $"✗ The '{model}' model cannot use tools (folder actions). Pick another model, e.g. qwen3.5:9b.",
        LocalAiState.NotReachable => korean
            ? "✗ Ollama에 연결할 수 없습니다. 아직 설치하지 않았다면 'Ollama 내려받기'를, 설치했다면 시작 메뉴에서 Ollama를 실행한 뒤 다시 확인하세요."
            : "✗ Cannot reach Ollama. If it is not installed yet, click \"Download Ollama\"; if it is, start it from the Start menu and check again.",
        _ => korean ? $"✗ 확인하지 못했습니다: {s.Detail}" : $"✗ Could not check: {s.Detail}",
    };

    /// <summary>채팅 중 로컬 모델 호출이 실패했을 때 원인과 해결 방법을 알려 주는 문장 (알 수 없으면 null).</summary>
    public static string? ExplainError(string message, string model, bool korean)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("network error") || m.Contains("actively refused") || m.Contains("connection refused") || m.Contains("timed out"))
            return korean
                ? "Ollama에 연결하지 못했습니다. Ollama가 실행 중인지 확인하세요(시작 메뉴에서 Ollama 실행). 설정의 '연결 확인'을 눌러 보세요."
                : "Could not reach Ollama. Make sure it is running (start it from the Start menu), then press \"Check connection\" in the settings.";
        if (m.Contains("404") && (m.Contains("not found") || m.Contains("pull")))
            return korean
                ? $"'{model}' 모델이 설치되어 있지 않습니다. 설정의 '모델 받기'를 누르거나 명령 프롬프트에서 `ollama pull {model}` 을 실행하세요."
                : $"The '{model}' model is not installed. Press \"Get the model\" in the settings, or run `ollama pull {model}` in a terminal.";
        if (m.Contains("does not support tools") || m.Contains("tool"))
            return korean
                ? $"'{model}' 모델은 폴더 작업(도구 호출)을 지원하지 않는 것 같습니다. 모델 메뉴에서 qwen3.5:9b 같은 도구 호출 모델을 고르세요."
                : $"The '{model}' model seems not to support tools. Pick a tool-capable model such as qwen3.5:9b.";
        if (m.Contains("memory") || m.Contains("out of memory") || m.Contains("insufficient"))
            return korean
                ? "메모리가 부족합니다. 더 작은 모델(예: qwen3.5:4b)을 고르거나 다른 프로그램을 닫아 보세요."
                : "Not enough memory. Choose a smaller model (e.g. qwen3.5:4b) or close other programs.";
        return null;
    }
}
