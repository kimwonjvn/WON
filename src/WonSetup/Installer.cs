using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WonSetup
{
    internal sealed class InstallException : Exception
    {
        public InstallException(string message) : base(message) { }
    }

    /// <summary>The MSIX and its signing certificate are embedded in this exe; nothing is downloaded.</summary>
    internal static class Payload
    {
        private static string Meta(string key) =>
            Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value ?? string.Empty;

        /// <summary>Thumbprint of the one certificate this installer is allowed to trust (set at build time).</summary>
        public static string ExpectedThumbprint => Normalize(Meta("CertThumbprint"));

        public static string Version =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";

        public static string Normalize(string thumbprint) =>
            (thumbprint ?? string.Empty).Replace(" ", string.Empty).Replace("‎", string.Empty).ToUpperInvariant();

        public static void Extract(string resourceName, string destination, Action<double> progress)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var src = asm.GetManifestResourceStream(resourceName))
            {
                if (src == null) throw new InstallException("This installer is incomplete (missing " + resourceName + ").");
                using (var dst = File.Create(destination))
                {
                    var buffer = new byte[1024 * 1024];
                    long total = src.Length, done = 0;
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, n);
                        done += n;
                        progress?.Invoke(total == 0 ? 1 : (double)done / total);
                    }
                }
            }
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

    internal static class Installer
    {
        public const string PackageName = "WON.FileExplorer";
        private const string TrustedStore = "TrustedPeople";

        public static bool IsCertificateTrusted(string thumbprint)
        {
            using (var store = new X509Store(TrustedStore, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0;
            }
        }

        /// <summary>
        /// Runs inside the elevated child process (WON_Setup.exe --trust file.cer). It only ever trusts the one certificate
        /// whose thumbprint is compiled into this exe, so it cannot be abused to trust something else.
        /// </summary>
        public static int TrustHelper(string cerPath)
        {
            try
            {
                var cert = new X509Certificate2(cerPath);
                if (Payload.Normalize(cert.Thumbprint) != Payload.ExpectedThumbprint) return 3;
                using (var store = new X509Store(TrustedStore, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(cert);
                }
                return 0;
            }
            catch
            {
                return 2;
            }
        }

        /// <summary>Shows the Windows admin prompt once and adds the certificate to Local Computer &gt; Trusted People.</summary>
        public static void TrustCertificateElevated(string cerPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--trust \"" + cerPath + "\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new InstallException("Could not register the WON certificate (code " + p.ExitCode + ").");
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new InstallException("Administrator approval was cancelled, so the certificate could not be trusted.");
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

        private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        /// <summary>Refuses to continue unless the embedded MSIX is signed by the certificate this installer trusts.</summary>
        public static void VerifyPackageSignature(string msixPath)
        {
            var r = RunPowerShell("(Get-AuthenticodeSignature -LiteralPath " + Quote(msixPath) + ").SignerCertificate.Thumbprint");
            if (Payload.Normalize(r.Output) != Payload.ExpectedThumbprint)
                throw new InstallException("The package is not signed by the expected WON certificate. Installation was stopped.");
        }

        public static bool IsAppRunning() => Process.GetProcessesByName("Span").Length > 0;

        /// <returns>Package family name, used to start the app afterwards.</returns>
        public static string InstallPackage(string msixPath, bool closeRunningApp)
        {
            var script = "$ErrorActionPreference='Stop'; try { Add-AppxPackage -Path " + Quote(msixPath) +
                         (closeRunningApp ? " -ForceApplicationShutdown" : "") +
                         " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }; " +
                         "(Get-AppxPackage -Name " + PackageName + ").PackageFamilyName";
            var r = RunPowerShell(script);
            Log.Write("Add-AppxPackage exit=" + r.ExitCode + " out=[" + r.Output + "] err=[" + r.Error + "]");
            if (r.ExitCode != 0)
                throw new InstallException(string.IsNullOrEmpty(r.Error) ? "Windows could not install the package." : r.Error);
            return r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(PackageName + "_", StringComparison.Ordinal)) ?? string.Empty;
        }

        public static void LaunchApp(string packageFamilyName)
        {
            Log.Write("launch family=[" + packageFamilyName + "]");
            if (string.IsNullOrEmpty(packageFamilyName)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + packageFamilyName + "!App") { UseShellExecute = false });
        }
    }
}
