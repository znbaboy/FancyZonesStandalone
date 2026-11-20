using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace FancyZonesStandalone
{
    internal static class Program
    {
        // Usage:
        //   FancyZonesStandalone.exe                 tray app (starts FancyZones)
        //   FancyZonesStandalone.exe --export x.zip  export layouts & settings, then exit
        //   FancyZonesStandalone.exe --import x.zip  import layouts & settings, then exit
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string Arg(string name)
            {
                var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

            try
            {
                if (Arg("--export") is string exp)
                {
                    Backup.Export(Path.GetFullPath(exp));
                    return 0;
                }
                if (Arg("--import") is string imp)
                {
                    var running = FancyZones.IsRunning;
                    if (running) FancyZones.Stop();
                    Backup.Import(Path.GetFullPath(imp));
                    if (running) FancyZones.Start();
                    return 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "FancyZones Standalone", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            using (var mutex = new Mutex(false, @"Local\FancyZonesStandalone-Tray"))
            {
                // After an update the old instance is still exiting, so wait a bit for it.
                if (!mutex.WaitOne(Has("--updated") ? 15000 : 0))
                {
                    if (!Has("--background")) FancyZones.OpenEditor(); // second launch = open editor
                    return 0;
                }

                Updater.CleanupOldFiles();
                if (!File.Exists(Paths.FancyZonesExe))
                {
                    MessageBox.Show("PowerToys.FancyZones.exe was not found next to this app.\nExtract the whole zip into one folder.",
                        "FancyZones Standalone", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                try { FancyZones.EnsureSettings(); } catch { }
                var firstRun = !File.Exists(Paths.ConfigFile);
                if (firstRun) { try { Startup.Enabled = true; } catch { } Config.AutoUpdate = true; }
                FancyZones.Start();

                Application.Run(new TrayApp(showMenu: Has("--show-menu")));
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }
}
