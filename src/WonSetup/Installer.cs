using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace WonSetup
{
    internal sealed class InstallException : Exception
    {
        public InstallException(string message) : base(message) { }
    }

    /// <summary>The app (a zip of the published files) and the uninstaller are embedded in this exe; nothing is downloaded.</summary>
    internal static class Payload
    {
        public static string Version =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";

        public static Stream Open(string resourceName)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (s == null) throw new InstallException("This installer is incomplete (missing " + resourceName + ").");
            return s;
        }
    }

    internal static class Log
    {
        private static readonly string PathName = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WON-setup.log");

        public static void Write(string message)
        {
            try { File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, new UTF8Encoding(true)); }
            catch { /* logging must never break the install */ }
        }
    }

    internal sealed class InstallOptions
    {
        public string Directory = Installer.DefaultDirectory;
        public bool DesktopShortcut = true;
    }

    internal static class Installer
    {
        public const string AppExeName = "Span.exe";
        public const string UninstallerName = "Uninstall.exe";
        public const string ShortcutName = "WON.lnk";
        public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WON";
        public const string MsixPackageName = "WON.FileExplorer";

        /// <summary>Per-user folder: writing here never needs administrator rights.</summary>
        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WON");

        public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);
        public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

        // ── processes ──

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static string ImagePath(Process p)
        {
            var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
            if (h == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : string.Empty;
            }
            finally { CloseHandle(h); }
        }

        private static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var d = dir.TrimEnd('\\') + "\\";
            return path.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>WON (and its thumbnail workers) that run from the given folder.</summary>
        public static List<Process> RunningFrom(string dir)
        {
            var result = new List<Process>();
            foreach (var name in new[] { "Span", "Span.Thumbs" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (IsUnder(ImagePath(p), dir)) { result.Add(p); continue; } } catch { }
                    p.Dispose();
                }
            return result;
        }

        public static void CloseRunning(string dir)
        {
            var list = RunningFrom(dir);
            foreach (var p in list) { try { if (p.ProcessName == "Span") p.CloseMainWindow(); } catch { } }
            foreach (var p in list) { try { if (!p.WaitForExit(8000)) p.Kill(); } catch { } }
            foreach (var p in list) { try { p.WaitForExit(3000); p.Dispose(); } catch { } }
        }

        // ── the old MSIX flavour of WON (needed administrator approval to install) ──

        public static bool MsixInstalled()
        {
            try
            {
                var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                return Directory.Exists(packages) && Directory.GetDirectories(packages, MsixPackageName + "_*").Length > 0;
            }
            catch { return false; }
        }

        /// <summary>True when the MSIX version already copied its settings next to the data the new version reads.</summary>
        public static bool SettingsCarryOver() =>
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WON", "localsettings.json"));

        public static bool RemoveMsix()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("Span"))
                {
                    try
                    {
                        if (ImagePath(p).IndexOf("\\WindowsApps\\" + MsixPackageName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            p.CloseMainWindow();
                            if (!p.WaitForExit(8000)) p.Kill();
                        }
                    }
                    catch { }
                }
                var r = RunPowerShell("Get-AppxPackage -Name " + MsixPackageName + " | Remove-AppxPackage");
                Log.Write("Remove-AppxPackage exit=" + r.ExitCode + " err=[" + r.Error + "]");
                return r.ExitCode == 0 && !MsixInstalled();
            }
            catch (Exception ex)
            {
                Log.Write("RemoveMsix failed: " + ex.Message);
                return false;
            }
        }

        public sealed class PsResult
        {
            public int ExitCode;
            public string Output = string.Empty;
            public string Error = string.Empty;
        }

        public static PsResult RunPowerShell(string script)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script));
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                return new PsResult { ExitCode = p.ExitCode, Output = stdout.Result.Trim(), Error = stderr.Result.Trim() };
            }
        }

        // ── install ──

        /// <summary>Unpacks the app next to its final folder first, so a failed install never leaves a half-written app behind.</summary>
        public static void Extract(string stagingDir, Action<double> progress)
        {
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);
            var root = Path.GetFullPath(stagingDir).TrimEnd('\\') + "\\";

            using (var zipStream = Payload.Open("payload.zip"))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                long total = zip.Entries.Sum(e => e.Length), done = 0;
                var buffer = new byte[1024 * 1024];
                foreach (var entry in zip.Entries)
                {
                    var rel = entry.FullName.Replace('\\', '/');
                    if (rel.EndsWith("/")) continue;
                    var target = Path.GetFullPath(Path.Combine(stagingDir, rel.Replace('/', '\\')));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InstallException("The installer contains an unexpected path: " + entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = entry.Open())
                    using (var dst = File.Create(target))
                    {
                        int n;
                        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            dst.Write(buffer, 0, n);
                            done += n;
                        }
                    }
                    progress?.Invoke(total == 0 ? 1 : (double)done / total);
                }
            }

            using (var u = Payload.Open("uninstall.exe"))
            using (var dst = File.Create(Path.Combine(stagingDir, UninstallerName)))
                u.CopyTo(dst);

            if (!File.Exists(Path.Combine(stagingDir, AppExeName)))
                throw new InstallException("This installer is incomplete (the app file is missing).");
        }

        /// <summary>Replaces the old folder with the freshly unpacked one. The app must not be running from it.</summary>
        public static void Swap(string stagingDir, string finalDir)
        {
            if (Directory.Exists(finalDir))
            {
                Exception last = null;
                for (int i = 0; i < 6; i++)
                {
                    try { Directory.Delete(finalDir, true); last = null; break; }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { last = ex; System.Threading.Thread.Sleep(500); }
                }
                if (last != null) throw new InstallException("The old WON files are still in use: " + last.Message);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(finalDir));
            Directory.Move(stagingDir, finalDir);
        }

        public static void RegisterUninstall(string dir, string version)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
            {
                var exe = Path.Combine(dir, AppExeName);
                key.SetValue("DisplayName", "WON");
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "WON");
                key.SetValue("InstallLocation", dir);
                key.SetValue("DisplayIcon", exe);
                key.SetValue("UninstallString", "\"" + Path.Combine(dir, UninstallerName) + "\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("URLInfo", "https://github.com/kimwonjvn/WON");
                try
                {
                    long size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
                }
                catch { }
            }
        }

        public static void CreateShortcut(string linkPath, string dir)
        {
            var exe = Path.Combine(dir, AppExeName);
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exe);
            link.SetWorkingDirectory(dir);
            link.SetDescription("WON - Miller Columns File Explorer");
            link.SetIconLocation(exe, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            ((IPersistFile)link).Save(linkPath, true);
        }

        /// <summary>The whole install. Everything happens inside the user's profile, so no administrator approval is involved.</summary>
        public static string Run(InstallOptions options, Action<string> status, Action<double> progress,
                                 Func<bool> confirmCloseRunning, Func<bool> removeOldMsix, bool tr)
        {
            var dir = Path.GetFullPath(options.Directory).TrimEnd('\\');
            if (dir.Length < 8 || Path.GetPathRoot(dir).TrimEnd('\\').Equals(dir, StringComparison.OrdinalIgnoreCase))
                throw new InstallException("Choose a folder, not a drive root.");

            var staging = dir + ".new";
            status(tr ? "파일을 준비하는 중…" : "Preparing files…");
            Extract(staging, p => progress?.Invoke(p));

            var running = RunningFrom(dir);
            if (running.Count > 0)
            {
                foreach (var p in running) p.Dispose();
                if (!confirmCloseRunning())
                {
                    try { Directory.Delete(staging, true); } catch { }
                    throw new InstallException(tr ? "WON을 닫고 설치를 다시 실행해 주세요." : "Close WON and run the installer again.");
                }
                CloseRunning(dir);
            }

            status(tr ? "WON을 설치하는 중…" : "Installing WON…");
            Swap(staging, dir);
            RegisterUninstall(dir, Payload.Version);

            try { CreateShortcut(StartMenuShortcut, dir); }
            catch (Exception ex) { Log.Write("start menu shortcut failed: " + ex.Message); }
            if (options.DesktopShortcut)
            {
                try { CreateShortcut(DesktopShortcut, dir); }
                catch (Exception ex) { Log.Write("desktop shortcut failed: " + ex.Message); }
            }

            if (MsixInstalled())
            {
                Log.Write("old MSIX WON found");
                try { removeOldMsix(); } catch (Exception ex) { Log.Write("removeOldMsix: " + ex.Message); }
            }

            return Path.Combine(dir, AppExeName);
        }

        public static void Launch(string exePath)
        {
            Log.Write("launch " + exePath);
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath) });
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr data, int flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relPath, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
