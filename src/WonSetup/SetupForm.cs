using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WonSetup
{
    internal sealed class SetupForm : Form
    {
        private static readonly bool Ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
        private static string T(string en, string ko) => Ko ? ko : en;

        private readonly Button _install = new Button();
        private readonly Button _close = new Button();
        private readonly CheckBox _launch = new CheckBox();
        private readonly CheckBox _desktop = new CheckBox();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();
        private bool _busy;
        private bool _done;

        public SetupForm()
        {
            Text = T("WON Setup", "WON 설치");
            ClientSize = new Size(540, 432);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (s != null) Icon = new Icon(s);
            }
            catch { /* the icon is decoration only */ }

            var logo = new PictureBox { Location = new Point(28, 24), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom };
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (s != null) logo.Image = new Icon(s, 64, 64).ToBitmap();
            }
            catch { }
            Controls.Add(logo);

            Controls.Add(new Label
            {
                Text = "WON", Font = new Font("Segoe UI Semibold", 22F), AutoSize = true, Location = new Point(104, 20)
            });
            Controls.Add(new Label
            {
                Text = T("Version ", "버전 ") + Payload.Version, ForeColor = Color.DimGray, AutoSize = true, Location = new Point(108, 68)
            });

            Controls.Add(new Label
            {
                Text = T("A Finder-style file explorer for Windows. Click Install and it is ready in a moment.",
                         "윈도우용 파인더 스타일 파일 탐색기입니다. 설치를 누르면 잠시 후 바로 쓸 수 있습니다."),
                Location = new Point(28, 108), Size = new Size(484, 24)
            });

            Controls.Add(new Label
            {
                Text = T("Simple and safe", "간단하고 안전합니다"),
                Font = new Font("Segoe UI Semibold", 9.5F), Location = new Point(28, 144), AutoSize = true
            });
            Controls.Add(new Label
            {
                Text = T("• No administrator approval and no certificate are needed.\n" +
                         "• Installed only for your Windows account, in " + Installer.DefaultDirectory + ".\n" +
                         "• Nothing else is installed and nothing is uploaded. Remove it any time in Settings > Apps.",
                         "• 관리자 승인도, 인증서 설치도 필요하지 않습니다.\n" +
                         "• 현재 Windows 계정에만 설치됩니다: " + Installer.DefaultDirectory + "\n" +
                         "• 다른 프로그램은 설치하지 않고 아무것도 업로드하지 않습니다. 제거는 설정 > 앱에서 언제든 할 수 있습니다."),
                Location = new Point(28, 168), Size = new Size(484, 80), ForeColor = Color.FromArgb(60, 60, 60)
            });

            _desktop.Text = T("Add a shortcut to the desktop", "바탕화면에 바로가기 만들기");
            _desktop.Checked = true;
            _desktop.AutoSize = true;
            _desktop.Location = new Point(28, 256);
            Controls.Add(_desktop);

            _launch.Text = T("Start WON when finished", "설치가 끝나면 WON 실행");
            _launch.Checked = true;
            _launch.AutoSize = true;
            _launch.Location = new Point(28, 280);
            Controls.Add(_launch);

            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Maximum = 1000;
            _bar.Location = new Point(28, 312);
            _bar.Size = new Size(484, 8);
            _bar.Visible = false;
            Controls.Add(_bar);

            _status.Location = new Point(28, 326);
            _status.Size = new Size(484, 34);
            Controls.Add(_status);

            Controls.Add(new Label
            {
                Text = T("This installer is signed by an individual, so Windows may show an \"unknown publisher\" warning. Install only if you trust where you downloaded it.",
                         "이 설치 파일은 개인이 서명한 것이라 Windows가 '알 수 없는 게시자' 경고를 보일 수 있습니다. 내려받은 곳을 신뢰할 때만 설치하세요."),
                ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F), Location = new Point(28, 372), Size = new Size(296, 52)
            });

            _install.Text = T("Install", "설치");
            _install.Size = new Size(84, 30);
            _install.Location = new Point(334, 384);
            _install.Click += async (s, e) => await RunInstallAsync();
            Controls.Add(_install);

            _close.Text = T("Cancel", "취소");
            _close.Size = new Size(84, 30);
            _close.Location = new Point(428, 384);
            _close.Click += (s, e) => Close();
            Controls.Add(_close);

            AcceptButton = _install;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                MessageBox.Show(this, T("Please wait until the installation finishes.", "설치가 끝날 때까지 기다려 주세요."), Text);
                return;
            }
            base.OnFormClosing(e);
        }

        private void SetStatus(string text, bool error = false)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, error))); return; }
            _status.ForeColor = error ? Color.Firebrick : Color.FromArgb(40, 40, 40);
            _status.Text = text;
        }

        private void SetProgress(double fraction)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(fraction))); return; }
            _bar.Value = Math.Max(0, Math.Min(_bar.Maximum, (int)(fraction * _bar.Maximum)));
        }

        private async Task RunInstallAsync()
        {
            if (_busy) return;
            _busy = true;
            _install.Enabled = false;
            _close.Enabled = false;
            _desktop.Enabled = false;
            _launch.Enabled = false;
            _bar.Value = 0;
            _bar.Visible = true;

            Log.Write("install started, version " + Payload.Version);
            string exe = string.Empty;
            var options = new InstallOptions { DesktopShortcut = _desktop.Checked };
            try
            {
                exe = await Task.Run(() => Installer.Run(options, s => SetStatus(s), SetProgress, AskCloseRunning, AskRemoveMsix, Ko));
                _done = true;
                Log.Write("install finished");
                SetStatus(T("WON is installed.", "WON 설치가 끝났습니다."));
                if (_launch.Checked) Installer.Launch(exe);
            }
            catch (InstallException ex)
            {
                Log.Write("InstallException: " + ex.Message);
                SetStatus(T("Installation failed: ", "설치하지 못했습니다: ") + ex.Message, error: true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                Log.Write("Blocked: " + ex);
                SetStatus(T("Windows or a security program blocked the installation (", "Windows 또는 보안 프로그램이 설치를 막았습니다(")
                          + ex.Message + T("). On a company or school PC, ask the administrator to allow WON_Setup.exe. Log: ", "). 회사·학교 PC라면 관리자에게 WON_Setup.exe 허용을 요청해 주세요. 기록: ")
                          + Path.Combine(Path.GetTempPath(), "WON-setup.log"), error: true);
            }
            catch (Exception ex)
            {
                Log.Write("Unexpected: " + ex);
                SetStatus(T("Unexpected error: ", "예상치 못한 오류: ") + ex.Message + "  (" + T("log: ", "기록: ") + Path.Combine(Path.GetTempPath(), "WON-setup.log") + ")", error: true);
            }
            finally
            {
                _bar.Visible = false;
                _busy = false;
                _close.Enabled = true;
                _close.Text = T("Close", "닫기");
                if (_done) { _install.Visible = false; AcceptButton = _close; }
                else { _install.Enabled = true; _install.Text = T("Try again", "다시 시도"); }
            }
        }

        private bool AskCloseRunning()
        {
            bool yes = false;
            Invoke(new Action(() => yes = MessageBox.Show(this,
                T("WON is running and must be closed to update. Close it and continue? (Finish any file operation in progress first.)",
                  "WON이 실행 중이라 닫아야 업데이트할 수 있습니다. 닫고 계속할까요? (진행 중인 파일 작업이 있다면 먼저 끝내 주세요.)"),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
            return yes;
        }

        /// <summary>The previous flavour of WON (installed with a certificate) can stay next to this one, or be removed.</summary>
        private bool AskRemoveMsix()
        {
            bool carry = Installer.SettingsCarryOver();
            bool remove = false;
            Invoke(new Action(() => remove = MessageBox.Show(this,
                T("An older WON that was installed with a certificate was found. Remove it now so there is only one WON?\n\n" +
                  (carry ? "Your settings are carried over. " : "Some on-screen settings (theme, language, view) will start from their defaults. ") +
                  "Your favorites, chats and server connections are kept.\n\nChoose No to keep both.",
                  "인증서로 설치했던 이전 WON이 있습니다. WON이 하나만 남도록 지금 제거할까요?\n\n" +
                  (carry ? "설정은 그대로 이어집니다. " : "테마·언어·보기 방식 같은 화면 설정은 기본값으로 시작합니다. ") +
                  "즐겨찾기, 대화 기록, 서버 연결 정보는 그대로 남습니다.\n\n'아니요'를 누르면 둘 다 남겨 둡니다."),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes));
            if (!remove) return false;
            SetStatus(T("Removing the older WON…", "이전 WON을 제거하는 중…"));
            var ok = Installer.RemoveMsix();
            if (!ok)
                Invoke(new Action(() => MessageBox.Show(this,
                    T("The older WON could not be removed automatically. You can remove it in Settings > Apps (both can stay installed meanwhile).",
                      "이전 WON을 자동으로 제거하지 못했습니다. 설정 > 앱에서 직접 제거하실 수 있습니다(그동안 둘 다 설치된 상태로 써도 됩니다)."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information)));
            return ok;
        }
    }
}
