using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace FancyZonesStandalone
{
    internal static class Paths
    {
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        public static readonly string ManagerExe = Assembly.GetExecutingAssembly().Location;
        public static readonly string FancyZonesExe = Path.Combine(AppDir, "PowerToys.FancyZones.exe");
        public static readonly string EditorExe = Path.Combine(AppDir, "PowerToys.FancyZonesEditor.exe");
        public static readonly string VersionFile = Path.Combine(AppDir, "version.json");
        public static readonly string ManifestFile = Path.Combine(AppDir, "manifest.txt");

        public static readonly string PowerToysData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys");
        public static readonly string FzData = Path.Combine(PowerToysData, "FancyZones");
        public static readonly string FzSettings = Path.Combine(FzData, "settings.json");
        public static readonly string BackupDir = Path.Combine(FzData, "standalone-backups");

        public static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FancyZonesStandalone");
        public static readonly string ConfigFile = Path.Combine(ConfigDir, "config.ini");
    }

    /// <summary>Package version written by CI into version.json next to the exe.</summary>
    internal sealed class PackageVersion
    {
        public string Tag = "dev";          // e.g. v0.102.3001-b12
        public string PowerToys = "?";      // e.g. 0.102.3001
        public int Build;                   // monotonically increasing CI build number

        public static PackageVersion Load()
        {
            var v = new PackageVersion();
            try
            {
                if (!File.Exists(Paths.VersionFile)) return v;
                var json = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(Paths.VersionFile));
                if (json.TryGetValue("tag", out var t)) v.Tag = Convert.ToString(t);
                if (json.TryGetValue("powertoys", out var p)) v.PowerToys = Convert.ToString(p);
                if (json.TryGetValue("build", out var b)) v.Build = Convert.ToInt32(b);
            }
            catch { }
            return v;
        }

        /// <summary>Build number from a tag like v0.102.3001-b12 (0 if absent).</summary>
        public static int BuildFromTag(string tag)
        {
            var m = System.Text.RegularExpressions.Regex.Match(tag ?? "", @"-b(\d+)$");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
    }

    /// <summary>Tiny key=value config in %LOCALAPPDATA%\FancyZonesStandalone.</summary>
    internal static class Config
    {
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static Config()
        {
            try
            {
                if (File.Exists(Paths.ConfigFile))
                    foreach (var line in File.ReadAllLines(Paths.ConfigFile))
                    {
                        var i = line.IndexOf('=');
                        if (i > 0) Values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch { }
        }

        public static bool AutoUpdate { get => Get("autoUpdate", true); set => Set("autoUpdate", value); }
        public static DateTime LastUpdateCheck
        {
            get => DateTime.TryParse(Values.TryGetValue("lastUpdateCheck", out var s) ? s : "", null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue;
            set { Values["lastUpdateCheck"] = value.ToString("o"); Save(); }
        }

        private static bool Get(string key, bool def) =>
            Values.TryGetValue(key, out var s) && bool.TryParse(s, out var b) ? b : def;

        private static void Set(string key, bool value) { Values[key] = value.ToString(); Save(); }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.ConfigDir);
                File.WriteAllLines(Paths.ConfigFile, Values.Select(kv => kv.Key + "=" + kv.Value));
            }
            catch { }
        }
    }

    internal static class Startup
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        // Registry value names are case-insensitive, so this also replaces the old Configure.ps1
        // entry ("FancyZones StandAlone") that started PowerToys.FancyZones.exe directly.
        private const string Name = "FancyZones Standalone";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(Name) is string s && s.IndexOf(Paths.ManagerExe, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(Name, "\"" + Paths.ManagerExe + "\" --background");
                    else k.DeleteValue(Name, false);
                }
            }
        }
    }

    /// <summary>Start/stop PowerToys.FancyZones.exe and talk to it through its named events.</summary>
    internal static class FancyZones
    {
        // From src/common/interop/shared_constants.h in microsoft/PowerToys.
        private const string ExitEvent = @"Local\PowerToys-FZE-ExitEvent-ca8c73de-a52c-4274-b691-46e9592d3b43";
        private const string ToggleEditorEvent = @"Local\FancyZones-ToggleEditorEvent-1e174338-06a3-472b-874d-073b21c62f14";

        public static Process[] Running() => Process.GetProcessesByName("PowerToys.FancyZones");
        public static bool IsRunning => Running().Length > 0;

        public static void Start()
        {
            if (IsRunning || !File.Exists(Paths.FancyZonesExe)) return;
            // FancyZones takes a parent PID on its command line and exits when that process does,
            // so it never outlives the tray app (same behaviour as the PowerToys runner).
            Process.Start(new ProcessStartInfo(Paths.FancyZonesExe, Process.GetCurrentProcess().Id.ToString())
            {
                WorkingDirectory = Paths.AppDir,
                UseShellExecute = false,
            });
        }

        public static void Stop()
        {
            SignalEvent(ExitEvent);
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(150);
            foreach (var p in Running()) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            foreach (var p in Process.GetProcessesByName("PowerToys.FancyZonesEditor")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        }

        public static void Restart() { Stop(); Start(); }

        /// <summary>Ask FancyZones to open the editor (same as Win+Shift+`), so it writes fresh monitor info first.</summary>
        public static void OpenEditor()
        {
            if (!IsRunning) { Start(); Thread.Sleep(1500); }
            if (!SignalEvent(ToggleEditorEvent) && File.Exists(Paths.EditorExe))
                Process.Start(new ProcessStartInfo(Paths.EditorExe) { WorkingDirectory = Paths.AppDir });
        }

        private static bool SignalEvent(string name)
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(name)) { ev.Set(); return true; }
            }
            catch { return false; }
        }

        /// <summary>Write the files FancyZones needs if they don't exist yet (replaces the old Configure.ps1).</summary>
        public static void EnsureSettings()
        {
            Directory.CreateDirectory(Paths.FzData);
            if (!File.Exists(Paths.FzSettings))
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DefaultSettings.json"))
                using (var r = new StreamReader(s))
                    File.WriteAllText(Paths.FzSettings, r.ReadToEnd(), new UTF8Encoding(false));
            }
            var log = Path.Combine(Paths.PowerToysData, "log_settings.json");
            if (!File.Exists(log)) File.WriteAllText(log, "{\"logLevel\":\"warn\"}", new UTF8Encoding(false));
        }
    }
}
