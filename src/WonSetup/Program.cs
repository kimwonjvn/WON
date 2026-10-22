using System;
using System.Windows.Forms;

namespace WonSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Elevated helper: "WON_Setup.exe --trust <file.cer>" (started by the installer itself, shows no UI).
            if (args.Length >= 2 && args[0] == "--trust")
                return Installer.TrustHelper(args[1]);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }
    }
}
