using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Span
{
    /// <summary>
    /// 좌측 미리보기 패널 배치: 밀러 열 오른쪽에 빈 공간이 충분하면 열들을 밀지 않고
    /// 빈 공간 위에 겹쳐서(오버레이) 표시하고, 공간이 없으면 기존처럼 옆 컬럼으로 표시한다.
    /// </summary>
    public sealed partial class MainWindow
    {
        private const double PreviewOverlayMargin = 8;
        private bool _leftPreviewOverlay;
        private bool _applyingPreviewPlacement;

        /// <summary>패널이 표시 중일 때 호출: 현재 빈 공간 기준으로 오버레이/사이드 배치를 결정한다.</summary>
        private void ApplyLeftPreviewPlacement()
        {
            if (_isClosed || _applyingPreviewPlacement) return;
            if (LeftPreviewPanel.Parent is not Grid host) return;
            if (LeftPreviewCol.Width.Value < 1 && !_leftPreviewOverlay) return; // 숨김 상태

            _applyingPreviewPlacement = true;
            try
            {
                double w = GetSavedPreviewWidth("LeftPreviewWidth");
                double total = host.ActualWidth;
                double content = 0;
                if (_activeMillerTabId != null && _tabMillerPanels.TryGetValue(_activeMillerTabId, out var panel))
                    content = panel.items.ActualWidth;
                else
                    content = MillerColumnsControl.ActualWidth;

                bool fits = total > 0 && (total - content) >= (w + PreviewOverlayMargin);

                if (fits)
                {
                    LeftPreviewSplitterCol.Width = new GridLength(0);
                    LeftPreviewCol.Width = new GridLength(0);
                    Grid.SetColumn(LeftPreviewPanel, 0);
                    Grid.SetColumnSpan(LeftPreviewPanel, 3);
                    LeftPreviewPanel.HorizontalAlignment = HorizontalAlignment.Right;
                    LeftPreviewPanel.Width = w;
                    _leftPreviewOverlay = true;
                }
                else
                {
                    Grid.SetColumn(LeftPreviewPanel, 2);
                    Grid.SetColumnSpan(LeftPreviewPanel, 1);
                    LeftPreviewPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                    LeftPreviewPanel.ClearValue(FrameworkElement.WidthProperty);
                    LeftPreviewSplitterCol.Width = new GridLength(2, GridUnitType.Pixel);
                    LeftPreviewCol.Width = new GridLength(w, GridUnitType.Pixel);
                    _leftPreviewOverlay = false;
                }
            }
            finally { _applyingPreviewPlacement = false; }
        }

        /// <summary>패널 숨김 시 배치 상태를 원래대로 되돌린다.</summary>
        private void ResetLeftPreviewPlacement()
        {
            if (!_leftPreviewOverlay) return;
            Grid.SetColumn(LeftPreviewPanel, 2);
            Grid.SetColumnSpan(LeftPreviewPanel, 1);
            LeftPreviewPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            LeftPreviewPanel.ClearValue(FrameworkElement.WidthProperty);
            _leftPreviewOverlay = false;
        }

        private void OnMillerItemsSizeChangedForPreview(object sender, SizeChangedEventArgs e)
        {
            if (_isClosed) return;
            if (!_leftPreviewOverlay && LeftPreviewCol.Width.Value < 1) return;
            ApplyLeftPreviewPlacement();
        }
    }
}
