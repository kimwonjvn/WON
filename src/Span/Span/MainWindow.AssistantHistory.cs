using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Span.Services.Assistant;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Span
{
    /// <summary>
    /// 어시스턴트 대화 기록: 이 PC(%LOCALAPPDATA%\WON\assistant-chats)에만 저장하고,
    /// 지난 대화를 열어 이어서 대화하거나 개별/전체 삭제할 수 있다. 첨부 파일 내용과 API 키는 저장하지 않는다.
    /// </summary>
    public partial class MainWindow
    {
        private void OnAssistantHistoryToggled(object sender, RoutedEventArgs e)
        {
            if (_asstUiLoading) return;
            AsstSettings.Set(AsstHistoryKey, AssistantHistoryToggle.IsOn);
        }

        /// <summary>패널을 처음 열 때: 가장 최근 대화를 이어서 보여 주고, 없으면 시작 화면.</summary>
        private void ResumeLastAssistantChat()
        {
            if (AsstHistoryEnabled)
            {
                var last = AsstChats.List().FirstOrDefault();
                if (last != null && LoadAssistantChat(last.Id)) return;
            }
            ShowAssistantEmptyState();
        }

        private void StartNewAssistantChat()
        {
            _asstCts?.Cancel();
            _asstAgent?.ResetConversation();
            _asstRestoreTurns = null;
            _asstChat = null;
            HideThinking();
            ShowAssistantEmptyState();
            AssistantInput.Focus(FocusState.Programmatic);
        }

        /// <summary>저장된 대화를 화면에 그리고, 다음 질문부터 그 맥락으로 이어가게 한다.</summary>
        private bool LoadAssistantChat(string id)
        {
            var chat = AsstChats.Load(id);
            if (chat == null || chat.Messages.Count == 0) return false;

            _asstCts?.Cancel();
            _asstAgent?.ResetConversation();
            HideThinking();
            _asstChat = chat;
            _asstRestoreTurns = chat.Messages.Select(m => (m.Role == "user", m.Text)).ToList();

            AssistantMessages.Children.Clear();
            foreach (var m in chat.Messages)
            {
                if (m.Role == "user")
                    AddUserMessage(m.Text, Array.Empty<LlmAttachment>(), animate: false, restoredNames: m.Attachments);
                else
                    AddAssistantMessage(m.Text, hitLimit: false, animate: false);
            }
            AddAssistantNote(AsT("Earlier chat restored — keep going below. Past plans are not re-run.", "지난 대화를 불러왔어요. 아래에서 이어서 질문하세요. 이전 변경 계획은 다시 실행되지 않습니다."));
            return true;
        }

        private void PersistAssistantExchange(string userText, IReadOnlyList<LlmAttachment> attachments, string reply)
        {
            if (!AsstHistoryEnabled) return;
            _asstChat ??= new StoredChat();
            _asstChat.Messages.Add(new StoredChatMessage { Role = "user", Text = userText, Attachments = attachments.Select(a => a.Name).ToList() });
            _asstChat.Messages.Add(new StoredChatMessage { Role = "assistant", Text = reply ?? string.Empty });
            _asstChat.Updated = DateTime.Now;
            if (string.IsNullOrWhiteSpace(_asstChat.Title)) _asstChat.Title = AssistantChatStore.MakeTitle(userText);
            AsstChats.Save(_asstChat);
        }

        private void OnAssistantHistoryClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement anchor) ShowAssistantHistoryFlyout(anchor);
        }

        private void ShowAssistantHistoryFlyout(FrameworkElement anchor)
        {
            var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
            var tertiary = (Brush)Application.Current.Resources["SpanTextTertiaryBrush"];
            var root = new StackPanel { Width = 330, Spacing = 4 };
            root.Children.Add(new TextBlock
            {
                Text = AsT("Chat history", "대화 기록"), FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = tertiary, Margin = new Thickness(8, 2, 8, 6)
            });

            var chats = AsstChats.List();
            if (!AsstHistoryEnabled)
                root.Children.Add(new TextBlock
                {
                    Text = AsT("Saving is off (turn it on in the settings). Existing chats are listed below.", "기록 저장이 꺼져 있습니다 (설정에서 켤 수 있어요). 저장된 대화만 표시합니다."),
                    FontSize = 11, Foreground = tertiary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 6)
                });
            if (chats.Count == 0)
                root.Children.Add(new TextBlock
                {
                    Text = AsT("No saved chats yet.", "저장된 대화가 없습니다."), FontSize = 12, Foreground = tertiary,
                    Margin = new Thickness(8, 4, 8, 8)
                });

            var list = new StackPanel { Spacing = 1 };
            foreach (var c in chats)
                list.Children.Add(BuildHistoryRow(c, flyout));
            root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            if (chats.Count > 0)
            {
                var clear = new Button
                {
                    Content = AsT("Delete all chats", "모든 대화 삭제"),
                    HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0),
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                };
                clear.Click += async (_, _) => { flyout.Hide(); await DeleteAllAssistantChatsAsync(chats.Count); };
                root.Children.Add(clear);
            }

            flyout.Content = root;
            flyout.ShowAt(anchor);
        }

        private FrameworkElement BuildHistoryRow(StoredChatSummary chat, Flyout flyout)
        {
            bool current = _asstChat != null && _asstChat.Id == chat.Id;
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = chat.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });
            text.Children.Add(new TextBlock
            {
                Text = $"{RecentDocTimeText(chat.Updated)} · {chat.MessageCount / 2}{AsT(" exchanges", "개 대화")}",
                FontSize = 11, Foreground = (Brush)Application.Current.Resources["SpanTextTertiaryBrush"]
            });

            var open = new Button
            {
                Content = text, Padding = new Thickness(8, 5, 8, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                Background = current ? (Brush)Application.Current.Resources["SpanBgLayer2Brush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            var id = chat.Id;
            open.Click += (_, _) =>
            {
                flyout.Hide();
                if (_asstBusy) { ViewModel.ShowToast(AsT("Wait for the current answer first.", "진행 중인 답변이 끝난 뒤에 열 수 있습니다."), 2000); return; }
                if (!LoadAssistantChat(id)) ViewModel.ShowToast(AsT("Could not open that chat.", "대화를 열 수 없습니다."), 2000);
            };

            var del = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(del, AsT("Delete this chat", "이 대화 삭제"));
            del.Click += (_, _) =>
            {
                flyout.Hide();
                AsstChats.Delete(id);
                if (_asstChat != null && _asstChat.Id == id) StartNewAssistantChat();
                ViewModel.ShowToast(AsT("Chat deleted.", "대화를 삭제했습니다."), 1800);
            };

            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
            Grid.SetColumn(del, 1);
            row.Children.Add(open);
            row.Children.Add(del);
            return row;
        }

        private async Task DeleteAllAssistantChatsAsync(int count)
        {
            var dialog = new ContentDialog
            {
                Title = AsT("Delete all chats?", "모든 대화를 삭제할까요?"),
                Content = AsT($"{count} saved chat(s) will be deleted from this PC. This cannot be undone.",
                              $"저장된 대화 {count}개가 이 PC에서 삭제됩니다. 되돌릴 수 없습니다."),
                PrimaryButtonText = AsT("Delete", "삭제"),
                CloseButtonText = _loc.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };
            if (await ShowContentDialogSafeAsync(dialog) != ContentDialogResult.Primary) return;
            AsstChats.DeleteAll();
            StartNewAssistantChat();
            ViewModel.ShowToast(AsT("All chats deleted.", "모든 대화를 삭제했습니다."), 2000);
        }
    }
}
