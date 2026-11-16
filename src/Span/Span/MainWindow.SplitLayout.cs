using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Span.Services;
using System;

namespace Span
{
    /// <summary>
    /// MainWindow의 분할 뷰 방향(좌우 2열 / 위아래 2행) 부분 클래스 (WON).
    /// 두 창의 Grid 배치만 바꾼다. 창 자체와 탐색 상태는 그대로 유지된다.
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _splitLayoutHooked;

        /// <summary>분할 뷰 방향이 위아래(2행)인지 여부.</summary>
        private bool IsSplitStacked => _settings.SplitOrientation == 1;

        /// <summary>
        /// 우측(둘째) 창이 보이는지 여부가 바뀔 때마다 배치를 다시 적용하도록 연결하고 한 번 적용한다.
        /// 우측 창의 Visibility는 IsSplitViewEnabled에 x:Bind되어 있어, 분할 상태가 바뀌는 모든 경로
        /// (토글, 탭 전환, 세션 복원)에서 이 콜백이 호출된다.
        /// </summary>
        private void InitializeSplitLayout()
        {
            if (_splitLayoutHooked) return;
            _splitLayoutHooked = true;
            RightPaneContainer.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => ApplySplitLayout());
            ApplySplitLayout();
        }

        /// <summary>
        /// 설정된 방향에 맞게 좌/우 창과 구분선의 Grid 위치를 정한다.
        /// 좌우: 기존과 동일(0열 | 2열). 위아래: 두 창이 모든 열에 걸쳐 0행 / 1행에 놓인다.
        /// </summary>
        private void ApplySplitLayout()
        {
            try
            {
                if (_isClosed) return;

                bool stacked = IsSplitStacked;
                bool splitVisible = RightPaneContainer.Visibility == Visibility.Visible;

                if (stacked)
                {
                    Grid.SetRow(LeftPaneContainer, 0);
                    Grid.SetColumn(LeftPaneContainer, 0);
                    Grid.SetColumnSpan(LeftPaneContainer, 3);

                    Grid.SetRow(RightPaneContainer, 1);
                    Grid.SetColumn(RightPaneContainer, 0);
                    Grid.SetColumnSpan(RightPaneContainer, 3);
                    RightPaneContainer.Margin = new Thickness(0, 1, 0, 0);

                    SplitRow1.Height = splitVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

                    Grid.SetRow(SplitDivider, 1);
                    Grid.SetColumn(SplitDivider, 0);
                    Grid.SetColumnSpan(SplitDivider, 3);
                    SplitDivider.Width = double.NaN;
                    SplitDivider.Height = 1;
                    SplitDivider.HorizontalAlignment = HorizontalAlignment.Stretch;
                    SplitDivider.VerticalAlignment = VerticalAlignment.Top;
                }
                else
                {
                    Grid.SetRow(LeftPaneContainer, 0);
                    Grid.SetColumn(LeftPaneContainer, 0);
                    Grid.SetColumnSpan(LeftPaneContainer, 1);

                    Grid.SetRow(RightPaneContainer, 0);
                    Grid.SetColumn(RightPaneContainer, 2);
                    Grid.SetColumnSpan(RightPaneContainer, 1);
                    RightPaneContainer.Margin = new Thickness(1, 0, 0, 0);

                    SplitRow1.Height = new GridLength(0);

                    Grid.SetRow(SplitDivider, 0);
                    Grid.SetColumn(SplitDivider, 2);
                    Grid.SetColumnSpan(SplitDivider, 1);
                    SplitDivider.Width = 1;
                    SplitDivider.Height = double.NaN;
                    SplitDivider.HorizontalAlignment = HorizontalAlignment.Left;
                    SplitDivider.VerticalAlignment = VerticalAlignment.Stretch;
                }
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[SplitLayout] ApplySplitLayout error: {ex.Message}");
            }
        }

        /// <summary>
        /// 분할 뷰 버튼 우클릭: 좌우 2열 / 위아래 2행 선택 메뉴.
        /// 분할 뷰가 꺼져 있으면 선택한 방향으로 바로 켠다.
        /// </summary>
        private void OnSplitViewButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            try
            {
                bool enabled = ViewModel.IsSplitViewEnabled;
                bool stacked = IsSplitStacked;

                var flyout = new MenuFlyout();

                var sideBySide = new ToggleMenuFlyoutItem
                {
                    Text = _loc.Get("Split_SideBySide") ?? "Split left/right (2 columns)",
                    Icon = new FontIcon { Glyph = "", FontSize = 14 },
                    IsChecked = enabled && !stacked
                };
                sideBySide.Click += (_, _) => ChooseSplitOrientation(stacked: false);
                flyout.Items.Add(sideBySide);

                var topBottom = new ToggleMenuFlyoutItem
                {
                    Text = _loc.Get("Split_Stacked") ?? "Split top/bottom (2 rows)",
                    Icon = new FontIcon { Glyph = "", FontSize = 14, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform { Angle = 90 } },
                    IsChecked = enabled && stacked
                };
                topBottom.Click += (_, _) => ChooseSplitOrientation(stacked: true);
                flyout.Items.Add(topBottom);

                if (enabled)
                {
                    flyout.Items.Add(new MenuFlyoutSeparator());
                    var close = new MenuFlyoutItem
                    {
                        Text = _loc.Get("Split_Close") ?? "Close split view",
                        Icon = new FontIcon { Glyph = "", FontSize = 14 }
                    };
                    close.Click += (_, _) =>
                    {
                        ToggleSplitView();
                        UpdateSplitViewButtonState();
                    };
                    flyout.Items.Add(close);
                }

                if (sender is FrameworkElement fe)
                    flyout.ShowAt(fe);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[SplitLayout] OnSplitViewButtonRightTapped error: {ex.Message}");
            }
        }

        /// <summary>
        /// 방향을 저장하고 적용한다. 분할 뷰가 꺼져 있으면 켠다.
        /// </summary>
        private void ChooseSplitOrientation(bool stacked)
        {
            try
            {
                _settings.SplitOrientation = stacked ? 1 : 0;
                if (!ViewModel.IsSplitViewEnabled)
                {
                    ToggleSplitView();
                    UpdateSplitViewButtonState();
                }
                ApplySplitLayout();
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[SplitLayout] ChooseSplitOrientation error: {ex.Message}");
            }
        }
    }
}
