// TrayTheme.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Light and dark menu colours that follow the Windows
//           taskbar theme (Settings > Personalization > Colors > "Choose your
//           default Windows mode"), switched live when the theme changes.

using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RtcCallMonitor.Tray
{
    internal static class TrayTheme
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        /// <summary>
        /// The tray menu belongs to the taskbar, so it follows the Windows mode
        /// (SystemUsesLightTheme), not the app mode.
        /// </summary>
        public static bool IsDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("SystemUsesLightTheme") is int light && light == 0;
            }
            catch
            {
                return false;
            }
        }

        public static void Apply(ContextMenuStrip menu)
        {
            var dark = IsDark() && !SystemInformation.HighContrast;
            var renderer = new Renderer(dark ? (ProfessionalColorTable)new DarkColors() : new LightColors(), dark);
            menu.Renderer = renderer;
            ApplyTo(menu.Items, renderer, dark);
        }

        private static void ApplyTo(ToolStripItemCollection items, ToolStripRenderer renderer, bool dark)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor = dark ? Color.FromArgb(240, 240, 240) : SystemColors.MenuText;
                if (item is ToolStripMenuItem mi && mi.HasDropDownItems)
                {
                    mi.DropDown.Renderer = renderer;
                    ApplyTo(mi.DropDownItems, renderer, dark);
                }
            }
        }

        private sealed class Renderer : ToolStripProfessionalRenderer
        {
            private readonly bool _dark;

            public Renderer(ProfessionalColorTable colors, bool dark) : base(colors)
            {
                _dark = dark;
                RoundedEdges = false;
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled)
                {
                    e.TextColor = _dark ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText;
                }
                else
                {
                    e.TextColor = _dark ? Color.FromArgb(240, 240, 240) : SystemColors.MenuText;
                }
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = _dark ? Color.FromArgb(240, 240, 240) : SystemColors.MenuText;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // draw our own tick so it shows on the dark background
                var r = new Rectangle(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2, e.ImageRectangle.Width - 4, e.ImageRectangle.Height - 4);
                using var pen = new Pen(_dark ? Color.FromArgb(240, 240, 240) : SystemColors.MenuText, 2);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, new[]
                {
                    new Point(r.Left + r.Width / 6, r.Top + r.Height / 2),
                    new Point(r.Left + r.Width / 2 - 1, r.Bottom - r.Height / 5),
                    new Point(r.Right - r.Width / 8, r.Top + r.Height / 5),
                });
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly Color Back = Color.FromArgb(43, 43, 43);
            private static readonly Color Hover = Color.FromArgb(65, 65, 65);
            private static readonly Color Line = Color.FromArgb(80, 80, 80);

            public override Color ToolStripDropDownBackground => Back;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Back;
            public override Color CheckBackground => Back;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }

        private sealed class LightColors : ProfessionalColorTable
        {
            private static readonly Color Back = Color.FromArgb(249, 249, 249);
            private static readonly Color Hover = Color.FromArgb(229, 229, 229);
            private static readonly Color Line = Color.FromArgb(215, 215, 215);

            public override Color ToolStripDropDownBackground => Back;
            public override Color ImageMarginGradientBegin => Back;
            public override Color ImageMarginGradientMiddle => Back;
            public override Color ImageMarginGradientEnd => Back;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Back;
            public override Color CheckBackground => Back;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }
    }
}
