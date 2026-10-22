using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Span.Services;
using Span.Services.Assistant;
using Span.Services.FileOperations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Span
{
    /// <summary>
    /// WON Assistant — 오른쪽에서 슬라이드로 열리는 대화 패널 (Claude / OpenAI API).
    /// 안전 원칙: 기본 비활성, API 키는 Windows 자격 증명에만 저장.
    /// 질문과 관련된 폴더/파일의 이름·경로·크기·날짜만 도구로 전송하고, 파일/이미지 내용은 사용자가 직접 첨부한 것만 전송한다.
    /// 변경 작업은 계획으로만 쌓고 사용자가 체크·승인한 것만 기존 파일 작업 엔진으로 실행한다(삭제는 휴지통, Ctrl+Z 지원).
    /// </summary>
    public partial class MainWindow
    {
        private const string AsstEnabledKey = "AssistantEnabled";
        private const string AsstProviderKey = "AssistantProvider";
        private const string AsstModelKey = "AssistantModel";
        private const string AsstBaseUrlKey = "AssistantBaseUrl";
        private const string AsstSkillsEnabledKey = "AssistantSkillsEnabled";
        private const double AsstPanelWidth = 420;

        private static readonly HttpClient AssistantHttp = new() { Timeout = TimeSpan.FromSeconds(300) };

        private AssistantAgent? _asstAgent;
        private CancellationTokenSource? _asstCts;
        private Storyboard? _asstStoryboard;
        private bool _asstOpen;
        private bool _asstBusy;
        private bool _asstUiLoading;
        private bool _asstInitialized;
        private FrameworkElement? _asstThinkingRow;
        private TextBlock? _asstThinkingText;
        private AssistantSkillStore? _asstSkillStore;
        private readonly List<LlmAttachment> _asstAttachments = new();

        private SettingsService AsstSettings => App.Current.Services.GetRequiredService<SettingsService>();
        private bool AsstKo => _loc.Language.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
        private string AsT(string en, string ko) => AsstKo ? ko : en;

        private AssistantSkillStore AsstSkills => _asstSkillStore ??= new AssistantSkillStore(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WON", "assistant-skills"));

        // ═══════════════════════════════════════════════════════════════
        //  Open / close (slide animation)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>단축키/명령 팔레트 진입점.</summary>
        private Task ShowAssistantDialogAsync()
        {
            ToggleAssistantPanel();
            return Task.CompletedTask;
        }

        private void OnAssistantToolbarClick(object sender, RoutedEventArgs e) => ToggleAssistantPanel();
        private void OnAssistantCloseClick(object sender, RoutedEventArgs e) => CloseAssistantPanel();

        private void ToggleAssistantPanel()
        {
            if (_asstOpen) CloseAssistantPanel(); else OpenAssistantPanel();
        }

        private void OpenAssistantPanel()
        {
            if (_asstOpen) return;
            _asstOpen = true;
            EnsureAssistantInitialized();
            RefreshAssistantChrome();
            RefreshAssistantSettingsUi();
            AssistantSettingsArea.Visibility = IsAssistantReady(out _) ? Visibility.Collapsed : Visibility.Visible;
            AssistantPanel.Visibility = Visibility.Visible;
            AnimateAssistantPanel(open: true);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => AssistantInput.Focus(FocusState.Programmatic));
        }

        private void CloseAssistantPanel()
        {
            if (!_asstOpen) return;
            _asstOpen = false;
            AnimateAssistantPanel(open: false);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusActiveView);
        }

        private void AnimateAssistantPanel(bool open)
        {
            double from = AssistantPanel.ActualWidth;
            _asstStoryboard?.Stop();
            AssistantPanel.Width = from;

            double target = open ? AsstPanelWidth : 0;
            var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
            var duration = new Duration(TimeSpan.FromMilliseconds(open ? 260 : 200));

            var width = new DoubleAnimation { From = from, To = target, Duration = duration, EasingFunction = ease, EnableDependentAnimation = true };
            Storyboard.SetTarget(width, AssistantPanel);
            Storyboard.SetTargetProperty(width, "Width");

            var shift = new DoubleAnimation { From = open ? 56 : 0, To = open ? 0 : 56, Duration = duration, EasingFunction = ease };
            Storyboard.SetTarget(shift, AssistantPanelShift);
            Storyboard.SetTargetProperty(shift, "X");

            var fade = new DoubleAnimation { From = open ? 0 : 1, To = open ? 1 : 0, Duration = duration, EasingFunction = ease };
            Storyboard.SetTarget(fade, AssistantPanelInner);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var sb = new Storyboard();
            sb.Children.Add(width); sb.Children.Add(shift); sb.Children.Add(fade);
            sb.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_asstStoryboard, sb)) return;
                sb.Stop();
                AssistantPanel.Width = target;
                AssistantPanelShift.X = 0;
                AssistantPanelInner.Opacity = 1;
                if (!open) AssistantPanel.Visibility = Visibility.Collapsed;
            };
            _asstStoryboard = sb;
            sb.Begin();
        }

        private void OnAssistantPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 너비 애니메이션 중 내부(고정 폭) 콘텐츠가 밖으로 삐져나오지 않게 자른다.
            AssistantPanel.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        }

        // ═══════════════════════════════════════════════════════════════
        //  Chrome (localized texts, model / skills labels)
        // ═══════════════════════════════════════════════════════════════

        private void EnsureAssistantInitialized()
        {
            if (_asstInitialized) return;
            _asstInitialized = true;

            var modelFlyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight };
            modelFlyout.Opening += (_, _) => RebuildAssistantModelMenu(modelFlyout);
            AssistantModelButton.Flyout = modelFlyout;

            ShowAssistantEmptyState();
        }

        private void ApplyAssistantTexts()
        {
            ToolTipService.SetToolTip(AssistantToolbarButton, AsT("AI Assistant (Ctrl+Shift+K)", "AI 어시스턴트 (Ctrl+Shift+K)"));
            ToolTipService.SetToolTip(AssistantNewChatButton, AsT("New chat", "새 대화"));
            ToolTipService.SetToolTip(AssistantSettingsButton, AsT("Assistant settings", "어시스턴트 설정"));
            ToolTipService.SetToolTip(AssistantCloseButton, AsT("Close", "닫기"));
            ToolTipService.SetToolTip(AssistantAttachButton, AsT("Attach file or image", "파일/이미지 첨부"));
            ToolTipService.SetToolTip(AssistantSkillsButton, AsT("Skills", "스킬"));
            ToolTipService.SetToolTip(AssistantSendButton, AsT("Send (Enter)", "보내기 (Enter)"));
            AssistantAttachFileItem.Text = AsT("Attach file…", "파일 첨부…");
            AssistantAttachImageItem.Text = AsT("Attach image…", "이미지 첨부…");
            AssistantInput.PlaceholderText = AsT("Ask about this folder…", "이 폴더에 대해 물어보세요…");
            AssistantPrivacyText.Text = AsT(
                "When enabled, the names, paths, sizes and dates of folders/files you ask about are sent to the selected provider. " +
                "File and image CONTENTS are sent only for files you attach yourself. Changes are made only after you approve a plan.",
                "사용하면 질문과 관련된 폴더/파일의 이름·경로·크기·날짜가 선택한 서비스로 전송됩니다. " +
                "파일/이미지의 내용은 직접 첨부한 것만 전송됩니다. 변경은 계획을 승인한 뒤에만 실행됩니다.");
            AssistantEnableToggle.Header = AsT("Enable assistant", "어시스턴트 사용");
            AssistantKeySaveButton.Content = AsT("Save key", "키 저장");
            AssistantKeyClearButton.Content = AsT("Remove key", "키 삭제");
        }

        private string AsstProvider()
        {
            var p = AsstSettings.Get(AsstProviderKey, "Claude");
            return p is "OpenAI" or "Local" ? p : "Claude";
        }

        private string AsstModel(string provider)
        {
            var m = AsstSettings.Get(AsstModelKey + "." + provider, "");
            return string.IsNullOrWhiteSpace(m) ? AssistantModelCatalog.DefaultModel(provider) : m;
        }

        private string AsstBaseUrl(string provider) => AsstSettings.Get(AsstBaseUrlKey + "." + provider, "");
        private static string AsstDefaultBase(string provider) => provider switch
        {
            "OpenAI" => OpenAiClient.DefaultBase,
            "Local" => "http://localhost:11434",
            _ => ClaudeClient.DefaultBase,
        };

        private string? AsstCurrentFolder()
        {
            var f = ViewModel.ActiveExplorer?.CurrentPath;
            return !string.IsNullOrEmpty(f) && System.IO.Directory.Exists(f) ? f : null;
        }

        private HashSet<string> AsstEnabledSkillIds() =>
            new(AsstSettings.Get(AsstSkillsEnabledKey, "").Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        private void RefreshAssistantChrome()
        {
            ApplyAssistantTexts();
            var p = AsstProvider();
            var model = AsstModel(p);
            AssistantModelText.Text = AssistantModelCatalog.LabelFor(p, model);
            ToolTipService.SetToolTip(AssistantModelButton, $"{AssistantModelCatalog.DisplayName(p)} · {model}");

            int enabledSkills = AsstSkills.List().Count(s => AsstEnabledSkillIds().Contains(s.Id));
            AssistantSkillsText.Text = AsT("Skills", "스킬") + (enabledSkills > 0 ? $" · {enabledSkills}" : "");

            var folder = AsstCurrentFolder();
            AssistantSubtitle.Text = folder ?? AsT("No folder open", "열려 있는 폴더 없음");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Settings area (enable / API key / base URL)
        // ═══════════════════════════════════════════════════════════════

        private bool IsAssistantReady(out string reason)
        {
            reason = string.Empty;
            var p = AsstProvider();
            if (!AsstSettings.Get(AsstEnabledKey, false))
            {
                reason = AsT("The assistant is turned off. Turn it on below.", "어시스턴트가 꺼져 있습니다. 아래에서 켜 주세요.");
                return false;
            }
            if (p != "Local" && AssistantCredentialStore.Load(p) == null)
            {
                reason = AsT($"No {p} API key is saved yet.", $"{p} API 키가 아직 저장되지 않았습니다.");
                return false;
            }
            return true;
        }

        private void RefreshAssistantSettingsUi()
        {
            _asstUiLoading = true;
            try
            {
                var p = AsstProvider();
                bool hasKey = AssistantCredentialStore.Load(p) != null;
                AssistantEnableToggle.IsOn = AsstSettings.Get(AsstEnabledKey, false);
                bool isLocal = p == "Local";
                AssistantKeyBox.Visibility = AssistantKeySaveButton.Visibility = AssistantKeyClearButton.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
                AssistantKeyBox.Header = AsT($"{p} API key", $"{p} API 키");
                AssistantKeyBox.PlaceholderText = hasKey ? AsT("(saved — paste a new key to replace)", "(저장됨 — 바꾸려면 새 키를 붙여넣기)") : "sk-…";
                AssistantKeyBox.Password = string.Empty;
                AssistantKeyClearButton.IsEnabled = hasKey;
                AssistantBaseUrlBox.Header = isLocal
                    ? AsT("Local server URL (this PC only)", "로컬 서버 주소 (이 PC만 가능)")
                    : AsT("Base URL (optional, https only)", "Base URL (선택, https만)");
                AssistantBaseUrlBox.PlaceholderText = AsstDefaultBase(p);
                AssistantBaseUrlBox.Text = AsstBaseUrl(p);
                AssistantSettingsStatus.Text = isLocal
                    ? AsT("Local model: no API key needed. Run Ollama on this PC; nothing leaves it.", "로컬 모델: API 키가 필요 없습니다. 이 PC에서 Ollama를 실행하며 내용이 PC 밖으로 나가지 않습니다.")
                    : string.Empty;
            }
            finally { _asstUiLoading = false; }
        }

        private void OnAssistantSettingsClick(object sender, RoutedEventArgs e)
        {
            var show = AssistantSettingsArea.Visibility != Visibility.Visible;
            if (show) RefreshAssistantSettingsUi();
            AssistantSettingsArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnAssistantEnableToggled(object sender, RoutedEventArgs e)
        {
            if (_asstUiLoading) return;
            AsstSettings.Set(AsstEnabledKey, AssistantEnableToggle.IsOn);
        }

        private void OnAssistantKeySaveClick(object sender, RoutedEventArgs e)
        {
            var p = AsstProvider();
            var key = AssistantKeyBox.Password.Trim();
            if (key.Length == 0) { AssistantSettingsStatus.Text = AsT("Paste an API key first.", "API 키를 먼저 붙여넣으세요."); return; }
            if (!AssistantCredentialStore.Save(p, key))
            {
                AssistantSettingsStatus.Text = AsT("Could not store the API key securely.", "API 키를 안전하게 저장하지 못했습니다.");
                return;
            }
            RefreshAssistantSettingsUi();
            if (IsAssistantReady(out _))
            {
                AssistantSettingsArea.Visibility = Visibility.Collapsed;
                ViewModel.ShowToast(AsT("API key saved to Windows Credential Manager.", "API 키를 Windows 자격 증명에 저장했습니다."), 2500);
            }
            else
                AssistantSettingsStatus.Text = AsT("Saved. Turn the assistant on above to start.", "저장했습니다. 위에서 어시스턴트를 켜면 시작할 수 있습니다.");
        }

        private void OnAssistantKeyClearClick(object sender, RoutedEventArgs e)
        {
            AssistantCredentialStore.Delete(AsstProvider());
            RefreshAssistantSettingsUi();
            AssistantSettingsStatus.Text = AsT("Key removed.", "키를 삭제했습니다.");
        }

        private void OnAssistantBaseUrlLostFocus(object sender, RoutedEventArgs e)
        {
            if (_asstUiLoading) return;
            var p = AsstProvider();
            var text = AssistantBaseUrlBox.Text.Trim();
            if (text.Length > 0 && !LlmEndpoint.TryResolveBase(text, AsstDefaultBase(p), out _, out var err))
            {
                AssistantSettingsStatus.Text = err;
                return;
            }
            AsstSettings.Set(AsstBaseUrlKey + "." + p, text);
            AssistantSettingsStatus.Text = string.Empty;
        }

        // ═══════════════════════════════════════════════════════════════
        //  Model picker
        // ═══════════════════════════════════════════════════════════════

        private void RebuildAssistantModelMenu(MenuFlyout flyout)
        {
            flyout.Items.Clear();
            var current = AsstProvider();
            var currentModel = AsstModel(current);
            foreach (var provider in AssistantModelCatalog.Providers)
            {
                var sub = new MenuFlyoutSubItem { Text = AssistantModelCatalog.DisplayName(provider) };
                bool known = false;
                foreach (var (id, label) in AssistantModelCatalog.For(provider))
                {
                    bool isCurrent = provider == current && id == currentModel;
                    known |= isCurrent;
                    var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = isCurrent };
                    var (p, m) = (provider, id);
                    item.Click += (_, _) => SetAssistantModel(p, m);
                    sub.Items.Add(item);
                }
                if (provider == current && !known)
                    sub.Items.Add(new ToggleMenuFlyoutItem { Text = currentModel, IsChecked = true });
                sub.Items.Add(new MenuFlyoutSeparator());
                var custom = new MenuFlyoutItem { Text = AsT("Custom model…", "직접 입력…") };
                var prov = provider;
                custom.Click += async (_, _) => await PromptCustomModelAsync(prov);
                sub.Items.Add(custom);
                flyout.Items.Add(sub);
            }
        }

        private void SetAssistantModel(string provider, string model)
        {
            AsstSettings.Set(AsstProviderKey, provider);
            AsstSettings.Set(AsstModelKey + "." + provider, model);
            RefreshAssistantChrome();
            if (_asstOpen && AssistantSettingsArea.Visibility == Visibility.Visible) RefreshAssistantSettingsUi();
        }

        private async Task PromptCustomModelAsync(string provider)
        {
            var box = new TextBox { Header = AsT("Model ID", "모델 ID"), Text = AsstModel(provider), MinWidth = 320 };
            var dialog = new ContentDialog
            {
                Title = AsT($"{provider} model", $"{provider} 모델"),
                Content = box,
                PrimaryButtonText = _loc.Get("Save"),
                CloseButtonText = _loc.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };
            if (await ShowContentDialogSafeAsync(dialog) != ContentDialogResult.Primary) return;
            var id = box.Text.Trim();
            if (id.Length == 0 || id.Length > 100 || id.Any(char.IsWhiteSpace)) return;
            SetAssistantModel(provider, id);
        }

        // ═══════════════════════════════════════════════════════════════
        //  Conversation
        // ═══════════════════════════════════════════════════════════════

        private const string XamlNs = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

        private static T AsXaml<T>(string element) where T : class
        {
            int space = element.IndexOf(' ');
            return (T)XamlReader.Load(element.Insert(space, " " + XamlNs));
        }

        private void ShowAssistantEmptyState()
        {
            AssistantMessages.Children.Clear();
            var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 40, 0, 0), Tag = "empty" };
            panel.Children.Add(new TextBlock
            {
                Text = AsT("How can I help?", "무엇을 도와드릴까요?"),
                FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = AsT("I can explore folders and propose organising changes. Nothing is changed until you approve a plan.",
                           "폴더를 살펴보고 정리 계획을 제안할 수 있어요. 계획을 승인하기 전에는 아무것도 바뀌지 않습니다."),
                FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap
            });

            var suggestions = new[]
            {
                AsT("Find the 10 largest files in this folder", "이 폴더에서 가장 큰 파일 10개 찾아줘"),
                AsT("Plan how to sort these files into folders by type", "이 파일들을 종류별 폴더로 정리하는 계획을 세워줘"),
                AsT("Find *.log files older than 90 days", "90일 넘은 *.log 파일을 찾아줘"),
            };
            foreach (var s in suggestions)
            {
                var text = s;
                var btn = new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
                btn.Click += (_, _) => { AssistantInput.Text = text; AssistantInput.Focus(FocusState.Programmatic); AssistantInput.SelectionStart = text.Length; };
                panel.Children.Add(btn);
            }
            AssistantMessages.Children.Add(panel);
        }

        private void RemoveAssistantEmptyState()
        {
            for (int i = AssistantMessages.Children.Count - 1; i >= 0; i--)
                if (AssistantMessages.Children[i] is FrameworkElement fe && Equals(fe.Tag, "empty"))
                    AssistantMessages.Children.RemoveAt(i);
        }

        private void ScrollAssistantToEnd()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                AssistantScroll.UpdateLayout();
                AssistantScroll.ChangeView(null, AssistantScroll.ScrollableHeight, null, false);
            });
        }

        /// <summary>새 메시지를 아래에서 살짝 떠오르며 나타나게 한다.</summary>
        private void AddAssistantRow(FrameworkElement row)
        {
            row.Opacity = 0;
            var shift = new TranslateTransform { Y = 10 };
            row.RenderTransform = shift;
            AssistantMessages.Children.Add(row);

            var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(220)) };
            Storyboard.SetTarget(fade, row); Storyboard.SetTargetProperty(fade, "Opacity");
            var slide = new DoubleAnimation { From = 10, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(220)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(slide, shift); Storyboard.SetTargetProperty(slide, "Y");
            var sb = new Storyboard();
            sb.Children.Add(fade); sb.Children.Add(slide);
            sb.Completed += (_, _) => { row.Opacity = 1; shift.Y = 0; };
            sb.Begin();
            ScrollAssistantToEnd();
        }

        private void AddUserMessage(string text, IReadOnlyList<LlmAttachment> attachments)
        {
            var bubble = AsXaml<Border>("<Border HorizontalAlignment='Right' MaxWidth='340' CornerRadius='14' Padding='12,8' " +
                                        "Background='{ThemeResource SpanBgLayer2Brush}' BorderBrush='{ThemeResource SpanBorderSubtleBrush}' BorderThickness='1'/>");
            var stack = new StackPanel { Spacing = 6 };
            if (attachments.Count > 0)
            {
                var wrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var a in attachments) wrap.Children.Add(BuildAttachmentChip(a, removable: false));
                stack.Children.Add(new ScrollViewer { Content = wrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            }
            stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            bubble.Child = stack;
            AddAssistantRow(bubble);
        }

        private void AddAssistantMessage(string text, bool hitLimit)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = "WON", FontSize = 11, Opacity = 0.55, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(BuildMarkdown(string.IsNullOrWhiteSpace(text) ? AsT("(no response)", "(응답 없음)") : text));
            if (hitLimit)
                stack.Children.Add(new TextBlock { Text = AsT("Stopped at the step limit.", "단계 제한에 도달해 중단했습니다."), FontSize = 11, Opacity = 0.6 });
            AddAssistantRow(stack);
        }

        private void AddAssistantError(string text)
        {
            var box = AsXaml<Border>("<Border CornerRadius='10' Padding='12,8' Background='{ThemeResource SystemFillColorCriticalBackgroundBrush}'/>");
            box.Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
            AddAssistantRow(box);
        }

        private void AddAssistantNote(string text)
        {
            AddAssistantRow(new TextBlock { Text = text, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        private void ShowThinking()
        {
            _asstThinkingText = new TextBlock { Text = AsT("Thinking…", "생각 중…"), FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
            row.Children.Add(_asstThinkingText);
            _asstThinkingRow = row;
            AddAssistantRow(row);
        }

        private void HideThinking()
        {
            if (_asstThinkingRow != null) AssistantMessages.Children.Remove(_asstThinkingRow);
            _asstThinkingRow = null; _asstThinkingText = null;
        }

        private string AsstToolLabel(string tool) => tool switch
        {
            "list_directory" => AsT("Looking at the folder…", "폴더를 살펴보는 중…"),
            "search_files" => AsT("Searching…", "검색하는 중…"),
            "get_file_info" => AsT("Checking file info…", "파일 정보를 확인하는 중…"),
            _ when tool.StartsWith("propose_", StringComparison.Ordinal) => AsT("Preparing a plan…", "변경 계획을 만드는 중…"),
            _ => AsT("Working…", "작업 중…")
        };

        private void SetAssistantBusy(bool busy)
        {
            _asstBusy = busy;
            AssistantSendIcon.Glyph = busy ? "" : "";
            ToolTipService.SetToolTip(AssistantSendButton, busy ? AsT("Stop", "중지") : AsT("Send (Enter)", "보내기 (Enter)"));
        }

        private void OnAssistantSendClick(object sender, RoutedEventArgs e)
        {
            if (_asstBusy) { _asstCts?.Cancel(); return; }
            SendAssistantMessage();
        }

        private void OnAssistantNewChatClick(object sender, RoutedEventArgs e)
        {
            _asstCts?.Cancel();
            _asstAgent?.ResetConversation();
            HideThinking();
            ShowAssistantEmptyState();
            AssistantInput.Focus(FocusState.Programmatic);
        }

        private void OnAssistantInputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) return; // 줄바꿈
            e.Handled = true;
            // IME 조합 중인 마지막 글자가 입력에 반영된 뒤에 전송한다.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (!_asstBusy) SendAssistantMessage(); });
        }

        private bool TryCreateAssistantClient(out ILlmClient? client, out string error)
        {
            client = null; error = string.Empty;
            if (!IsAssistantReady(out error)) return false;
            var p = AsstProvider();
            var key = p == "Local" ? string.Empty : AssistantCredentialStore.Load(p)!;
            if (!LlmEndpoint.TryResolveBase(AsstBaseUrl(p), AsstDefaultBase(p), out var baseUri, out var urlErr)) { error = urlErr; return false; }
            if (p == "Local" && !baseUri.IsLoopback)
            {
                error = AsT("The local model address must be on this PC (localhost).", "로컬 모델 주소는 이 PC(localhost)여야 합니다.");
                return false;
            }
            var model = AsstModel(p);
            client = p is "OpenAI" or "Local"
                ? new OpenAiClient(AssistantHttp, key, model, baseUri)
                : new ClaudeClient(AssistantHttp, key, model, baseUri);
            return true;
        }

        private async void SendAssistantMessage()
        {
            if (_asstBusy) return;
            var text = AssistantInput.Text.Trim();
            if (text.Length == 0 && _asstAttachments.Count == 0) return;
            if (text.Length == 0) text = AsT("Please take a look at the attached file(s).", "첨부한 파일을 살펴봐 주세요.");

            if (!TryCreateAssistantClient(out var client, out var why) || client == null)
            {
                RefreshAssistantSettingsUi();
                AssistantSettingsArea.Visibility = Visibility.Visible;
                AssistantSettingsStatus.Text = why;
                return;
            }

            var attachments = _asstAttachments.ToList();
            AssistantInput.Text = string.Empty;
            _asstAttachments.Clear();
            RefreshAssistantChips();

            RemoveAssistantEmptyState();
            AddUserMessage(text, attachments);
            ShowThinking();
            SetAssistantBusy(true);

            var folder = AsstCurrentFolder();
            AssistantSubtitle.Text = folder ?? AsT("No folder open", "열려 있는 폴더 없음");
            _asstAgent ??= new AssistantAgent(client, folder);
            _asstAgent.Client = client;
            _asstAgent.CurrentFolder = folder;
            var enabled = AsstEnabledSkillIds();
            _asstAgent.SkillInstructions = AssistantSkillPrompt.Build(AsstSkills.List().Where(s => enabled.Contains(s.Id)));

            _asstCts = new CancellationTokenSource();
            var token = _asstCts.Token;
            try
            {
                var result = await _asstAgent.RunAsync(text, token,
                    tool => DispatcherQueue.TryEnqueue(() => { if (_asstThinkingText != null) _asstThinkingText.Text = AsstToolLabel(tool); }),
                    attachments);
                HideThinking();
                AddAssistantMessage(result.Text, result.HitTurnLimit);
                if (result.Plan.Count > 0) AddPlanCard(result.Plan);
            }
            catch (OperationCanceledException)
            {
                HideThinking();
                AddAssistantNote(AsT("Stopped.", "중지했습니다."));
                RestoreComposer(text, attachments);
            }
            catch (LlmException ex)
            {
                HideThinking();
                AddAssistantError(AsT("Error: ", "오류: ") + ex.Message);
                RestoreComposer(text, attachments);
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[Assistant] unexpected error: {ex.GetType().Name}");
                HideThinking();
                AddAssistantError(AsT("Unexpected error: ", "예상치 못한 오류: ") + ex.Message);
                RestoreComposer(text, attachments);
            }
            finally
            {
                SetAssistantBusy(false);
                _asstCts?.Dispose(); _asstCts = null;
            }
        }

        /// <summary>실패/중지한 경우 입력과 첨부를 되돌려 놓아 바로 다시 보낼 수 있게 한다.</summary>
        private void RestoreComposer(string text, IReadOnlyList<LlmAttachment> attachments)
        {
            if (AssistantInput.Text.Length == 0) AssistantInput.Text = text;
            foreach (var a in attachments)
                if (_asstAttachments.Count < AssistantAttachmentLoader.MaxCount) _asstAttachments.Add(a);
            RefreshAssistantChips();
        }

        // ═══════════════════════════════════════════════════════════════
        //  Plan card (approve → run)
        // ═══════════════════════════════════════════════════════════════

        private void AddPlanCard(IReadOnlyList<PlannedOperation> plan)
        {
            var card = AsXaml<Border>("<Border CornerRadius='10' Padding='12' Background='{ThemeResource SpanBgLayer2Brush}' " +
                                      "BorderBrush='{ThemeResource SpanBorderSubtleBrush}' BorderThickness='1'/>");
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                Text = AsT($"Proposed changes ({plan.Count}) — check what to run", $"제안된 변경 ({plan.Count}개) — 실행할 항목을 체크하세요"),
                FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap
            });

            var checks = new List<CheckBox>();
            var list = new StackPanel { Spacing = 0 };
            foreach (var op in plan)
            {
                var cb = new CheckBox { IsChecked = true, MinHeight = 28, Content = new TextBlock { Text = op.Describe(), TextWrapping = TextWrapping.Wrap, FontSize = 12 } };
                checks.Add(cb); list.Children.Add(cb);
            }
            stack.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var status = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
            var run = new Button { Content = AsT("Run checked", "체크한 항목 실행"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var discard = new Button { Content = AsT("Discard", "버리기") };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { run, discard } };
            stack.Children.Add(buttons);
            stack.Children.Add(status);

            discard.Click += (_, _) =>
            {
                list.Children.Clear(); buttons.Visibility = Visibility.Collapsed;
                status.Text = AsT("Plan discarded.", "계획을 버렸습니다.");
            };
            run.Click += async (_, _) =>
            {
                var chosen = plan.Where((_, i) => checks[i].IsChecked == true).ToList();
                if (chosen.Count == 0) return;
                run.IsEnabled = discard.IsEnabled = false;
                foreach (var c in checks) c.IsEnabled = false;
                int done = 0;
                try
                {
                    foreach (var op in chosen)
                    {
                        status.Text = $"{AsT("Running", "실행 중")} {done + 1}/{chosen.Count}: {op.Describe()}";
                        await ViewModel.ExecuteFileOperationAsync(ToOperation(op));
                        done++;
                    }
                    status.Text = string.Format(AsT("Done: {0} operation(s). Press Ctrl+Z to undo.", "완료: {0}개 작업. Ctrl+Z 로 되돌릴 수 있습니다."), done);
                    buttons.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    Helpers.DebugLogger.Log($"[Assistant] plan run error: {ex.GetType().Name}");
                    status.Text = string.Format(AsT("Stopped after {0} operation(s): ", "{0}개 실행 후 중단됨: "), done) + ex.Message;
                    run.IsEnabled = discard.IsEnabled = true;
                    foreach (var c in checks) c.IsEnabled = true;
                }
            };

            card.Child = stack;
            AddAssistantRow(card);
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

        // ═══════════════════════════════════════════════════════════════
        //  Markdown (headings, bullets, code blocks, **bold**, `code`)
        // ═══════════════════════════════════════════════════════════════

        private FrameworkElement BuildMarkdown(string text)
        {
            var root = new StackPanel { Spacing = 6 };
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var para = new List<string>();

            void FlushPara()
            {
                if (para.Count == 0) return;
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, LineHeight = 21 };
                AddMarkdownInlines(tb.Inlines, string.Join("\n", para));
                root.Children.Add(tb);
                para.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushPara();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i++]);
                    var block = AsXaml<Border>("<Border CornerRadius='8' Padding='10' Background='{ThemeResource SpanBgLayer2Brush}' " +
                                               "BorderBrush='{ThemeResource SpanBorderSubtleBrush}' BorderThickness='1'/>");
                    block.Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = new TextBlock { Text = string.Join("\n", code), FontFamily = new FontFamily("Consolas"), FontSize = 12, IsTextSelectionEnabled = true }
                    };
                    root.Children.Add(block);
                    continue;
                }

                if (trimmed.Length == 0) { FlushPara(); continue; }

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    int level = 0; while (level < trimmed.Length && trimmed[level] == '#') level++;
                    if (level <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                    {
                        FlushPara();
                        var h = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontWeight = FontWeights.SemiBold, FontSize = level <= 1 ? 17 : level == 2 ? 15.5 : 14 };
                        AddMarkdownInlines(h.Inlines, trimmed.Substring(level + 1));
                        root.Children.Add(h);
                        continue;
                    }
                }

                string? bullet = null; string rest = trimmed;
                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("• ", StringComparison.Ordinal))
                { bullet = "•"; rest = trimmed.Substring(2); }
                else
                {
                    int d = 0; while (d < trimmed.Length && char.IsDigit(trimmed[d])) d++;
                    if (d > 0 && d < 4 && d + 1 < trimmed.Length && trimmed[d] == '.' && trimmed[d + 1] == ' ')
                    { bullet = trimmed.Substring(0, d + 1); rest = trimmed.Substring(d + 2); }
                }
                if (bullet != null)
                {
                    FlushPara();
                    int indent = (line.Length - trimmed.Length) / 2;
                    var row = new Grid { Margin = new Thickness(6 + indent * 14, 0, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(new TextBlock { Text = bullet, Opacity = 0.7 });
                    var body = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, LineHeight = 21 };
                    Grid.SetColumn(body, 1);
                    AddMarkdownInlines(body.Inlines, rest);
                    row.Children.Add(body);
                    root.Children.Add(row);
                    continue;
                }

                para.Add(line.TrimEnd());
            }
            FlushPara();
            return root;
        }

        private static void AddMarkdownInlines(InlineCollection inlines, string text)
        {
            var sb = new StringBuilder();
            void Flush() { if (sb.Length > 0) { inlines.Add(new Run { Text = sb.ToString() }); sb.Clear(); } }

            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '`')
                {
                    int j = text.IndexOf('`', i + 1);
                    if (j > i + 1)
                    {
                        Flush();
                        inlines.Add(new Run { Text = text.Substring(i + 1, j - i - 1), FontFamily = new FontFamily("Consolas"), FontSize = 12.5 });
                        i = j + 1; continue;
                    }
                }
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    int j = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (j > i + 2)
                    {
                        Flush();
                        inlines.Add(new Run { Text = text.Substring(i + 2, j - i - 2), FontWeight = FontWeights.SemiBold });
                        i = j + 2; continue;
                    }
                }
                sb.Append(text[i]); i++;
            }
            Flush();
        }

        // ═══════════════════════════════════════════════════════════════
        //  Attachments (files / images / paste / drag & drop)
        // ═══════════════════════════════════════════════════════════════

        private void AddAssistantAttachment(string path)
        {
            if (!AssistantAttachmentLoader.TryLoad(path, out var att, out var err) || att == null)
            {
                ShowAttachNote($"{System.IO.Path.GetFileName(path)}: {err}", isError: true);
                return;
            }
            AddAssistantAttachment(att);
        }

        private void AddAssistantAttachment(LlmAttachment att)
        {
            if (!AssistantAttachmentLoader.CanAdd(_asstAttachments, att, out var err))
            {
                ShowAttachNote(err, isError: true);
                return;
            }
            _asstAttachments.Add(att);
            RefreshAssistantChips();
        }

        private void ShowAttachNote(string text, bool isError)
        {
            AssistantAttachNote.Text = text;
            AssistantAttachNote.Foreground = isError
                ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            AssistantAttachNote.Visibility = Visibility.Visible;
        }

        private void RefreshAssistantChips()
        {
            AssistantChips.Children.Clear();
            foreach (var a in _asstAttachments)
            {
                var att = a;
                AssistantChips.Children.Add(BuildAttachmentChip(att, removable: true, onRemove: () =>
                {
                    _asstAttachments.Remove(att);
                    RefreshAssistantChips();
                }));
            }
            AssistantChipsScroll.Visibility = _asstAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_asstAttachments.Count > 0)
                ShowAttachNote(AsstProvider() == "Local"
                    ? AsT("Attached files stay on this PC (local model).", "첨부한 파일은 이 PC 안의 로컬 모델에만 전달됩니다.")
                    : AsT($"The contents of attached files are sent to {AsstProvider()}.", $"첨부한 파일의 내용이 {AsstProvider()}(으)로 전송됩니다."), isError: false);
            else
                AssistantAttachNote.Visibility = Visibility.Collapsed;
        }

        private FrameworkElement BuildAttachmentChip(LlmAttachment a, bool removable, Action? onRemove = null)
        {
            var chip = AsXaml<Border>("<Border CornerRadius='8' Padding='6,4' Background='{ThemeResource SpanBgLayer1Brush}' " +
                                      "BorderBrush='{ThemeResource SpanBorderSubtleBrush}' BorderThickness='1'/>");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (a.Kind == LlmAttachmentKind.Image)
            {
                var img = new Image { Width = 26, Height = 26, Stretch = Stretch.UniformToFill };
                LoadThumbnail(img, a.Data);
                row.Children.Add(img);
            }
            else row.Children.Add(new FontIcon { Glyph = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = a.Name, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            if (removable)
            {
                var x = new Button
                {
                    Content = new FontIcon { Glyph = "", FontSize = 9 },
                    Padding = new Thickness(4), MinWidth = 0, MinHeight = 0,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0)
                };
                x.Click += (_, _) => onRemove?.Invoke();
                row.Children.Add(x);
            }
            chip.Child = row;
            return chip;
        }

        private static async void LoadThumbnail(Image img, byte[] data)
        {
            try
            {
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(data.AsBuffer());
                ms.Seek(0);
                var bmp = new BitmapImage { DecodePixelWidth = 52 };
                await bmp.SetSourceAsync(ms);
                img.Source = bmp;
            }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[Assistant] thumbnail failed: {ex.GetType().Name}"); }
        }

        private async void OnAssistantAttachFileClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                picker.FileTypeFilter.Add("*");
                var files = await picker.PickMultipleFilesAsync();
                if (files == null) return;
                foreach (var f in files) AddAssistantAttachment(f.Path);
            }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[Assistant] attach file failed: {ex.GetType().Name}"); }
        }

        private async void OnAssistantAttachImageClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail };
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }) picker.FileTypeFilter.Add(ext);
                var files = await picker.PickMultipleFilesAsync();
                if (files == null) return;
                foreach (var f in files) AddAssistantAttachment(f.Path);
            }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[Assistant] attach image failed: {ex.GetType().Name}"); }
        }

        private void OnAssistantDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = AsT("Attach to message", "메시지에 첨부");
            }
        }

        private async void OnAssistantDrop(object sender, DragEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile f) AddAssistantAttachment(f.Path);
                    else ShowAttachNote(AsT("Folders cannot be attached — ask the assistant to look inside instead.", "폴더는 첨부할 수 없습니다. 어시스턴트에게 폴더를 살펴보라고 요청하세요."), isError: true);
                }
            }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[Assistant] drop failed: {ex.GetType().Name}"); }
            finally { deferral.Complete(); }
        }

        private async void OnAssistantInputPaste(object sender, TextControlPasteEventArgs e)
        {
            try
            {
                var view = Clipboard.GetContent();
                if (view.Contains(StandardDataFormats.Text)) return; // 일반 텍스트 붙여넣기는 그대로 둔다

                if (view.Contains(StandardDataFormats.Bitmap))
                {
                    e.Handled = true;
                    var reference = await view.GetBitmapAsync();
                    using var stream = await reference.OpenReadAsync();
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    using var mem = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                    var bytes = new byte[mem.Size];
                    using (var reader = new DataReader(mem.GetInputStreamAt(0)))
                    {
                        await reader.LoadAsync((uint)mem.Size);
                        reader.ReadBytes(bytes);
                    }
                    if (bytes.Length > AssistantAttachmentLoader.MaxImageBytes)
                        ShowAttachNote(AsT("The pasted image is larger than 5 MB.", "붙여넣은 이미지가 5MB보다 큽니다."), isError: true);
                    else
                        AddAssistantAttachment(AssistantAttachmentLoader.FromPngBytes($"pasted-{DateTime.Now:HHmmss}.png", bytes));
                }
                else if (view.Contains(StandardDataFormats.StorageItems))
                {
                    e.Handled = true;
                    foreach (var item in await view.GetStorageItemsAsync())
                        if (item is Windows.Storage.StorageFile f) AddAssistantAttachment(f.Path);
                }
            }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[Assistant] paste failed: {ex.GetType().Name}"); }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Skills (instruction presets; Claude SKILL.md import)
        // ═══════════════════════════════════════════════════════════════

        private enum SkillAction { None, ImportFile, ImportFolder, Create }

        private async void OnAssistantSkillsClick(object sender, RoutedEventArgs e)
        {
            try { await ShowSkillsDialogAsync(); }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[Assistant] skills dialog failed: {ex.GetType().Name}"); }
            RefreshAssistantChrome();
        }

        private async Task ShowSkillsDialogAsync()
        {
            while (true)
            {
                var action = SkillAction.None;
                var skills = AsstSkills.List();
                var enabled = AsstEnabledSkillIds();

                var content = new StackPanel { Spacing = 10, Width = 440 };
                content.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75,
                    Text = AsT("Skills are extra instructions the assistant follows. Import a Claude skill (SKILL.md) or write your own. " +
                               "Only the text is used — scripts inside skill folders are never imported or run, and skills cannot grant new permissions.",
                               "스킬은 어시스턴트가 따르는 추가 지침입니다. Claude 스킬(SKILL.md)을 가져오거나 직접 만들 수 있습니다. " +
                               "텍스트만 사용하며, 스킬 폴더 안의 스크립트는 가져오거나 실행하지 않고, 스킬로 새 권한을 줄 수 없습니다.")
                });

                var list = new StackPanel { Spacing = 4 };
                if (skills.Count == 0)
                    list.Children.Add(new TextBlock { Text = AsT("No skills yet.", "아직 스킬이 없습니다."), Opacity = 0.6, FontSize = 12 });
                foreach (var skill in skills)
                {
                    var s = skill;
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var toggle = new ToggleSwitch { IsOn = enabled.Contains(s.Id), OnContent = "", OffContent = "", MinWidth = 0 };
                    toggle.Toggled += (_, _) =>
                    {
                        var ids = AsstEnabledSkillIds();
                        if (toggle.IsOn) ids.Add(s.Id); else ids.Remove(s.Id);
                        AsstSettings.Set(AsstSkillsEnabledKey, string.Join("|", ids));
                    };
                    var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    info.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    if (s.Description.Length > 0)
                        info.Children.Add(new TextBlock { Text = s.Description, FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(info, 1);
                    var del = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
                    ToolTipService.SetToolTip(del, AsT("Delete skill", "스킬 삭제"));
                    Grid.SetColumn(del, 2);
                    row.Children.Add(toggle); row.Children.Add(info); row.Children.Add(del);
                    list.Children.Add(row);

                    del.Click += (_, _) =>
                    {
                        AsstSkills.Delete(s.Id);
                        var ids = AsstEnabledSkillIds(); ids.Remove(s.Id);
                        AsstSettings.Set(AsstSkillsEnabledKey, string.Join("|", ids));
                        list.Children.Remove(row);
                    };
                }
                content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

                var dialog = new ContentDialog
                {
                    Title = AsT("Skills", "스킬"),
                    Content = content,
                    CloseButtonText = AsT("Done", "완료"),
                    XamlRoot = this.Content.XamlRoot
                };
                var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                HyperlinkButton Link(string text, SkillAction a)
                {
                    var b = new HyperlinkButton { Content = text, Padding = new Thickness(6, 2, 6, 2), FontSize = 12 };
                    b.Click += (_, _) => { action = a; dialog.Hide(); };
                    return b;
                }
                links.Children.Add(Link(AsT("Import SKILL.md…", "SKILL.md 가져오기…"), SkillAction.ImportFile));
                links.Children.Add(Link(AsT("Import skill folder…", "스킬 폴더 가져오기…"), SkillAction.ImportFolder));
                links.Children.Add(Link(AsT("New skill…", "새 스킬…"), SkillAction.Create));
                content.Children.Add(links);

                await ShowContentDialogSafeAsync(dialog);
                if (action == SkillAction.None) return;

                switch (action)
                {
                    case SkillAction.ImportFile: await ImportSkillFileAsync(); break;
                    case SkillAction.ImportFolder: await ImportSkillFolderAsync(); break;
                    case SkillAction.Create: await CreateSkillAsync(); break;
                }
            }
        }

        private void SaveAndEnableSkill(AssistantSkill skill)
        {
            if (!AsstSkills.Save(skill, out var err))
            {
                ViewModel.ShowToast(AsT("Could not save the skill: ", "스킬을 저장하지 못했습니다: ") + err, isError: true);
                return;
            }
            var ids = AsstEnabledSkillIds(); ids.Add(skill.Id);
            AsstSettings.Set(AsstSkillsEnabledKey, string.Join("|", ids));
        }

        private void ImportSkillText(string path, string fallbackName)
        {
            try
            {
                if (new System.IO.FileInfo(path).Length > 200 * 1024)
                {
                    ViewModel.ShowToast(AsT("The skill file is too large.", "스킬 파일이 너무 큽니다."), isError: true);
                    return;
                }
                if (!AssistantSkillParser.TryParse(System.IO.File.ReadAllText(path), fallbackName, out var skill, out var err) || skill == null)
                {
                    ViewModel.ShowToast(AsT("Could not import the skill: ", "스킬을 가져오지 못했습니다: ") + err, isError: true);
                    return;
                }
                SaveAndEnableSkill(skill);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                ViewModel.ShowToast(AsT("Could not read the file: ", "파일을 읽지 못했습니다: ") + ex.Message, isError: true);
            }
        }

        private async Task ImportSkillFileAsync()
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var ext in new[] { ".md", ".markdown", ".txt" }) picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            var name = System.IO.Path.GetFileNameWithoutExtension(file.Path);
            if (name.Equals("SKILL", StringComparison.OrdinalIgnoreCase))
                name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file.Path)) ?? name;
            ImportSkillText(file.Path, name);
        }

        private async Task ImportSkillFolderAsync()
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            var skillFile = System.IO.Directory.EnumerateFiles(folder.Path, "SKILL.md", System.IO.SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (skillFile == null)
            {
                ViewModel.ShowToast(AsT("No SKILL.md found in that folder.", "선택한 폴더에 SKILL.md가 없습니다."), isError: true);
                return;
            }
            ImportSkillText(skillFile, folder.Name);
        }

        private async Task CreateSkillAsync()
        {
            var nameBox = new TextBox { Header = AsT("Name", "이름"), MaxLength = AssistantSkillParser.MaxNameChars };
            var descBox = new TextBox { Header = AsT("Short description (optional)", "짧은 설명 (선택)"), MaxLength = 300 };
            var bodyBox = new TextBox
            {
                Header = AsT("Instructions", "지침"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 140, MaxHeight = 260,
                PlaceholderText = AsT("e.g. Always propose moving photos into YYYY-MM folders and never touch the Raw folder.",
                                      "예: 사진은 항상 YYYY-MM 폴더로 옮기도록 제안하고, Raw 폴더는 건드리지 마.")
            };
            var error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed), FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var panel = new StackPanel { Spacing = 10, Width = 440, Children = { nameBox, descBox, bodyBox, error } };
            var dialog = new ContentDialog
            {
                Title = AsT("New skill", "새 스킬"),
                Content = panel,
                PrimaryButtonText = _loc.Get("Save"),
                CloseButtonText = _loc.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            AssistantSkill? created = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var name = AssistantSkillParser.CleanName(nameBox.Text);
                var raw = $"---\nname: {name}\ndescription: {descBox.Text.Replace('\n', ' ')}\n---\n{bodyBox.Text}";
                if (!AssistantSkillParser.TryParse(raw, name, out created, out var err))
                {
                    error.Text = err; error.Visibility = Visibility.Visible; args.Cancel = true;
                }
            };
            if (await ShowContentDialogSafeAsync(dialog) == ContentDialogResult.Primary && created != null)
                SaveAndEnableSkill(created);
        }
    }
}
