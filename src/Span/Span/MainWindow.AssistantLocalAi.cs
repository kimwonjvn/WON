using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Span.Services.Assistant;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Span
{
    /// <summary>
    /// 로컬 AI(Ollama) 연결 도우미: 초심자가 "설치 → 연결 확인 → 모델 받기" 순서로 따라 할 수 있게 돕는다.
    /// 이 PC(localhost)의 Ollama 와만 통신하고, 모델 내려받기는 사용자가 확인한 뒤에만 시작한다.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly HttpClient LocalAiHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly HttpClient LocalAiPullHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

        private CancellationTokenSource? _localPullCts;
        private bool _localChecking;
        private bool _localPulling;

        private void ApplyLocalAiTexts()
        {
            AssistantLocalTitle.Text = AsT("Local AI setup helper", "로컬 AI 연결 도우미");
            AssistantLocalSteps.Text = AsT(
                "First time? Three steps:\n1. Click \"Download Ollama\", run the installer.\n2. Click \"Check connection\".\n3. Click \"Get the model\" (a few GB, needs internet once).\nAfter that it works offline and nothing leaves your PC.",
                "처음이라면 3단계로 끝납니다.\n① 'Ollama 내려받기'로 설치 프로그램을 받아 실행합니다.\n② 설치가 끝나면 '연결 확인'을 누릅니다.\n③ '모델 받기'로 AI 모델(수 GB)을 한 번 내려받습니다.\n그 뒤로는 인터넷 없이도 동작하고, 내용이 PC 밖으로 나가지 않습니다.");
            AssistantLocalCheckButton.Content = AsT("Check connection", "연결 확인");
            AssistantLocalDownloadButton.Content = AsT("Download Ollama", "Ollama 내려받기");
            AssistantLocalGuideButton.Content = AsT("Open the guide", "가이드 열기");
            if (!_localPulling) AssistantLocalPullButton.Content = AsT("Get the model", "모델 받기");
        }

        private bool AsstIsLocalProvider => AsstProvider() == "Local";

        /// <summary>설정 영역이 열려 있고 제공자가 Local 이면 연결 상태를 자동으로 확인한다.</summary>
        private void MaybeCheckLocalAi()
        {
            if (!AsstIsLocalProvider || AssistantSettingsArea.Visibility != Visibility.Visible) return;
            _ = RunLocalAiCheckAsync();
        }

        private bool TryGetLocalBase(out Uri baseUri)
        {
            baseUri = new Uri(AsstDefaultBase("Local"));
            if (!LlmEndpoint.TryResolveBase(AsstBaseUrl("Local"), AsstDefaultBase("Local"), out var u, out _) || !u.IsLoopback) return false;
            baseUri = u;
            return true;
        }

        private async Task RunLocalAiCheckAsync()
        {
            if (_localChecking || _localPulling) return;
            _localChecking = true;
            AssistantLocalStatus.Text = AsT("Checking…", "확인하는 중…");
            try
            {
                if (!TryGetLocalBase(out var baseUri))
                {
                    AssistantLocalStatus.Text = AsT("The local server address must be on this PC (localhost).", "로컬 서버 주소는 이 PC(localhost)여야 합니다.");
                    return;
                }
                var model = AsstModel("Local");
                var status = await LocalAiHelper.CheckAsync(LocalAiHttp, baseUri, model, CancellationToken.None);
                AssistantLocalStatus.Text = LocalAiHelper.Describe(status, model, AsstKo);
                AssistantLocalPullButton.IsEnabled = status.State is LocalAiState.ModelMissing or LocalAiState.NoToolSupport or LocalAiState.Ready;
                AssistantLocalCheckButton.Content = status.State == LocalAiState.Ready
                    ? AsT("Check again", "다시 확인") : AsT("Check connection", "연결 확인");
            }
            finally { _localChecking = false; }
        }

        private void OnLocalAiCheckClick(object sender, RoutedEventArgs e) => _ = RunLocalAiCheckAsync();

        private async void OnLocalAiDownloadClick(object sender, RoutedEventArgs e)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(LocalAiHelper.DownloadUrl)); }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[LocalAi] open download page failed: {ex.Message}"); }
        }

        private async void OnLocalAiGuideClick(object sender, RoutedEventArgs e)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(LocalAiHelper.GuideUrl)); }
            catch (Exception ex) { Helpers.DebugLogger.Log($"[LocalAi] open guide failed: {ex.Message}"); }
        }

        private async void OnLocalAiPullClick(object sender, RoutedEventArgs e)
        {
            if (_localPulling) { _localPullCts?.Cancel(); return; }
            if (_localChecking) return;
            if (!TryGetLocalBase(out var baseUri))
            {
                AssistantLocalStatus.Text = AsT("The local server address must be on this PC (localhost).", "로컬 서버 주소는 이 PC(localhost)여야 합니다.");
                return;
            }
            var model = AsstModel("Local");

            // 이미 받은 모델이면 다시 받지 않는다.
            var before = await LocalAiHelper.CheckAsync(LocalAiHttp, baseUri, model, CancellationToken.None);
            if (before.State == LocalAiState.NotReachable)
            {
                AssistantLocalStatus.Text = LocalAiHelper.Describe(before, model, AsstKo);
                return;
            }
            if (before.State == LocalAiState.Ready)
            {
                AssistantLocalStatus.Text = LocalAiHelper.Describe(before, model, AsstKo);
                return;
            }

            var dialog = new ContentDialog
            {
                Title = AsT("Download the AI model?", "AI 모델을 내려받을까요?"),
                Content = AsT(
                    $"\"{model}\" will be downloaded by Ollama onto this PC. Models are several GB (qwen3.5:9b is about 6.6 GB), so make sure you have free disk space and a good connection. You can cancel and resume later.",
                    $"Ollama가 \"{model}\" 모델을 이 PC에 내려받습니다. 모델은 수 GB입니다(qwen3.5:9b는 약 6.6GB). 디스크 여유 공간과 인터넷 상태를 확인하세요. 중간에 취소해도 나중에 이어서 받을 수 있습니다."),
                PrimaryButtonText = AsT("Download", "내려받기"),
                CloseButtonText = _loc.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };
            if (await ShowContentDialogSafeAsync(dialog) != ContentDialogResult.Primary) return;

            _localPulling = true;
            _localPullCts = new CancellationTokenSource();
            AssistantLocalPullButton.Content = AsT("Cancel download", "받기 취소");
            AssistantLocalCheckButton.IsEnabled = false;
            AssistantLocalProgress.Value = 0;
            AssistantLocalProgress.IsIndeterminate = true;
            AssistantLocalProgress.Visibility = Visibility.Visible;
            AssistantLocalProgressText.Visibility = Visibility.Visible;
            AssistantLocalProgressText.Text = AsT("Starting…", "시작하는 중…");
            AssistantLocalStatus.Text = AsT("Downloading the model. You can keep using WON meanwhile.", "모델을 내려받는 중입니다. 그동안 WON을 계속 쓰셔도 됩니다.");

            try
            {
                var progress = new Progress<PullProgress>(p =>
                {
                    if (p.Fraction is double f)
                    {
                        AssistantLocalProgress.IsIndeterminate = false;
                        AssistantLocalProgress.Value = f;
                        AssistantLocalProgressText.Text = $"{(int)(f * 100)}%  ({p.Completed / 1_000_000_000.0:0.0} / {p.Total / 1_000_000_000.0:0.0} GB)  {p.Status}";
                    }
                    else
                    {
                        AssistantLocalProgress.IsIndeterminate = true;
                        AssistantLocalProgressText.Text = p.Status;
                    }
                });
                await LocalAiHelper.PullAsync(LocalAiPullHttp, baseUri, model, progress, _localPullCts.Token);
                AssistantLocalStatus.Text = AsT("Download finished.", "내려받기를 마쳤습니다.");
            }
            catch (OperationCanceledException)
            {
                AssistantLocalStatus.Text = AsT("Download cancelled. Press \"Get the model\" later to continue where it stopped.", "내려받기를 취소했습니다. 나중에 '모델 받기'를 다시 누르면 이어서 받습니다.");
            }
            catch (Exception ex)
            {
                Helpers.DebugLogger.Log($"[LocalAi] pull failed: {ex.GetType().Name}: {ex.Message}");
                AssistantLocalStatus.Text = AsT("Download failed: ", "내려받기에 실패했습니다: ") + ex.Message;
            }
            finally
            {
                _localPulling = false;
                _localPullCts?.Dispose(); _localPullCts = null;
                AssistantLocalPullButton.Content = AsT("Get the model", "모델 받기");
                AssistantLocalCheckButton.IsEnabled = true;
                AssistantLocalProgress.Visibility = Visibility.Collapsed;
                AssistantLocalProgressText.Visibility = Visibility.Collapsed;
            }
            await RunLocalAiCheckAsync();
        }
    }
}
