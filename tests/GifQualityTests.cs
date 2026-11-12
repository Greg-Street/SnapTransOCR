using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class GifQualityTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            check(GifQuality.Find(null).Id == "compact" && GifQuality.Find("future-invalid").Id == "compact", "missing or unsupported GIF quality defaults to the smaller compact preset");
            check(GifQuality.Find("compact").OutputSize(new Size(1920, 1080)) == new Size(960, 540) && GifQuality.Find("compact").FramesPerSecond == 6, "default GIF quality caps a full-HD region at 960 by 540 and 6 frames per second");
            check(GifQuality.Find("balanced").OutputSize(new Size(3840, 2160)) == new Size(1280, 720) && GifQuality.Find("balanced").FramesPerSecond == 10, "balanced GIF quality limits output size independently of the selected physical region");
            check(GifQuality.Find("clear").OutputSize(new Size(2160, 3840)) == new Size(1080, 1920) && GifQuality.Find("clear").FramesPerSecond == 15, "clear GIF quality preserves portrait aspect ratio and increases capture rate");
            check(GifQuality.Find("original").OutputSize(new Size(3840, 2160)) == new Size(3840, 2160), "original GIF quality retains physical source dimensions");
            foreach (var profile in GifQuality.Presets)
                check(profile.OutputSize(new Size(160, 100)) == new Size(160, 100), "small GIF regions are never enlarged by " + profile.Id);
            using (var source = new Bitmap(1920, 1080))
            {
                using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.RoyalBlue);
                using (var scaled = GifQuality.Find("compact").Resize(source))
                    check(scaled.Size == new Size(960, 540) && scaled.GetPixel(0, 0).B > 150 && scaled.GetPixel(959, 539).B > 150, "scaled GIF frames retain edge colors without transparent or black borders");
            }
            string path = Path.Combine(Path.GetTempPath(), "SnapTransOCR-quality-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{}"); check(SettingsStore.Load(path).GifQuality == "compact", "upgrading settings without a GIF field selects compact quality without rewriting the file");
                var settings = new AppSettings { GifQuality = "clear", ArkApiKey = "synthetic-unmodified-key" };
                SettingsStore.Save(settings, path); var loaded = SettingsStore.Load(path);
                check(loaded.GifQuality == "clear" && loaded.ArkApiKey == settings.ArkApiKey && loaded.Hotkeys.SequenceEqual(settings.Hotkeys), "GIF preference round-trips while preserving existing credentials and shortcuts");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                AppSettings saved = null;
                using (var app = new DesktopApp(new AppSettings { GifQuality = "compact", HistoryEnabled = false, Hotkeys = new string[9] }, true, persistSettings: next => saved = next.Copy()))
                {
                    typeof(DesktopApp).GetMethod("ShowSettings", Private).Invoke(app, null);
                    var current = (AppSettings)typeof(DesktopApp).GetField("settings", Private).GetValue(app); current.GifQuality = "clear";
                    var window = (SettingsForm)typeof(DesktopApp).GetField("settingsWindow", Private).GetValue(app);
                    typeof(SettingsForm).GetMethod("Save", Private).Invoke(window, null);
                    check(saved != null && saved.GifQuality == "clear", "saving an already-open settings window does not overwrite a live GIF quality choice");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        internal static async Task Record(Action<bool, string> check)
        {
            string remembered = null; int saves = 0; Size result = Size.Empty;
            using (var form = new GifRecorderForm(new Rectangle(-30000, -30000, 1600, 900),
                capture: region => { var image = new Bitmap(region.Width, region.Height); using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Coral); return image; },
                save: source => { using (var image = Image.FromFile(source)) result = image.Size; saves++; return false; }, initialQuality: "balanced", rememberQuality: value => remembered = value))
            {
                form.Show(); var quality = (ComboBox)typeof(GifRecorderForm).GetField("quality", Private).GetValue(form);
                check(quality.SelectedIndex == 1 && remembered == null, "opening GIF controls restores the saved preset without writing configuration");
                quality.SelectedIndex = 0; form.StartRecording();
                var timer = (System.Windows.Forms.Timer)typeof(GifRecorderForm).GetField("timer", Private).GetValue(form); timer.Stop();
                await (Task)typeof(GifRecorderForm).GetField("pending", Private).GetValue(form); await Task.Yield();
                check(remembered == "compact" && !quality.Enabled && timer.Interval == 167, "starting recording remembers compact quality and locks output dimensions with its selected frame interval");
                await form.FinishRecording();
                check(result == new Size(960, 540) && quality.Enabled && saves == 1, "recording exports the scaled GIF and unlocks quality after cancelling save");
                quality.SelectedIndex = 3; form.StartRecording(); timer.Stop();
                await (Task)typeof(GifRecorderForm).GetField("pending", Private).GetValue(form); await Task.Yield(); await form.FinishRecording();
                check(result == new Size(1600, 900) && remembered == "original" && saves == 2, "the same recorder can switch back to original dimensions for a subsequent recording");
                form.Close();
            }
        }
    }
}
