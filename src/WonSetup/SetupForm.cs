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
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();
        private bool _busy;
        private bool _done;

        public SetupForm()
        {
            Text = T("WON Setup", "WON 설치");
            ClientSize = new Size(540, 420);
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
                Text = T("Installs the WON file explorer for your Windows account. Your existing WON settings are kept.",
                         "WON 파일 탐색기를 현재 Windows 계정에 설치합니다. 이미 쓰고 있다면 설정은 그대로 유지됩니다."),
                Location = new Point(28, 108), Size = new Size(484, 40)
            });

            Controls.Add(new Label
            {
                Text = T("What happens when you click Install", "설치를 누르면 이렇게 진행됩니다"),
                Font = new Font("Segoe UI Semibold", 9.5F), Location = new Point(28, 156), AutoSize = true
            });
            Controls.Add(new Label
            {
                Text = T("1. The installer checks that the package is signed by the WON certificate.\n" +
                         "2. Windows asks for administrator approval once, to trust that certificate (needed because WON is self-signed).\n" +
                         "3. WON is installed. No other software is installed and nothing is uploaded.",
                         "1. 설치 파일이 WON 인증서로 서명된 것인지 확인합니다.\n" +
                         "2. WON이 자체 서명이라, 그 인증서를 신뢰하도록 Windows가 관리자 승인을 한 번 묻습니다.\n" +
                         "3. WON을 설치합니다. 다른 프로그램은 설치하지 않으며 아무것도 업로드하지 않습니다."),
                Location = new Point(28, 180), Size = new Size(484, 84), ForeColor = Color.FromArgb(60, 60, 60)
            });

            _launch.Text = T("Start WON when finished", "설치가 끝나면 WON 실행");
            _launch.Checked = true;
            _launch.AutoSize = true;
            _launch.Location = new Point(28, 276);
            Controls.Add(_launch);

            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;
            _bar.Location = new Point(28, 308);
            _bar.Size = new Size(484, 8);
            _bar.Visible = false;
            Controls.Add(_bar);

            _status.Location = new Point(28, 322);
            _status.Size = new Size(484, 36);
            Controls.Add(_status);

            Controls.Add(new Label
            {
                Text = T("This installer and app are signed by an individual, so Windows may show an \"unknown publisher\" warning. Install only if you trust where you downloaded it.",
                         "이 설치 파일과 앱은 개인이 서명한 것이라 Windows가 '알 수 없는 게시자' 경고를 보일 수 있습니다. 내려받은 곳을 신뢰할 때만 설치하세요."),
                ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F), Location = new Point(28, 366), Size = new Size(290, 44)
            });

            _install.Text = T("Install", "설치");
            _install.Size = new Size(84, 30);
            _install.Location = new Point(332 + 28, 372);
            _install.Click += async (s, e) => await RunInstallAsync();
            Controls.Add(_install);

            _close.Text = T("Cancel", "취소");
            _close.Size = new Size(84, 30);
            _close.Location = new Point(428, 372);
            _close.Click += (s, e) => Close();
            Controls.Add(_close);

            AcceptButton = _install;
            _install.Location = new Point(334, 372);
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

        private async Task RunInstallAsync()
        {
            if (_busy) return;
            _busy = true;
            _install.Enabled = false;
            _close.Enabled = false;
            _bar.Visible = true;

            var dir = Path.Combine(Path.GetTempPath(), "WON-setup-" + Guid.NewGuid().ToString("N"));
            Log.Write("install started, version " + Payload.Version);
            string family = string.Empty;
            try
            {
                family = await Task.Run(() => InstallCore(dir));
                _done = true;
                Log.Write("install finished");
                SetStatus(T("WON is installed.", "WON 설치가 끝났습니다."));
                if (_launch.Checked) Installer.LaunchApp(family);
            }
            catch (InstallException ex)
            {
                Log.Write("InstallException: " + ex.Message);
                SetStatus(T("Installation failed: ", "설치하지 못했습니다: ") + ex.Message, error: true);
            }
            catch (Exception ex)
            {
                Log.Write("Unexpected: " + ex);
                SetStatus(T("Unexpected error: ", "예상치 못한 오류: ") + ex.Message, error: true);
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                _bar.Visible = false;
                _busy = false;
                _close.Enabled = true;
                _close.Text = T("Close", "닫기");
                if (_done) { _install.Visible = false; AcceptButton = _close; }
                else { _install.Enabled = true; _install.Text = T("Try again", "다시 시도"); }
            }
        }

        private string InstallCore(string dir)
        {
            Directory.CreateDirectory(dir);
            var msix = Path.Combine(dir, "WON.msix");
            var cer = Path.Combine(dir, "WON.cer");

            SetStatus(T("Preparing files…", "파일을 준비하는 중…"));
            Payload.Extract("payload.cer", cer, null);
            Payload.Extract("payload.msix", msix, p => SetStatus(T("Preparing files… ", "파일을 준비하는 중… ") + (int)(p * 100) + "%"));

            SetStatus(T("Checking the package signature…", "설치 파일의 서명을 확인하는 중…"));
            Installer.VerifyPackageSignature(msix);

            if (!Installer.IsCertificateTrusted(Payload.ExpectedThumbprint))
            {
                SetStatus(T("Waiting for administrator approval to trust the WON certificate…",
                            "WON 인증서를 신뢰하도록 관리자 승인을 기다리는 중…"));
                Installer.TrustCertificateElevated(cer);
                if (!Installer.IsCertificateTrusted(Payload.ExpectedThumbprint))
                    throw new InstallException(T("The certificate could not be trusted. Administrator rights are required.",
                                                 "인증서를 신뢰 목록에 올리지 못했습니다. 관리자 권한이 필요합니다."));
            }

            bool closeApp = false;
            if (Installer.IsAppRunning())
            {
                bool yes = false;
                Invoke(new Action(() => yes = MessageBox.Show(this,
                    T("WON is running and must be closed to update. Close it and continue? (Finish any file operation in progress first.)",
                      "WON이 실행 중이라 닫아야 업데이트할 수 있습니다. 닫고 계속할까요? (진행 중인 파일 작업이 있다면 먼저 끝내 주세요.)"),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
                if (!yes) throw new InstallException(T("Close WON and run the installer again.", "WON을 닫고 설치를 다시 실행해 주세요."));
                closeApp = true;
            }

            SetStatus(T("Installing WON…", "WON을 설치하는 중…"));
            return Installer.InstallPackage(msix, closeApp);
        }
    }
}
