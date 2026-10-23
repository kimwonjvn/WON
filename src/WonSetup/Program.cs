using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WonSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Unattended: WON_Setup.exe /S [/D=<folder>] [/NODESKTOP] [/NOLAUNCH]   (also used by the tests)
            if (args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
                return RunSilent(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }

        private static int RunSilent(string[] args)
        {
            var options = new InstallOptions();
            bool inDir = false; // an unquoted folder with spaces arrives as several arguments
            foreach (var a in args)
            {
                if (a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) { options.Directory = a.Substring(3).Trim('"'); inDir = true; continue; }
                if (a.StartsWith("/")) inDir = false;
                else if (inDir) options.Directory += " " + a.Trim('"');
                if (a.Equals("/NODESKTOP", StringComparison.OrdinalIgnoreCase)) options.DesktopShortcut = false;
            }
            Log.Write("silent install, version " + Payload.Version + ", dir " + options.Directory);
            try
            {
                var exe = Installer.Run(options, s => Log.Write(s), null, () => true, () => false, false);
                if (!args.Any(a => a.Equals("/NOLAUNCH", StringComparison.OrdinalIgnoreCase))) Installer.Launch(exe);
                Log.Write("silent install finished");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("silent install failed: " + ex);
                return 1;
            }
        }
    }
}
