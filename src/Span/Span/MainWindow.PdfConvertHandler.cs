using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Span.Services;
using Span.Services.PdfConversion;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Span
{
    /// <summary>
    /// 선택한 파일(이미지 / Word / Excel / PowerPoint / HWP …)을 PDF 로 변환하거나 하나의 PDF 로 합친다.
    /// 이미지와 PDF 합치기는 내장 PDFsharp(MIT), 문서는 한컴오피스 / Microsoft Office / LibreOffice 중 설치된 엔진을 쓴다.
    /// 원본은 바꾸지 않고, 결과는 원본 옆에 새 파일로 만든다 (기존 파일을 덮어쓰지 않는다).
    /// </summary>
    public partial class MainWindow : IPdfConvertHost
    {
        private PdfConversionService? _pdfService;
        private CancellationTokenSource? _pdfCts;
        private bool _pdfBusy;

        private PdfConversionService PdfService => _pdfService ??= new PdfConversionService(
            new IPdfEngine[] { new HancomEngine(), new MsOfficeEngine(), new LibreOfficeEngine() },
            PdfImageLoader.LoadAsync);

        void IPdfConvertHost.PerformConvertToPdf(string[] paths, bool merge)
        {
            if (paths.Length == 0) return;
            _ = RunPdfConversionAsync(GetSelectedPathsForContextMenu(paths[0]), merge);
        }

        /// <summary>단축키(Ctrl+Shift+D / Ctrl+Shift+M)용: 현재 선택한 항목을 변환한다.</summary>
        private void StartPdfFromSelection(bool merge)
        {
            var paths = new List<string>();
            try { paths = GetCurrentSelectedItems().Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList(); }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[Pdf] selection failed: {ex.Message}"); }
            _ = RunPdfConversionAsync(paths, merge);
        }

        private async Task RunPdfConversionAsync(IReadOnlyList<string> selection, bool merge)
        {
            if (_pdfBusy)
            {
                ViewModel.ShowToast(AsT("A PDF conversion is already running.", "PDF 변환이 이미 진행 중입니다."), 2500);
                return;
            }

            // 로컬 파일만 (FTP/SFTP·압축 파일 내부 항목 제외), 변환 가능한 형식만.
            var files = selection
                .Where(p => !p.Contains("://", StringComparison.Ordinal) && !Helpers.ArchivePathHelper.IsArchivePath(p))
                .Where(p => merge ? PdfSourceClassifier.CanMerge(p) : PdfSourceClassifier.CanConvert(p))
                .Where(System.IO.File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                ViewModel.ShowToast(AsT("Select images, Word, Excel, PowerPoint or HWP files to convert.",
                                        "변환할 이미지·Word·Excel·PowerPoint·HWP 파일을 선택하세요."), 3000);
                return;
            }
            if (merge && files.Count == 1 && PdfSourceClassifier.Classify(files[0]) != PdfSourceKind.Pdf) merge = false;

            _pdfBusy = true;
            _pdfCts = new CancellationTokenSource();
            var progress = new Progress<PdfProgress>(p =>
            {
                if (p.Done < p.Total)
                    ViewModel.ShowToast(AsT($"Converting to PDF… {p.Done + 1}/{p.Total}  {p.CurrentName}",
                                            $"PDF 변환 중… {p.Done + 1}/{p.Total}  {p.CurrentName}"), 60_000);
            });

            try
            {
                var result = await PdfService.RunAsync(files, merge, progress, _pdfCts.Token);
                await ReportPdfResultAsync(result, merge);
            }
            catch (OperationCanceledException)
            {
                ViewModel.ShowToast(AsT("PDF conversion cancelled.", "PDF 변환을 취소했습니다."), 2500);
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[Pdf] failed: {ex.GetType().Name}: {ex.Message}");
                ViewModel.ShowToast(AsT("PDF conversion failed: ", "PDF 변환 실패: ") + ex.Message, 5000);
            }
            finally
            {
                _pdfBusy = false;
                _pdfCts?.Dispose(); _pdfCts = null;
            }
        }

        private async Task ReportPdfResultAsync(PdfBatchResult result, bool merge)
        {
            string summary;
            if (merge && result.MergedOutput != null)
                summary = AsT($"Merged PDF created ({result.MergedPages} pages): {System.IO.Path.GetFileName(result.MergedOutput)}",
                              $"합본 PDF를 만들었습니다 ({result.MergedPages}쪽): {System.IO.Path.GetFileName(result.MergedOutput)}");
            else if (result.Succeeded == 1 && result.Failed == 0)
                summary = AsT($"PDF created: {System.IO.Path.GetFileName(result.Items.First(i => i.Ok).Output)}",
                              $"PDF를 만들었습니다: {System.IO.Path.GetFileName(result.Items.First(i => i.Ok).Output)}");
            else
                summary = AsT($"PDF conversion done: {result.Succeeded} succeeded, {result.Failed} failed",
                              $"PDF 변환 완료: 성공 {result.Succeeded}개, 실패 {result.Failed}개");

            ViewModel.ShowToast(summary, result.Failed > 0 ? 4000 : 4500);

            if (result.Failed == 0) return;

            var lines = string.Join("\n", result.Items.Where(i => !i.Ok)
                .Select(i => $"• {System.IO.Path.GetFileName(i.Source)} — {i.Error}"));
            var dialog = new ContentDialog
            {
                Title = AsT("Some files could not be converted", "변환하지 못한 파일이 있습니다"),
                Content = new ScrollViewer
                {
                    MaxHeight = 320,
                    Content = new TextBlock { Text = lines, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 }
                },
                CloseButtonText = AsT("OK", "확인"),
                XamlRoot = this.Content.XamlRoot
            };
            await ShowContentDialogSafeAsync(dialog);
        }
    }
}
