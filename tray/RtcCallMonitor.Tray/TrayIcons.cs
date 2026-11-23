// TrayIcons.cs
// Version: 2.1.0
// Changelog:
//   2.1.0 - Badges get a subtle depth like the OneDrive/Microsoft 365 icons:
//           a top-left to bottom-right gradient (slightly lighter to slightly
//           darker), a soft drop shadow and a faint darker rim.
//   2.0.0 - Windows 11 style, like OneDrive and the other Microsoft 365 tray
//           icons: a monochrome Fluent headset glyph (Segoe Fluent Icons, or
//           Segoe MDL2 Assets on Windows 10) in the taskbar's colour (white on
//           a dark taskbar, near-black on a light one) with a small status
//           badge cut into the bottom-right corner: red dot = on a call,
//           grey with || = paused, amber with ! = needs attention, no badge =
//           watching. Monitor not running: the glyph is dimmed. Drawn as a
//           vector path at the exact tray size, so it stays sharp at any
//           display scaling. Falls back to the 1.x coloured dots if neither
//           icon font is installed.
//   1.0.0 - Initial version. Draws the status icons (no image files needed):
//           idle, on call, paused, problem and monitor not running.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RtcCallMonitor.Tray
{
    internal enum TrayState { Idle, OnCall, Paused, Problem, Offline }

    internal static class TrayIcons
    {
        private const string Headset = "";   // same code point in Segoe Fluent Icons and Segoe MDL2 Assets

        // Fluent status colours (light taskbar / dark taskbar)
        private static readonly Color OnCallRed = Color.FromArgb(0xD1, 0x34, 0x38);
        private static readonly Color CautionLight = Color.FromArgb(0x9D, 0x5D, 0x00);
        private static readonly Color CautionDark = Color.FromArgb(0xFC, 0xE1, 0x00);
        private static readonly Color NeutralLight = Color.FromArgb(0x5F, 0x5F, 0x5F);
        private static readonly Color NeutralDark = Color.FromArgb(0xC8, 0xC8, 0xC8);
        private static readonly Color InkLight = Color.FromArgb(0x1C, 0x1C, 0x1C);  // glyph on a light taskbar
        private static readonly Color InkDark = Color.White;                         // glyph on a dark taskbar

        private static FontFamily _iconFont;
        private static bool _iconFontLooked;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>The status icon at size × size pixels for a dark or light taskbar.</summary>
        public static Icon For(TrayState state, int size, bool darkTaskbar)
        {
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                var font = IconFont();
                if (font == null) DrawClassic(g, state, size);
                else DrawFluent(g, font, state, size, darkTaskbar);
            }

            // Icon.FromHandle doesn't own the handle: copy it and free the original
            var handle = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(handle);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        private static FontFamily IconFont()
        {
            if (_iconFontLooked) return _iconFont;
            _iconFontLooked = true;
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try
                {
                    var family = new FontFamily(name);   // throws if the font is not installed
                    _iconFont = family;
                    break;
                }
                catch (ArgumentException)
                {
                }
            }
            return _iconFont;
        }

        // ------------------------------------------------------------------ Fluent

        private static void DrawFluent(Graphics g, FontFamily font, TrayState state, int size, bool dark)
        {
            var ink = dark ? InkDark : InkLight;
            if (state == TrayState.Offline) ink = Color.FromArgb(dark ? 115 : 105, ink);  // greyed out, like a signed-out OneDrive

            using (var glyph = GlyphPath(font, Headset, size))
            using (var brush = new SolidBrush(ink))
            {
                if (glyph != null) g.FillPath(brush, glyph);
            }

            switch (state)
            {
                case TrayState.OnCall:
                    Badge(g, size, OnCallRed, null, Color.Empty);
                    break;
                case TrayState.Paused:
                    Badge(g, size, dark ? NeutralDark : NeutralLight, "pause", dark ? InkLight : Color.White);
                    break;
                case TrayState.Problem:
                    Badge(g, size, dark ? CautionDark : CautionLight, "alert", dark ? InkLight : Color.White);
                    break;
            }
        }

        /// <summary>The glyph as a path, centred and scaled to fill the icon (Fluent glyphs sit inside their em box).</summary>
        private static GraphicsPath GlyphPath(FontFamily font, string glyph, int size)
        {
            var path = new GraphicsPath();
            path.AddString(glyph, font, (int)FontStyle.Regular, size, PointF.Empty, StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.Dispose();
                return null;
            }

            var scale = size * 0.92f / Math.Max(bounds.Width, bounds.Height);
            using var m = new Matrix();
            m.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2), MatrixOrder.Append);
            m.Scale(scale, scale, MatrixOrder.Append);
            m.Translate(size / 2f, size / 2f, MatrixOrder.Append);
            path.Transform(m);
            return path;
        }

        /// <summary>A round badge in the bottom-right corner, with a transparent gap around it (Fluent badge style).</summary>
        private static void Badge(Graphics g, int size, Color fill, string mark, Color markColor)
        {
            var d = (float)Math.Round(size * 0.56);
            var badge = new RectangleF(size - d, size - d, d, d);
            var gap = Math.Max(1f, size / 16f);

            // punch the gap out of the glyph
            var old = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                g.FillEllipse(clear, RectangleF.Inflate(badge, gap, gap));
            }
            g.CompositingMode = old;

            // soft drop shadow: two offset, see-through layers read as a small blur
            var drop = Math.Max(0.75f, size / 24f);
            using (var shadow = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
            {
                g.FillEllipse(shadow, RectangleF.Inflate(new RectangleF(badge.X, badge.Y + drop, d, d), drop * 0.6f, drop * 0.6f));
                g.FillEllipse(shadow, new RectangleF(badge.X, badge.Y + drop, d, d));
            }

            // gentle gradient: light from the top left, as in Fluent icons
            using (var brush = new LinearGradientBrush(RectangleF.Inflate(badge, 1, 1), Shade(fill, 0.22f), Shade(fill, -0.14f), LinearGradientMode.ForwardDiagonal))
            {
                g.FillEllipse(brush, badge);
            }

            // faint rim so the badge keeps its edge on any wallpaper or accent colour
            using (var rim = new Pen(Color.FromArgb(70, Shade(fill, -0.45f)), Math.Max(0.75f, size / 32f)))
            {
                var inset = rim.Width / 2;
                g.DrawEllipse(rim, RectangleF.Inflate(badge, -inset, -inset));
            }

            if (mark == null) return;
            using var ink = new SolidBrush(markColor);
            var cx = badge.X + d / 2;
            var cy = badge.Y + d / 2;
            var w = Math.Max(1f, (float)Math.Round(d / 5));
            var h = (float)Math.Round(d * 0.5);

            if (mark == "pause")
            {
                var spacing = Math.Max(1f, w * 0.8f);
                g.FillRectangle(ink, cx - spacing / 2 - w, cy - h / 2, w, h);
                g.FillRectangle(ink, cx + spacing / 2, cy - h / 2, w, h);
            }
            else // alert: "!"
            {
                var bar = h * 0.62f;
                var top = cy - h / 2 - d * 0.04f;
                g.FillRectangle(ink, cx - w / 2, top, w, bar);
                g.FillRectangle(ink, cx - w / 2, top + bar + Math.Max(1f, d * 0.08f), w, w);
            }
        }

        /// <summary>amount &gt; 0 mixes towards white, &lt; 0 towards black.</summary>
        private static Color Shade(Color c, float amount)
        {
            int Mix(int v) => amount >= 0
                ? (int)Math.Round(v + (255 - v) * amount)
                : (int)Math.Round(v * (1 + amount));
            return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
        }

        // ------------------------------------------------------------------ 1.x fallback

        private static void DrawClassic(Graphics g, TrayState state, int size)
        {
            var pad = Math.Max(1, size / 16);
            var rect = new Rectangle(pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);
            switch (state)
            {
                case TrayState.OnCall: Fill(g, rect, Color.FromArgb(220, 38, 38)); break;
                case TrayState.Paused: Fill(g, rect, Color.FromArgb(217, 119, 6)); break;
                case TrayState.Problem: Fill(g, rect, Color.FromArgb(234, 88, 12)); break;
                case TrayState.Offline:
                    using (var pen = new Pen(Color.FromArgb(120, 120, 120), Math.Max(2, size / 8)))
                        g.DrawEllipse(pen, Rectangle.Inflate(rect, -size / 16, -size / 16));
                    break;
                default: Fill(g, rect, Color.FromArgb(22, 163, 74)); break;
            }
        }

        private static void Fill(Graphics g, Rectangle rect, Color color)
        {
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, rect);
        }
    }
}
