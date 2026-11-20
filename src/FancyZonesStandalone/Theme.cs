using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FancyZonesStandalone
{
    /// <summary>Dark/light tray menu that follows the Windows app theme.</summary>
    internal static class Theme
    {
        public static bool IsDark
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
                }
                catch { return false; }
            }
        }

        public static void Apply(ContextMenuStrip menu)
        {
            var dark = IsDark;
            menu.Renderer = new ToolStripProfessionalRenderer(dark ? (ProfessionalColorTable)new DarkColors() : new LightColors()) { RoundedEdges = false };
            var fg = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(25, 25, 25);
            var dim = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);
            menu.BackColor = dark ? DarkColors.Bg : LightColors.Bg;
            foreach (ToolStripItem i in menu.Items) i.ForeColor = i.Enabled ? fg : dim;
        }

        private class DarkColors : ProfessionalColorTable
        {
            public static readonly Color Bg = Color.FromArgb(43, 43, 43);
            private static readonly Color Hover = Color.FromArgb(65, 65, 65);
            private static readonly Color Line = Color.FromArgb(70, 70, 70);
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Line;
            public override Color CheckBackground => Color.FromArgb(0, 95, 184);
            public override Color CheckSelectedBackground => Color.FromArgb(0, 103, 192);
            public override Color CheckPressedBackground => Color.FromArgb(0, 103, 192);
            public override Color ButtonSelectedBorder => Hover;
        }

        private class LightColors : ProfessionalColorTable
        {
            public static readonly Color Bg = Color.FromArgb(249, 249, 249);
            private static readonly Color Hover = Color.FromArgb(230, 230, 230);
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Color.FromArgb(215, 215, 215);
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
        }
    }
}
