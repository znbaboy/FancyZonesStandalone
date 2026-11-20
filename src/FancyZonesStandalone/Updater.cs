using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FancyZonesStandalone
{
    internal sealed class UpdateInfo
    {
        public string Tag;
        public int Build;
        public string ZipUrl;
        public string ShaUrl;
        public string PageUrl;
        public string Notes;
    }

    /// <summary>Checks GitHub releases of this repo and swaps the package in place.</summary>
    internal static class Updater
    {
        public const string Repo = "znbaboy/FancyZonesStandalone";

        private static readonly HttpClient Http;

        static Updater()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("FancyZonesStandalone-Updater");
        }

        /// <summary>Returns the latest release if it's newer than the installed build, else null.</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            var json = await Http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest").ConfigureAwait(false);
            var rel = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var info = new UpdateInfo
            {
                Tag = Convert.ToString(rel["tag_name"]),
                PageUrl = Convert.ToString(rel["html_url"]),
                Notes = rel.TryGetValue("body", out var b) ? Convert.ToString(b) : "",
            };
            info.Build = PackageVersion.BuildFromTag(info.Tag);
            foreach (Dictionary<string, object> a in (System.Collections.ArrayList)rel["assets"])
            {
                var name = Convert.ToString(a["name"]);
                var url = Convert.ToString(a["browser_download_url"]);
                if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase)) info.ShaUrl = url;
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) info.ZipUrl = url;
            }
            Config.LastUpdateCheck = DateTime.UtcNow;
            var local = PackageVersion.Load();
            if (info.ZipUrl == null || info.ShaUrl == null) return null;
            return info.Build > local.Build ? info : null;
        }

        public static bool CanWriteAppDir()
        {
            try
            {
                var probe = Path.Combine(Paths.AppDir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Downloads, verifies and installs the update, then relaunches the new tray app.</summary>
        public static async Task ApplyAsync(UpdateInfo info)
        {
            if (!CanWriteAppDir())
                throw new UnauthorizedAccessException("Can't write to " + Paths.AppDir + ". Move the FancyZones folder somewhere you own (e.g. your user folder) to allow updates.");

            var work = Path.Combine(Path.GetTempPath(), "FancyZonesStandalone-update");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, "package.zip");

            using (var resp = await Http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using (var fs = File.Create(zip)) await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
            }

            var expected = (await Http.GetStringAsync(info.ShaUrl).ConfigureAwait(false)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
            string actual;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(zip))
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (actual != expected)
                throw new InvalidDataException("Downloaded update failed its SHA256 check, so it was not installed.");

            var extract = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zip, extract);
            // Packages may have a single top-level folder.
            var root = extract;
            var top = Directory.GetDirectories(extract);
            if (Directory.GetFiles(extract).Length == 0 && top.Length == 1) root = top[0];
            if (!File.Exists(Path.Combine(root, "PowerToys.FancyZones.exe")))
                throw new InvalidDataException("Update package is missing PowerToys.FancyZones.exe.");

            FancyZones.Stop();
            Install(root);

            Process.Start(new ProcessStartInfo(Path.Combine(Paths.AppDir, Path.GetFileName(Paths.ManagerExe)), "--updated")
            {
                WorkingDirectory = Paths.AppDir,
                UseShellExecute = false,
            });
        }

        private static void Install(string root)
        {
            var newFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(root.Length).TrimStart('\\', '/')).ToList();

            foreach (var rel in newFiles)
            {
                var src = Path.Combine(root, rel);
                var dest = Path.Combine(Paths.AppDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                try { File.Copy(src, dest, true); }
                catch (IOException)
                {
                    // In use (this exe, or a DLL it loaded): Windows allows renaming a running file.
                    MoveAside(dest);
                    File.Copy(src, dest, true);
                }
                catch (UnauthorizedAccessException)
                {
                    MoveAside(dest);
                    File.Copy(src, dest, true);
                }
            }

            // Remove files that the previous package had but the new one doesn't.
            if (File.Exists(Paths.ManifestFile))
            {
                var keep = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
                foreach (var rel in File.ReadAllLines(Paths.ManifestFile).Select(l => l.Trim()).Where(l => l.Length > 0))
                {
                    if (keep.Contains(rel) || rel.Contains("..")) continue;
                    var path = Path.Combine(Paths.AppDir, rel);
                    try { if (File.Exists(path)) File.Delete(path); } catch { try { MoveAside(path); } catch { } }
                }
            }
        }

        private static void MoveAside(string path)
        {
            if (File.Exists(path)) File.Move(path, path + ".old-" + DateTime.UtcNow.Ticks);
        }

        /// <summary>Deletes files left behind by MoveAside after a previous update.</summary>
        public static void CleanupOldFiles()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Paths.AppDir, "*.old-*", SearchOption.AllDirectories))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
