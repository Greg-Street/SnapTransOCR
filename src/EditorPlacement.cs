using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class EditorPlacement
    {
        internal static void Place(EditorForm editor, Form settings)
        {
            editor.StartPosition = FormStartPosition.Manual;
            if (Ui.RenderOffscreen) { editor.Location = new Point(-30000, -30000); return; }
            // Keep the hidden window wholly on the target monitor before its initial DPI pass.
            Rectangle work = settings == null ? Screen.FromPoint(Cursor.Position).WorkingArea : Screen.FromControl(settings).WorkingArea;
            editor.MinimumSize = new Size(Math.Min(editor.MinimumSize.Width, work.Width), Math.Min(editor.MinimumSize.Height, work.Height));
            editor.Bounds = new Rectangle(work.Location, new Size(Math.Min(editor.Width, work.Width), Math.Min(editor.Height, work.Height)));
            IntPtr handle = editor.Handle;
            editor.MinimumSize = new Size(Math.Min(editor.MinimumSize.Width, work.Width), Math.Min(editor.MinimumSize.Height, work.Height));
            if (settings == null)
            {
                Size size = editor.PreferredImageSize(work.Size);
                editor.Bounds = new Rectangle(work.Left + (work.Width - size.Width) / 2, work.Top + (work.Height - size.Height) / 2, size.Width, size.Height);
            }
            else editor.Bounds = Calculate(work, settings.Bounds, editor.MinimumSize, Ui.Scale(editor, 12), editor.PreferredImageSize);
        }

        internal static Rectangle Calculate(Rectangle work, Rectangle settings, Size minimum, int gap, Func<Size, Size> fit)
        {
            int minWidth = Math.Min(minimum.Width, work.Width), minHeight = Math.Min(minimum.Height, work.Height);
            // Use free space first; retain the user's settings window position and size.
            var spaces = new[]
            {
                Rectangle.FromLTRB(Math.Min(work.Right, Math.Max(work.Left, settings.Right + gap)), work.Top, work.Right, work.Bottom),
                Rectangle.FromLTRB(work.Left, work.Top, Math.Max(work.Left, Math.Min(work.Right, settings.Left - gap)), work.Bottom),
                Rectangle.FromLTRB(work.Left, Math.Min(work.Bottom, Math.Max(work.Top, settings.Bottom + gap)), work.Right, work.Bottom),
                Rectangle.FromLTRB(work.Left, work.Top, work.Right, Math.Max(work.Top, Math.Min(work.Bottom, settings.Top - gap)))
            };
            foreach (Rectangle space in spaces)
            {
                if (space.Width < minWidth || space.Height < minHeight) continue;
                Size size = fit(space.Size);
                return new Rectangle(Clamp(settings.Left, space.Left, space.Right - size.Width), Clamp(settings.Top, space.Top, space.Bottom - size.Height), size.Width, size.Height);
            }
            // Keep the left half of settings accessible whenever the monitor can fit the minimum editor width.
            int availableWidth = Math.Min(work.Width, Math.Max(minWidth, work.Right - settings.Left - settings.Width / 2));
            Size fallback = fit(new Size(availableWidth, work.Height));
            return new Rectangle(work.Right - fallback.Width, Clamp(settings.Top + gap, work.Top, work.Bottom - fallback.Height), fallback.Width, fallback.Height);
        }

        internal static Size FitImage(Size image, Size available, Size minimum, Size chrome)
        {
            // Source dimensions are physical pixels: high DPI scales controls, never the screenshot itself.
            double scale = Math.Max(0, Math.Min(1, Math.Min((available.Width - chrome.Width) / (double)image.Width, (available.Height - chrome.Height) / (double)image.Height)));
            return new Size(Math.Min(available.Width, Math.Max(minimum.Width, chrome.Width + (int)Math.Ceiling(image.Width * scale))),
                Math.Min(available.Height, Math.Max(minimum.Height, chrome.Height + (int)Math.Ceiling(image.Height * scale))));
        }
        private static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(value, maximum)); }
    }
}
