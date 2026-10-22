using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Span.Models;
using Span.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace Span
{
    /// <summary>
    /// 사이드바 "최근 문서": 엑셀 / 파워포인트 / 문서 행을 누르면 최근에 연 파일 목록이 펼쳐지고,
    /// 항목을 누르면 기본 프로그램으로 연다. 파일 내용은 읽지 않는다 (Windows 최근 항목 바로가기만 사용).
    /// </summary>
    public partial class MainWindow
    {
        private const string RecentDocsExpandedKey = "SidebarRecentDocsExpanded";
        private const int RecentDocsPerKind = 15;

        private bool RecentDocsExpanded => AsstSettings.Get(RecentDocsExpandedKey, true);

        private void ToggleRecentDocsExpanded()
        {
            AsstSettings.Set(RecentDocsExpandedKey, !RecentDocsExpanded);
            ApplyRecentDocsExpanded();
        }

        private void ApplyRecentDocsExpanded()
        {
            SidebarRecentDocsRows.Visibility = RecentDocsExpanded ? Visibility.Visible : Visibility.Collapsed;
            RecentDocsChevron.Angle = RecentDocsExpanded ? 0 : -90;
        }

        private static (string Letter, Color Color) RecentDocBadge(RecentDocKind kind) => kind switch
        {
            RecentDocKind.Excel => ("X", Color.FromArgb(255, 33, 115, 70)),
            RecentDocKind.PowerPoint => ("P", Color.FromArgb(255, 196, 62, 28)),
            _ => ("W", Color.FromArgb(255, 43, 87, 154)),
        };

        private string RecentDocKindLabel(RecentDocKind kind) => kind switch
        {
            RecentDocKind.Excel => AsT("Excel", "엑셀"),
            RecentDocKind.PowerPoint => AsT("PowerPoint", "파워포인트"),
            _ => AsT("Documents", "문서"),
        };

        /// <summary>엑셀 / 파워포인트 / 문서 세 줄을 만든다 (언어가 바뀔 때도 다시 호출됨).</summary>
        private void BuildRecentDocsRows()
        {
            SidebarRecentDocsRows.Children.Clear();
            foreach (var kind in new[] { RecentDocKind.Excel, RecentDocKind.PowerPoint, RecentDocKind.Document })
            {
                var (letter, color) = RecentDocBadge(kind);
                var badge = new Border
                {
                    Width = 16, Height = 16, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                    Child = new TextBlock
                    {
                        Text = letter, FontSize = 10, FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                var label = new TextBlock
                {
                    Text = RecentDocKindLabel(kind), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var row = new Grid
                {
                    Height = 26, Padding = new Thickness(12, 0, 8, 0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    }
                };
                Grid.SetColumn(label, 1);
                row.Children.Add(badge);
                row.Children.Add(label);
                var k = kind;
                row.Tapped += async (_, _) => await ShowRecentDocsFlyoutAsync(k, row);
                row.PointerEntered += OnSidebarItemPointerEntered;
                row.PointerExited += OnSidebarItemPointerExited;
                row.PointerPressed += OnSidebarItemPointerPressed;
                row.PointerReleased += OnSidebarItemPointerReleased;
                SidebarRecentDocsRows.Children.Add(row);
            }
            ApplyRecentDocsExpanded();
        }

        private static IReadOnlyDictionary<RecentDocKind, List<RecentDocument>> LoadRecentDocuments()
        {
            var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            return RecentDocumentsService.Collect(
                RecentDocumentsService.EnumerateRecentLinks(recent),
                FileSystemService.ResolveShellLink,
                // 네트워크 경로는 존재 확인이 오래 걸릴 수 있어 건너뛴다 (열 때 실패하면 안내).
                p => p.StartsWith(@"\\", StringComparison.Ordinal) || System.IO.File.Exists(p),
                RecentDocsPerKind);
        }

        private string RecentDocTimeText(DateTime t)
        {
            var today = DateTime.Today;
            if (t.Date == today) return AsT("Today ", "오늘 ") + t.ToString("HH:mm");
            if (t.Date == today.AddDays(-1)) return AsT("Yesterday ", "어제 ") + t.ToString("HH:mm");
            return t.ToString("yyyy-MM-dd");
        }

        private async Task ShowRecentDocsFlyoutAsync(RecentDocKind kind, FrameworkElement anchor)
        {
            IReadOnlyDictionary<RecentDocKind, List<RecentDocument>> data;
            try { data = await Task.Run(LoadRecentDocuments); }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[RecentDocs] load failed: {ex.Message}");
                return;
            }

            var flyout = new Flyout { Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
            var list = new StackPanel { Spacing = 1, Width = 340 };
            list.Children.Add(new TextBlock
            {
                Text = RecentDocKindLabel(kind), FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["SpanTextTertiaryBrush"],
                Margin = new Thickness(8, 2, 8, 6)
            });

            var docs = data[kind];
            if (docs.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = AsT("No recent files.", "최근에 연 파일이 없습니다."), FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["SpanTextTertiaryBrush"],
                    Margin = new Thickness(8, 4, 8, 8)
                });
            }
            foreach (var doc in docs)
                list.Children.Add(BuildRecentDocEntry(doc, flyout));

            flyout.Content = new ScrollViewer
            {
                MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list
            };
            flyout.ShowAt(anchor);
        }

        private FrameworkElement BuildRecentDocEntry(RecentDocument doc, Flyout flyout)
        {
            var folder = System.IO.Path.GetDirectoryName(doc.Path) ?? string.Empty;
            var folderName = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderName)) folderName = folder;

            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock
            {
                Text = doc.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = $"{folderName} · {RecentDocTimeText(doc.LastUsed)}", FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["SpanTextTertiaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var btn = new Button
            {
                Content = text, Padding = new Thickness(8, 5, 8, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            ToolTipService.SetToolTip(btn, doc.Path);
            btn.Click += (_, _) => { flyout.Hide(); OpenRecentDocument(doc.Path); };
            btn.RightTapped += (s, e) =>
            {
                var menu = new MenuFlyout();
                var openFolder = new MenuFlyoutItem { Text = AsT("Show in folder", "폴더 위치 열기"), Icon = new FontIcon { Glyph = "" } };
                openFolder.Click += (_, _) => { flyout.Hide(); OpenRecentDocumentFolder(doc.Path); };
                menu.Items.Add(openFolder);
                var copy = new MenuFlyoutItem { Text = AsT("Copy path", "경로 복사"), Icon = new FontIcon { Glyph = "" } };
                copy.Click += (_, _) =>
                {
                    var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    pkg.SetText(doc.Path);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                };
                menu.Items.Add(copy);
                menu.ShowAt(btn, new FlyoutShowOptions { Position = e.GetPosition(btn) });
                e.Handled = true;
            };
            return btn;
        }

        private void OpenRecentDocument(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[RecentDocs] open failed: {ex.Message}");
                ViewModel.ShowToast(AsT("Could not open the file.", "파일을 열 수 없습니다."), 2500);
            }
        }

        private void OpenRecentDocumentFolder(string path)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return;
                if (ViewModel.CurrentViewMode == ViewMode.Home || ViewModel.CurrentViewMode == ViewMode.ActionLog
                    || ViewModel.CurrentViewMode == ViewMode.RecycleBin)
                {
                    ViewModel.SwitchViewMode(ViewModel.ResolveViewModeFromHome());
                    UpdateViewModeVisibility();
                }
                var folder = new Models.FolderItem { Name = System.IO.Path.GetFileName(dir), Path = dir };
                _ = ViewModel.ActiveExplorer?.NavigateTo(folder);
                if (ViewModel.CurrentViewMode == ViewMode.MillerColumns) FocusColumnAsync(0); else FocusActiveView();
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[RecentDocs] open folder failed: {ex.Message}");
            }
        }
    }
}
