using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace WonUninstall
{
    internal static class Program
    {
        private static readonly bool Ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
        private static string T(string en, string ko) => Ko ? ko : en;

        private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WON";
        private const string ShortcutName = "WON.lnk";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Second stage: "Uninstall.exe --run <app folder> [--purge]", started from a temp copy so the app folder can be deleted.
            if (args.Length >= 2 && args[0] == "--run")
                return Remove(args[1], args.Length > 2 && args[2] == "--purge");

            var self = Process.GetCurrentProcess().MainModule.FileName;
            var dir = Path.GetDirectoryName(self);
            if (!File.Exists(Path.Combine(dir, "Span.exe")))
            {
                MessageBox.Show(T("This must be run from the WON folder (Settings > Apps > WON > Uninstall).",
                                  "WON 폴더에서 실행해야 합니다(설정 > 앱 > WON > 제거)."), "WON");
                return 1;
            }

            using (var form = new ConfirmForm())
            {
                if (form.ShowDialog() != DialogResult.OK) return 0;
                var temp = Path.Combine(Path.GetTempPath(), "WON-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(self, temp, true);
                Process.Start(new ProcessStartInfo(temp, "--run \"" + dir + "\"" + (form.Purge ? " --purge" : "")) { UseShellExecute = false });
            }
            return 0;
        }

        private static string ImagePath(Process p)
        {
            var h = OpenProcess(0x1000, false, p.Id);
            if (h == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : string.Empty;
            }
            finally { CloseHandle(h); }
        }

        private static int Remove(string dir, bool purge)
        {
            try
            {
                dir = Path.GetFullPath(dir).TrimEnd('\\');
                // Safety: only ever delete a folder that really is a WON install.
                if (!File.Exists(Path.Combine(dir, "Span.exe")) || !File.Exists(Path.Combine(dir, "Uninstall.exe")))
                {
                    MessageBox.Show(T("That folder does not look like a WON installation, so nothing was removed.",
                                      "WON이 설치된 폴더가 아니어서 아무것도 지우지 않았습니다."), "WON");
                    return 1;
                }

                var prefix = dir + "\\";
                foreach (var name in new[] { "Span", "Span.Thumbs" })
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (!ImagePath(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                            if (name == "Span") p.CloseMainWindow();
                            if (!p.WaitForExit(8000)) p.Kill();
                        }
                        catch { }
                    }

                // Only shortcuts that really start this installation (never someone else's "WON.lnk").
                TryDeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName), dir);
                TryDeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName), dir);
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false); } catch { }

                Exception last = null;
                for (int i = 0; i < 8; i++)
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); last = null; break; }
                    catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(600); }
                }
                if (last != null)
                {
                    MessageBox.Show(T("Some files could not be removed (still in use). Close WON and try again.\n",
                                      "일부 파일을 지우지 못했습니다(사용 중). WON을 닫고 다시 시도해 주세요.\n") + last.Message, "WON");
                    return 1;
                }

                if (purge)
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    foreach (var d in new[] { "WON", "Span" })
                        try { var p = Path.Combine(local, d); if (Directory.Exists(p)) Directory.Delete(p, true); } catch { }
                }

                MessageBox.Show(T("WON has been removed.", "WON을 제거했습니다."), "WON");
                // This temp copy deletes itself once it has exited.
                var self = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & del \"" + self + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(T("Could not remove WON: ", "WON을 제거하지 못했습니다: ") + ex.Message, "WON");
                return 1;
            }
        }

        private static void TryDeleteShortcut(string path, string dir)
        {
            try
            {
                if (!File.Exists(path)) return;
                var link = (IShellLinkW)new ShellLink();
                ((IPersistFile)link).Load(path, 0);
                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                if (target.ToString().StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) File.Delete(path);
            }
            catch { }
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr data, int flags);
    }

    internal sealed class ConfirmForm : Form
    {
        private static readonly bool Ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
        private static string T(string en, string ko) => Ko ? ko : en;

        private readonly CheckBox _purge = new CheckBox();
        public bool Purge => _purge.Checked;

        public ConfirmForm()
        {
            Text = T("Remove WON", "WON 제거");
            ClientSize = new Size(460, 190);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); } catch { }

            Controls.Add(new Label
            {
                Text = T("Remove WON from this PC?", "이 PC에서 WON을 제거할까요?"),
                Font = new Font("Segoe UI Semibold", 12F), AutoSize = true, Location = new Point(24, 20)
            });
            Controls.Add(new Label
            {
                Text = T("The program and its shortcuts are removed. Your files are never touched.",
                         "프로그램과 바로가기를 지웁니다. 내 파일은 건드리지 않습니다."),
                Location = new Point(24, 56), Size = new Size(412, 36)
            });
            _purge.Text = T("Also delete settings, chat history and saved server connections",
                            "설정, 대화 기록, 저장한 서버 연결 정보도 함께 삭제");
            _purge.Location = new Point(24, 100);
            _purge.Size = new Size(412, 24);
            Controls.Add(_purge);

            var ok = new Button { Text = T("Remove", "제거"), Size = new Size(90, 30), Location = new Point(246, 142), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = T("Cancel", "취소"), Size = new Size(90, 30), Location = new Point(346, 142), DialogResult = DialogResult.Cancel };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = cancel;
            CancelButton = cancel;
        }
    }
}
