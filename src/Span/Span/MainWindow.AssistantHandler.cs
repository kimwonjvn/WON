using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Span.Models;
using Span.Services;
using Span.Services.Assistant;
using Span.Services.FileOperations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Span
{
    /// <summary>
    /// WON Assistant — Claude / OpenAI API 로 폴더 탐색·정리 명령을 내리는 대화상자.
    /// 안전 원칙: 기본 비활성, 파일 내용은 전송하지 않음(이름·경로·크기·날짜만),
    /// 변경 작업은 계획으로만 쌓고 사용자가 체크·승인한 것만 기존 파일 작업 엔진으로 실행(삭제는 휴지통, 실행 취소 지원).
    /// </summary>
    public partial class MainWindow
    {
        private const string AsstEnabledKey = "AssistantEnabled";
        private const string AsstProviderKey = "AssistantProvider";
        private const string AsstModelKey = "AssistantModel";
        private const string AsstBaseUrlKey = "AssistantBaseUrl";

        private static readonly HttpClient AssistantHttp = new() { Timeout = TimeSpan.FromSeconds(90) };

        private static string DefaultModel(string provider) => provider == "OpenAI" ? "gpt-4o" : "claude-sonnet-5-5";

        private async Task ShowAssistantDialogAsync()
        {
            if (_isContentDialogOpen) return;

            var settings = App.Current.Services.GetRequiredService<SettingsService>();
            bool ko = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
            string T(string en, string kr) => ko ? kr : en;

            string provider = settings.Get(AsstProviderKey, "Claude");
            if (provider != "Claude" && provider != "OpenAI") provider = "Claude";
            bool enabled = settings.Get(AsstEnabledKey, false);
            bool hasKey = AssistantCredentialStore.Load(provider) != null;
            string? folder = ViewModel.ActiveExplorer?.CurrentPath;
            if (folder != null && !System.IO.Directory.Exists(folder)) folder = null;

            // ── 설정 영역 ──
            var enableToggle = new ToggleSwitch
            {
                Header = T("Enable assistant", "어시스턴트 사용"),
                IsOn = enabled, OnContent = "", OffContent = ""
            };
            var privacy = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8,
                Text = T("When enabled, folder/file NAMES, paths, sizes and dates that you ask about are sent to the selected provider. File contents are never sent. Changes are only made after you approve a plan.",
                         "사용하면 질문과 관련된 폴더/파일의 이름·경로·크기·날짜가 선택한 서비스로 전송됩니다. 파일 내용은 전송되지 않으며, 변경은 계획을 승인한 뒤에만 실행됩니다.")
            };
            var providerCombo = new ComboBox { Header = T("Provider", "서비스"), MinWidth = 200 };
            providerCombo.Items.Add("Claude");
            providerCombo.Items.Add("OpenAI");
            providerCombo.SelectedItem = provider;
            var modelBox = new TextBox { Header = T("Model", "모델"), Text = settings.Get(AsstModelKey, DefaultModel(provider)) };
            var baseBox = new TextBox
            {
                Header = T("Base URL (optional, https only)", "Base URL (선택, https만)"),
                PlaceholderText = provider == "OpenAI" ? OpenAiClient.DefaultBase : ClaudeClient.DefaultBase,
                Text = settings.Get(AsstBaseUrlKey, "")
            };
            var keyBox = new PasswordBox
            {
                Header = T("API key", "API 키"),
                PlaceholderText = hasKey ? T("(saved — leave blank to keep)", "(저장됨 — 비워 두면 유지)") : "sk-…"
            };
            var saveButton = new Button { Content = T("Save settings", "설정 저장") };
            var settingsStatus = new TextBlock { FontSize = 12 };

            providerCombo.SelectionChanged += (_, _) =>
            {
                var p = providerCombo.SelectedItem as string ?? "Claude";
                modelBox.Text = settings.Get(AsstModelKey + "." + p, DefaultModel(p));
                baseBox.PlaceholderText = p == "OpenAI" ? OpenAiClient.DefaultBase : ClaudeClient.DefaultBase;
                keyBox.PlaceholderText = AssistantCredentialStore.Load(p) != null ? T("(saved — leave blank to keep)", "(저장됨 — 비워 두면 유지)") : "sk-…";
            };

            saveButton.Click += (_, _) =>
            {
                var p = providerCombo.SelectedItem as string ?? "Claude";
                var model = string.IsNullOrWhiteSpace(modelBox.Text) ? DefaultModel(p) : modelBox.Text.Trim();
                if (!LlmEndpoint.TryResolveBase(baseBox.Text, p == "OpenAI" ? OpenAiClient.DefaultBase : ClaudeClient.DefaultBase, out _, out var urlErr))
                {
                    settingsStatus.Text = urlErr;
                    return;
                }
                settings.Set(AsstEnabledKey, enableToggle.IsOn);
                settings.Set(AsstProviderKey, p);
                settings.Set(AsstModelKey, model);
                settings.Set(AsstModelKey + "." + p, model);
                settings.Set(AsstBaseUrlKey, baseBox.Text.Trim());
                if (!string.IsNullOrEmpty(keyBox.Password))
                {
                    if (!AssistantCredentialStore.Save(p, keyBox.Password.Trim()))
                    {
                        settingsStatus.Text = T("Could not store the API key securely.", "API 키를 안전하게 저장하지 못했습니다.");
                        return;
                    }
                    keyBox.Password = string.Empty;
                }
                settingsStatus.Text = T("Saved.", "저장했습니다.");
            };

            var settingsPanel = new StackPanel { Spacing = 8, Children = { enableToggle, privacy, providerCombo, modelBox, baseBox, keyBox, saveButton, settingsStatus } };
            var settingsExpander = new Expander
            {
                Header = T("Settings", "설정"),
                IsExpanded = !(enabled && hasKey),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = settingsPanel
            };

            // ── 작업 영역 ──
            var folderText = new TextBlock
            {
                FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap,
                Text = folder == null ? T("No folder open", "열려 있는 폴더 없음") : T("Current folder: ", "현재 폴더: ") + folder
            };
            var input = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72,
                PlaceholderText = T("e.g. Find *.log files older than 90 days in this folder and propose moving them to an 'old-logs' folder",
                                    "예: 이 폴더에서 90일 지난 *.log 파일을 찾아 'old-logs' 폴더로 옮기는 계획을 세워줘")
            };
            var sendButton = new Button { Content = T("Send", "보내기"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var cancelButton = new Button { Content = T("Stop", "중지"), IsEnabled = false };
            var ring = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
            var output = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var statusLine = new TextBlock { FontSize = 12, Opacity = 0.7 };

            var planHeader = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Visibility = Visibility.Collapsed,
                Text = T("Proposed changes — check what to run:", "제안된 변경 — 실행할 항목을 체크하세요:") };
            var planList = new StackPanel { Spacing = 2 };
            var runButton = new Button { Content = T("Run checked", "체크한 항목 실행"), Visibility = Visibility.Collapsed,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var discardButton = new Button { Content = T("Discard plan", "계획 버리기"), Visibility = Visibility.Collapsed };

            var panel = new StackPanel { Spacing = 10, Width = 580 };
            panel.Children.Add(settingsExpander);
            panel.Children.Add(folderText);
            panel.Children.Add(input);
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sendButton, cancelButton, ring } });
            panel.Children.Add(statusLine);
            panel.Children.Add(output);
            panel.Children.Add(planHeader);
            panel.Children.Add(planList);
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { runButton, discardButton } });

            var dialog = new ContentDialog
            {
                Title = "WON Assistant",
                Content = new ScrollViewer { Content = panel, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                CloseButtonText = T("Close", "닫기"),
                XamlRoot = this.Content.XamlRoot
            };

            CancellationTokenSource? cts = null;
            IReadOnlyList<PlannedOperation> currentPlan = Array.Empty<PlannedOperation>();
            var checks = new List<CheckBox>();

            void ClearPlan()
            {
                currentPlan = Array.Empty<PlannedOperation>();
                checks.Clear(); planList.Children.Clear();
                planHeader.Visibility = runButton.Visibility = discardButton.Visibility = Visibility.Collapsed;
            }

            void ShowPlan(IReadOnlyList<PlannedOperation> plan)
            {
                ClearPlan();
                if (plan.Count == 0) return;
                currentPlan = plan;
                foreach (var op in plan)
                {
                    var cb = new CheckBox { IsChecked = true, Content = new TextBlock { Text = op.Describe(), TextWrapping = TextWrapping.Wrap } };
                    checks.Add(cb); planList.Children.Add(cb);
                }
                planHeader.Visibility = runButton.Visibility = discardButton.Visibility = Visibility.Visible;
            }

            discardButton.Click += (_, _) => ClearPlan();
            cancelButton.Click += (_, _) => cts?.Cancel();

            sendButton.Click += async (_, _) =>
            {
                var request = input.Text.Trim();
                if (request.Length == 0) return;

                var p = settings.Get(AsstProviderKey, "Claude");
                if (!settings.Get(AsstEnabledKey, false)) { statusLine.Text = T("The assistant is disabled. Enable it in Settings above and save.", "어시스턴트가 꺼져 있습니다. 위 설정에서 켜고 저장하세요."); settingsExpander.IsExpanded = true; return; }
                var key = AssistantCredentialStore.Load(p);
                if (key == null) { statusLine.Text = T("No API key saved for " + p + ".", p + " API 키가 저장되어 있지 않습니다."); settingsExpander.IsExpanded = true; return; }
                if (!LlmEndpoint.TryResolveBase(settings.Get(AsstBaseUrlKey, ""), p == "OpenAI" ? OpenAiClient.DefaultBase : ClaudeClient.DefaultBase, out var baseUri, out var urlErr))
                { statusLine.Text = urlErr; return; }

                var model = settings.Get(AsstModelKey, DefaultModel(p));
                ILlmClient client = p == "OpenAI"
                    ? new OpenAiClient(AssistantHttp, key, model, baseUri)
                    : new ClaudeClient(AssistantHttp, key, model, baseUri);

                ClearPlan();
                output.Text = string.Empty;
                sendButton.IsEnabled = false; cancelButton.IsEnabled = true; ring.IsActive = true;
                statusLine.Text = T("Thinking…", "생각 중…");
                cts = new CancellationTokenSource();
                try
                {
                    var agent = new AssistantAgent(client, folder);
                    var result = await agent.RunAsync(request, cts.Token,
                        tool => DispatcherQueue.TryEnqueue(() => statusLine.Text = (T("Working: ", "작업 중: ")) + tool));
                    output.Text = result.Text;
                    statusLine.Text = result.HitTurnLimit ? T("Stopped at the step limit.", "단계 제한에 도달해 중단했습니다.") : string.Empty;
                    ShowPlan(result.Plan);
                }
                catch (OperationCanceledException) { statusLine.Text = T("Stopped.", "중지했습니다."); }
                catch (LlmException ex) { statusLine.Text = T("Error: ", "오류: ") + ex.Message; }
                catch (Exception ex)
                {
                    Helpers.DebugLogger.Log($"[Assistant] unexpected error: {ex.GetType().Name}");
                    statusLine.Text = T("Unexpected error: ", "예상치 못한 오류: ") + ex.Message;
                }
                finally
                {
                    sendButton.IsEnabled = true; cancelButton.IsEnabled = false; ring.IsActive = false;
                    cts?.Dispose(); cts = null;
                }
            };

            runButton.Click += async (_, _) =>
            {
                var chosen = currentPlan.Where((_, i) => checks[i].IsChecked == true).ToList();
                if (chosen.Count == 0) return;
                runButton.IsEnabled = discardButton.IsEnabled = sendButton.IsEnabled = false;
                try
                {
                    int done = 0;
                    foreach (var op in chosen)
                    {
                        statusLine.Text = $"{T("Running", "실행 중")} {done + 1}/{chosen.Count}: {op.Describe()}";
                        await ViewModel.ExecuteFileOperationAsync(ToOperation(op));
                        done++;
                    }
                    statusLine.Text = string.Format(T("Done: {0} operation(s). Use Ctrl+Z to undo.", "완료: {0}개 작업. Ctrl+Z 로 되돌릴 수 있습니다."), done);
                    ClearPlan();
                }
                catch (Exception ex)
                {
                    Helpers.DebugLogger.Log($"[Assistant] plan run error: {ex.GetType().Name}");
                    statusLine.Text = T("Stopped: ", "중단됨: ") + ex.Message;
                }
                finally
                {
                    runButton.IsEnabled = discardButton.IsEnabled = sendButton.IsEnabled = true;
                }
            };

            _isContentDialogOpen = true;
            try { await dialog.ShowAsync(); }
            finally { _isContentDialogOpen = false; cts?.Cancel(); }
        }

        private static IFileOperation ToOperation(PlannedOperation op) => op.Kind switch
        {
            PlannedOperationKind.Move => new MoveFileOperation(new List<string> { op.Source }, op.Target!),
            PlannedOperationKind.Copy => new CopyFileOperation(new List<string> { op.Source }, op.Target!),
            PlannedOperationKind.Rename => new RenameFileOperation(op.Source, op.Target!),
            PlannedOperationKind.CreateFolder => new NewFolderOperation(op.Source),
            PlannedOperationKind.Delete => new DeleteFileOperation(new List<string> { op.Source }, permanent: false),
            _ => throw new InvalidOperationException("unknown operation")
        };
    }
}
