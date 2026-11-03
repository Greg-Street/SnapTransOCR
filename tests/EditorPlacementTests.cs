using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class EditorPlacementTests
    {
        internal static void Run(Action<bool, string> check)
        {
            RunGeometry(check);
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = false;
            try
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                using (var settings = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(work.Left, work.Top, Math.Min(860, work.Width), Math.Min(580, work.Height)) })
                using (var editor = new EditorForm(new Bitmap(180, 100)))
                {
                    IntPtr handle = settings.Handle;
                    Rectangle original = settings.Bounds;
                    editor.EnableHistoryNavigation(delegate { });
                    EditorPlacement.Place(editor, settings);
                    check(editor.IsHandleCreated && !editor.Visible && work.Contains(editor.Bounds), "history editor positions its native window before first showing it within the actual monitor");
                    check(settings.Bounds == original && editor.Owner == null && !editor.TopMost, "positioning a history editor neither moves settings nor forces the editor above it");
                    check(editor.StartPosition == FormStartPosition.Manual && editor.Width == editor.MinimumSize.Width, "a small history image uses the toolbar minimum width instead of a fixed large preview");
                }
                foreach (Size image in new[] { new Size(180, 100), new Size(900, 120), new Size(120, 900), new Size(2400, 1600) })
                using (var editor = new EditorForm(new Bitmap(image.Width, image.Height)))
                {
                    Ui.RenderOffscreen = false;
                    editor.EnableHistoryNavigation(delegate { });
                    EditorPlacement.Place(editor, null);
                    Rectangle target = Screen.FromPoint(Cursor.Position).WorkingArea;
                    check(target.Contains(editor.Bounds) && editor.Size == editor.PreferredImageSize(target.Size), "ordinary editor fits the full source and its actual toolbars to the cursor monitor for " + image);
                    Size planned = editor.Size;
                    Ui.RenderOffscreen = true; editor.Location = new Point(-30000, -30000); editor.Show(); Application.DoEvents(); editor.Update();
                    check(editor.Size == planned, "initial native showing retains the image-based size for " + image);
                    foreach (Control control in editor.Controls)
                    {
                        var canvas = control as ImageCanvas;
                        if (canvas != null)
                            check(canvas.Zoom <= 1 && canvas.Zoom * image.Width + 31 <= canvas.ClientSize.Width && canvas.Zoom * image.Height + 31 <= canvas.ClientSize.Height,
                                "the whole source image remains visible at its original aspect ratio after opening " + image + " (canvas=" + canvas.ClientSize + ", zoom=" + canvas.Zoom + ")");
                        var toolbar = control as FlowLayoutPanel;
                        if (toolbar == null) continue;
                        bool contained = true;
                        foreach (Control tool in toolbar.Controls) contained &= toolbar.ClientRectangle.Contains(tool.Bounds);
                        check(contained, "wrapped " + toolbar.GetType().Name + " controls remain fully reachable for " + image);
                    }
                    editor.Close();
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        internal static void RunGeometry(Action<bool, string> check)
        {
            foreach (int dpi in new[] { 96, 144, 192, 240 })
            {
                Func<int, int> s = value => (int)Math.Round(value * dpi / 96.0);
                var work = new Rectangle(-s(500), -s(100), s(1920), s(1040));
                var preferred = new Size(s(800), s(560)); var minimum = new Size(s(720), s(420));
                Func<Size, Size> fit = space => new Size(Math.Min(space.Width, preferred.Width), Math.Min(space.Height, preferred.Height));
                var left = new Rectangle(work.Left + s(20), work.Top + s(40), s(1000), s(730));
                var right = new Rectangle(work.Right - s(1020), work.Top + s(40), s(1000), s(730));
                Rectangle result = EditorPlacement.Calculate(work, left, minimum, s(12), fit);
                check(work.Contains(result) && !result.IntersectsWith(left) && result.Left > left.Right, "history editor uses free space to the right at " + dpi + " DPI");
                result = EditorPlacement.Calculate(work, right, minimum, s(12), fit);
                check(work.Contains(result) && !result.IntersectsWith(right) && result.Right < right.Left, "history editor uses free space to the left at " + dpi + " DPI");
                var centered = new Rectangle(work.Left + s(460), work.Top + s(100), s(1000), s(730));
                result = EditorPlacement.Calculate(work, centered, minimum, s(12), fit);
                check(work.Contains(result) && result.Left >= centered.Left + centered.Width / 2, "compact history editor leaves the navigation and list visible when side by side does not fit at " + dpi + " DPI");
                var tallWork = new Rectangle(-s(1920), -s(1200), s(1920), s(2200));
                result = EditorPlacement.Calculate(tallWork, new Rectangle(tallWork.Left, tallWork.Top, tallWork.Width, s(730)), minimum, s(12), fit);
                check(tallWork.Contains(result) && result.Top >= tallWork.Top + s(742), "history editor can use vertical free space on a negative-coordinate monitor at " + dpi + " DPI");
                var small = new Rectangle(-s(900), 0, s(640), s(360));
                result = EditorPlacement.Calculate(small, small, minimum, s(12), fit);
                check(small.Contains(result) && result.Size == small.Size, "history editor stays inside a work area smaller than its usual minimum at " + dpi + " DPI");
                Size available = new Size(s(1920), s(1040)), chrome = new Size(s(46), s(150)), compactMinimum = new Size(s(560), s(320));
                foreach (Size image in new[] { new Size(1, 1), new Size(180, 100), new Size(900, 120), new Size(120, 900), new Size(10000, 1000), new Size(1000, 10000) })
                {
                    Size sized = EditorPlacement.FitImage(image, available, compactMinimum, chrome);
                    check(sized.Width <= available.Width && sized.Height <= available.Height && sized.Width >= compactMinimum.Width && sized.Height >= compactMinimum.Height,
                        "image-based editor size respects work area and toolbar minimum for " + image + " at " + dpi + " DPI");
                }
                Size narrow = EditorPlacement.FitImage(new Size(1200, 800), new Size(s(300), s(200)), compactMinimum, chrome);
                check(narrow.Width == s(300) && narrow.Height == s(200), "work area takes priority over normal minimum on a small monitor at " + dpi + " DPI");
            }
            var desktop = new Size(1920, 1040); var controls = new Size(46, 150); var smallImageMinimum = new Size(560, 320);
            check(EditorPlacement.FitImage(new Size(180, 100), desktop, smallImageMinimum, controls) == smallImageMinimum,
                "a small screenshot opens at the toolbar minimum rather than enlarging the source image");
            check(EditorPlacement.FitImage(new Size(900, 120), desktop, smallImageMinimum, controls) == new Size(946, 320),
                "a wide screenshot that fits starts at its native pixel width plus canvas margins");
            check(EditorPlacement.FitImage(new Size(10000, 1000), desktop, smallImageMinimum, controls) == new Size(1920, 338),
                "an oversized wide screenshot uses the work-area width and only the height its fitted image needs");
            check(EditorPlacement.FitImage(new Size(1000, 10000), desktop, smallImageMinimum, controls) == new Size(560, 1040),
                "a long screenshot uses the available height without forcing a wide image window");
            check(EditorPlacement.FitImage(new Size(900, 120), new Size(2880, 1560), new Size(840, 480), new Size(69, 225)) == new Size(969, 480),
                "150 percent controls do not enlarge a 900-pixel screenshot to 1350 pixels");
        }
    }
}
