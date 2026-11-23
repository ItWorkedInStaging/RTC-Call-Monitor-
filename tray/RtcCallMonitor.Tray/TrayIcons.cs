// TrayIcons.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Draws the status icons (no image files needed):
//           idle, on call, paused, problem and monitor not running.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RtcCallMonitor.Tray
{
    internal enum TrayState { Idle, OnCall, Paused, Problem, Offline }

    internal static class TrayIcons
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon For(TrayState state, int size)
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var pad = Math.Max(1, size / 16);
                var rect = new Rectangle(pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);

                switch (state)
                {
                    case TrayState.OnCall:
                        Fill(g, rect, Color.FromArgb(220, 38, 38));       // red: on air
                        Glyph(g, rect, "●", Color.White, 0.55f);
                        break;
                    case TrayState.Paused:
                        Fill(g, rect, Color.FromArgb(217, 119, 6));       // amber
                        Glyph(g, rect, "II", Color.White, 0.5f);
                        break;
                    case TrayState.Problem:
                        Fill(g, rect, Color.FromArgb(234, 88, 12));       // orange
                        Glyph(g, rect, "!", Color.White, 0.7f);
                        break;
                    case TrayState.Offline:
                        using (var pen = new Pen(Color.FromArgb(120, 120, 120), Math.Max(2, size / 8)))
                            g.DrawEllipse(pen, Rectangle.Inflate(rect, -size / 16, -size / 16));
                        break;
                    default:
                        Fill(g, rect, Color.FromArgb(22, 163, 74));       // green: watching, no call
                        break;
                }
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

        private static void Fill(Graphics g, Rectangle rect, Color color)
        {
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, rect);
        }

        private static void Glyph(Graphics g, Rectangle rect, string text, Color color, float scale)
        {
            using var font = new Font("Segoe UI", Math.Max(6, rect.Height * scale), FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, rect, format);
        }
    }
}
