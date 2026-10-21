using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Span.Services.Assistant;

public sealed class AssistantResult
{
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<PlannedOperation> Plan { get; init; } = Array.Empty<PlannedOperation>();
    public bool HitTurnLimit { get; init; }
}

/// <summary>
/// 도구 호출 루프. 읽기 도구는 즉시 실행하고, 변경 도구는 검증 후 "계획"에만 쌓는다.
/// 이 클래스는 파일을 절대 변경하지 않는다 — 계획 실행은 사용자 승인 후 UI 쪽에서 한다.
/// </summary>
public sealed class AssistantAgent
{
    public const int MaxPlanSize = 200;

    public const int MaxHistoryMessages = 60;

    private ILlmClient _client;
    private readonly int _maxTurns;
    private readonly List<LlmMessage> _history = new();

    /// <summary>지금 앱에서 열려 있는 폴더 (대화 중에 바뀔 수 있다).</summary>
    public string? CurrentFolder { get; set; }

    /// <summary>켜진 스킬에서 만든 추가 지침 (<see cref="AssistantSkillPrompt.Build"/>).</summary>
    public string SkillInstructions { get; set; } = string.Empty;

    public int HistoryCount => _history.Count;

    /// <summary>모델/서비스를 바꿔도 대화 기록은 유지된다.</summary>
    public ILlmClient Client { get => _client; set => _client = value; }
    private readonly AssistantReadTools _read = new();

    private readonly List<PlannedOperation> _plan = new();
    private readonly HashSet<string> _plannedFolders = new(StringComparer.OrdinalIgnoreCase);

    public AssistantAgent(ILlmClient client, string? currentFolder, int maxTurns = 8)
    {
        _client = client;
        CurrentFolder = currentFolder;
        _maxTurns = maxTurns;
    }

    /// <summary>대화 기록과 계획을 모두 비운다 (새 대화).</summary>
    public void ResetConversation()
    {
        _history.Clear();
        _plan.Clear();
        _plannedFolders.Clear();
    }

    public string BuildSystemPrompt() =>
        "You are the file-management assistant inside WON, a Windows file explorer. " +
        "Help the user explore and organise folders using the provided tools.\n" +
        "Rules:\n" +
        "- Always use absolute Windows paths.\n" +
        "- Use list_directory / search_files / get_file_info to look before you act. Through the tools you can only see names, sizes and dates; file contents are visible only when the user attaches a file or image to a message.\n" +
        "- Tools starting with 'propose_' do NOT change anything: they queue an action that the user must approve. Never say a change has been made; say it is proposed.\n" +
        "- Never propose deleting unless the user explicitly asked for deletion. Deletion only moves items to the Recycle Bin.\n" +
        "- Keep plans small and exact; if the request is ambiguous, ask a short question instead of guessing.\n" +
        "- Reply in the user's language, concisely.\n" +
        (string.IsNullOrEmpty(CurrentFolder) ? "- No folder is currently open.\n" : $"- The folder currently open in the app is: {CurrentFolder}\n") +
        SkillInstructions;

    /// <summary>
    /// 사용자 메시지 한 건을 처리한다. 같은 인스턴스로 호출하면 이전 대화가 이어진다.
    /// 이번 호출에서 제안된 변경만 Plan 으로 돌려준다 (이전 호출의 계획은 비운다).
    /// 실패/취소하면 이번 호출에서 쌓인 대화 기록은 되돌려서 다음 호출이 깨끗하게 이어지게 한다.
    /// </summary>
    public async Task<AssistantResult> RunAsync(string userRequest, CancellationToken ct, Action<string>? progress = null,
        IReadOnlyList<LlmAttachment>? attachments = null)
    {
        TrimHistory();
        _plan.Clear();
        _plannedFolders.Clear();

        var system = BuildSystemPrompt();
        int mark = _history.Count;
        _history.Add(new LlmMessage { Role = LlmRole.User, Text = userRequest, Attachments = attachments ?? Array.Empty<LlmAttachment>() });
        string lastText = string.Empty;

        try
        {
            for (int turn = 0; turn < _maxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();
                var reply = await _client.SendAsync(system, _history, AssistantToolCatalog.All, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(reply.Text)) lastText = reply.Text;
                _history.Add(new LlmMessage { Role = LlmRole.Assistant, Text = reply.Text, ToolCalls = reply.ToolCalls });

                if (reply.ToolCalls.Count == 0)
                    return new AssistantResult { Text = lastText, Plan = _plan.ToList() };

                foreach (var call in reply.ToolCalls)
                {
                    progress?.Invoke(call.Name);
                    var (result, isError) = HandleToolCall(call);
                    _history.Add(new LlmMessage { Role = LlmRole.Tool, ToolCallId = call.Id, Text = result, IsError = isError });
                }
            }

            return new AssistantResult { Text = lastText, Plan = _plan.ToList(), HitTurnLimit = true };
        }
        catch
        {
            _history.RemoveRange(mark, _history.Count - mark);
            throw;
        }
    }

    /// <summary>기록이 너무 길면 오래된 대화를 (사용자 메시지 경계에서) 잘라 낸다.</summary>
    private void TrimHistory()
    {
        while (_history.Count > MaxHistoryMessages)
        {
            int next = _history.FindIndex(1, m => m.Role == LlmRole.User);
            if (next < 0) break;
            _history.RemoveRange(0, next);
        }
    }

    internal (string Result, bool IsError) HandleToolCall(LlmToolCall call)
    {
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (AssistantReadTools.Error("arguments are not valid JSON"), true);
        }

        if (_read.IsReadTool(call.Name))
        {
            var r = _read.Execute(call.Name, args);
            return (r, r.StartsWith("{\"error\"", StringComparison.Ordinal));
        }

        if (!AssistantToolCatalog.IsMutating(call.Name))
            return (AssistantReadTools.Error($"unknown tool '{call.Name}'"), true);

        if (_plan.Count >= MaxPlanSize)
            return (AssistantReadTools.Error($"plan is full ({MaxPlanSize} operations max)"), true);

        var err = Propose(call.Name, args);
        return err == null
            ? ($"{{\"queued\":true,\"position\":{_plan.Count},\"note\":\"awaiting user approval; nothing has been changed yet\"}}", false)
            : (AssistantReadTools.Error(err), true);
    }

    // 존재 여부는 이미 계획된 폴더도 포함해서 본다 (create_folder 후 그 안으로 move 가능)
    private bool Exists(string p) => File.Exists(p) || Directory.Exists(p) || _plannedFolders.Contains(Trim(p));
    private static string Trim(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private string? Propose(string tool, JsonElement args)
    {
        switch (tool)
        {
            case "propose_move":
            case "propose_copy":
            {
                if (!AssistantPathPolicy.TryValidateMutationPath(AssistantReadTools.Str(args, "source"), out var src, out var e1)) return $"source: {e1}";
                if (!AssistantPathPolicy.TryValidateMutationPath(AssistantReadTools.Str(args, "destination_directory"), out var dstDir, out var e2)) return $"destination_directory: {e2}";
                if (!File.Exists(src) && !Directory.Exists(src)) return "source does not exist";
                if (!Directory.Exists(dstDir) && !_plannedFolders.Contains(Trim(dstDir))) return "destination_directory does not exist (propose_create_folder first)";
                if (AssistantPathPolicy.IsSameOrUnder(dstDir, src)) return "cannot move/copy a folder into itself";
                var target = Path.Combine(dstDir, Path.GetFileName(Trim(src)));
                if (Exists(target)) return "an item with that name already exists in the destination";
                var kind = tool == "propose_move" ? PlannedOperationKind.Move : PlannedOperationKind.Copy;
                _plan.Add(new PlannedOperation(kind, src, dstDir));
                return null;
            }
            case "propose_rename":
            {
                if (!AssistantPathPolicy.TryValidateMutationPath(AssistantReadTools.Str(args, "path"), out var src, out var e1)) return $"path: {e1}";
                var newName = AssistantReadTools.Str(args, "new_name");
                if (!AssistantPathPolicy.IsValidName(newName, out var e2)) return $"new_name: {e2}";
                if (!File.Exists(src) && !Directory.Exists(src)) return "path does not exist";
                var target = Path.Combine(Path.GetDirectoryName(Trim(src)) ?? string.Empty, newName!);
                if (Exists(target) && !string.Equals(target, src, StringComparison.OrdinalIgnoreCase)) return "an item with that name already exists";
                _plan.Add(new PlannedOperation(PlannedOperationKind.Rename, src, newName));
                return null;
            }
            case "propose_create_folder":
            {
                if (!AssistantPathPolicy.TryValidateMutationPath(AssistantReadTools.Str(args, "path"), out var p, out var e1)) return $"path: {e1}";
                var parent = Path.GetDirectoryName(Trim(p));
                if (string.IsNullOrEmpty(parent) || (!Directory.Exists(parent) && !_plannedFolders.Contains(Trim(parent)))) return "parent folder does not exist";
                if (!AssistantPathPolicy.IsValidName(Path.GetFileName(Trim(p)), out var e2)) return $"folder name: {e2}";
                if (Exists(p)) return "an item with that name already exists";
                _plannedFolders.Add(Trim(p));
                _plan.Add(new PlannedOperation(PlannedOperationKind.CreateFolder, p, null));
                return null;
            }
            case "propose_delete":
            {
                if (!AssistantPathPolicy.TryValidateMutationPath(AssistantReadTools.Str(args, "path"), out var p, out var e1)) return $"path: {e1}";
                if (!File.Exists(p) && !Directory.Exists(p)) return "path does not exist";
                _plan.Add(new PlannedOperation(PlannedOperationKind.Delete, p, null));
                return null;
            }
            default:
                return $"unknown tool '{tool}'";
        }
    }
}
