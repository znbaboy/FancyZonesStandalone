using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FancyZonesStandalone
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _autoUpdate, _startup, _status;
        private readonly Timer _updateTimer, _watchdog;
        private readonly Control _ui = new Control(); // marshals background results onto the UI thread
        private UpdateInfo _pending;
        private bool _busy;

        public TrayApp(bool showMenu)
        {
            _ui.CreateControl();
            var v = PackageVersion.Load();

            var menu = new ContextMenuStrip();
            _status = new ToolStripMenuItem("FancyZones " + v.PowerToys + "  (build " + v.Build + ")") { Enabled = false };
            menu.Items.Add(_status);
            menu.Items.Add(new ToolStripSeparator());
            var editor = new ToolStripMenuItem("Open layout editor", null, (s, e) => FancyZones.OpenEditor()) { Font = new Font(menu.Font, FontStyle.Bold) };
            menu.Items.Add(editor);
            menu.Items.Add("Restart FancyZones", null, (s, e) => Safe(FancyZones.Restart));
            menu.Items.Add("Edit settings file", null, (s, e) => Safe(() => Process.Start("notepad.exe", "\"" + Paths.FzSettings + "\"")));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Export layouts && settings…", null, (s, e) => Export());
            menu.Items.Add("Import layouts && settings…", null, (s, e) => Import());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Check for updates", null, async (s, e) => await CheckForUpdates(manual: true));
            _autoUpdate = new ToolStripMenuItem("Update automatically", null, (s, e) => { Config.AutoUpdate = !Config.AutoUpdate; Refresh(); });
            _startup = new ToolStripMenuItem("Start with Windows", null, (s, e) => { Safe(() => Startup.Enabled = !Startup.Enabled); Refresh(); });
            menu.Items.Add(_autoUpdate);
            menu.Items.Add(_startup);
            menu.Items.Add("Open app folder", null, (s, e) => Process.Start("explorer.exe", "\"" + Paths.AppDir + "\""));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());
            menu.Opening += (s, e) => Refresh();

            Icon ico = null;
            try { if (File.Exists(Paths.FancyZonesExe)) ico = Icon.ExtractAssociatedIcon(Paths.FancyZonesExe); } catch { }
            _icon = new NotifyIcon
            {
                Icon = ico ?? SystemIcons.Application,
                Text = "FancyZones Standalone",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.DoubleClick += (s, e) => FancyZones.OpenEditor();
            _icon.BalloonTipClicked += async (s, e) => { if (_pending != null) await ApplyUpdate(_pending); };
            Refresh();

            // Keep FancyZones alive (e.g. after a crash) while the tray app runs.
            _watchdog = new Timer { Interval = 15000 };
            _watchdog.Tick += (s, e) => { if (!_busy) Safe(FancyZones.Start, quiet: true); };
            _watchdog.Start();

            // Update check: shortly after start, then every 6 hours.
            _updateTimer = new Timer { Interval = 30000 };
            _updateTimer.Tick += async (s, e) => { _updateTimer.Interval = 6 * 60 * 60 * 1000; await CheckForUpdates(manual: false); };
            _updateTimer.Start();

            if (showMenu) // used by CI to take README screenshots
            {
                var p = Screen.PrimaryScreen.WorkingArea;
                menu.Show(new Point(p.Right - 320, p.Bottom - 420));
            }
        }

        private void Refresh()
        {
            _autoUpdate.Checked = Config.AutoUpdate;
            try { _startup.Checked = Startup.Enabled; } catch { }
            var v = PackageVersion.Load();
            _status.Text = (FancyZones.IsRunning ? "● Running" : "○ Stopped") + "  ·  FancyZones " + v.PowerToys + " (build " + v.Build + ")";
        }

        private async Task CheckForUpdates(bool manual)
        {
            if (_busy) return;
            if (!manual && !Config.AutoUpdate && _pending != null) return;
            try
            {
                var info = await Task.Run(Updater.CheckAsync);
                if (info == null)
                {
                    if (manual) _icon.ShowBalloonTip(3000, "FancyZones Standalone", "You're on the latest version.", ToolTipIcon.Info);
                    return;
                }
                _pending = info;
                if (Config.AutoUpdate && !manual) { await ApplyUpdate(info); return; }
                if (manual)
                {
                    var r = MessageBox.Show("Version " + info.Tag + " is available.\n\nInstall it now? FancyZones will restart; your layouts are kept.",
                        "FancyZones Standalone", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (r == DialogResult.Yes) await ApplyUpdate(info);
                }
                else
                {
                    _icon.ShowBalloonTip(8000, "FancyZones update available", info.Tag + " — click to install.", ToolTipIcon.Info);
                }
            }
            catch (Exception ex)
            {
                if (manual) MessageBox.Show("Couldn't check for updates:\n" + ex.Message, "FancyZones Standalone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task ApplyUpdate(UpdateInfo info)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _icon.ShowBalloonTip(3000, "FancyZones Standalone", "Installing " + info.Tag + "…", ToolTipIcon.Info);
                await Task.Run(() => Updater.ApplyAsync(info));
                ExitApp(stopFancyZones: false); // the new tray app is already starting
            }
            catch (Exception ex)
            {
                _busy = false;
                Safe(FancyZones.Start, quiet: true);
                MessageBox.Show("Update failed:\n" + ex.Message, "FancyZones Standalone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Export()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "Export FancyZones layouts & settings",
                Filter = "FancyZones backup (*.zip)|*.zip",
                FileName = "FancyZones-backup-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    var n = Backup.Export(dlg.FileName);
                    _icon.ShowBalloonTip(4000, "Export complete", n + " files saved to " + Path.GetFileName(dlg.FileName), ToolTipIcon.Info);
                }
                catch (Exception ex) { MessageBox.Show("Export failed:\n" + ex.Message, "FancyZones Standalone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void Import()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Import FancyZones layouts & settings",
                Filter = "FancyZones backup (*.zip)|*.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    var files = Backup.Inspect(dlg.FileName);
                    var ok = MessageBox.Show("Import these files and replace your current ones?\n\n  • " + string.Join("\n  • ", files) +
                                             "\n\nYour current setup is backed up first.", "Import FancyZones backup",
                                             MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (ok != DialogResult.OK) return;
                    _busy = true;
                    FancyZones.Stop();
                    var safety = Backup.Import(dlg.FileName);
                    FancyZones.Start();
                    _icon.ShowBalloonTip(5000, "Import complete",
                        safety != null ? "Previous setup saved to " + Path.GetFileName(safety) : "Layouts imported.", ToolTipIcon.Info);
                }
                catch (Exception ex) { MessageBox.Show("Import failed:\n" + ex.Message, "FancyZones Standalone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                finally { _busy = false; Safe(FancyZones.Start, quiet: true); }
            }
        }

        private void ExitApp(bool stopFancyZones = true)
        {
            _watchdog.Stop();
            _updateTimer.Stop();
            if (stopFancyZones) Safe(FancyZones.Stop, quiet: true);
            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        }

        private static void Safe(Action a, bool quiet = false)
        {
            try { a(); }
            catch (Exception ex) { if (!quiet) MessageBox.Show(ex.Message, "FancyZones Standalone", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
