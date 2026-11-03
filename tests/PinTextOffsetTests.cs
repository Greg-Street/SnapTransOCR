using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;

namespace SnapTransOCR
{
    internal static class PinTextOffsetTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        // Pure text arithmetic. This entry does not construct a Form, Control, image or font.
        internal static void Run(Action<bool, string> check)
        {
            string[] text = { "A😀", "", "e\u0301\nZ", "终" };
            int reads = 0;
            int[] offsets = PinForm.BuildBlockOffsets(text.Length, index => { reads++; return text[index].Length; });
            string joined = String.Join(Environment.NewLine, text);
            check(offsets.SequenceEqual(new[] { 0, 5, 7, 13 }), "text block offsets count UTF-16 units and separators across empty, Unicode and multiline blocks");
            check(Enumerable.Range(0, text.Length).All(index => joined.Substring(offsets[index], text[index].Length) == text[index]), "each cached block offset addresses exactly its source substring in copied text");
            check(reads <= text.Length, "building block offsets reads text lengths in one linear pass");
            check(PinForm.BuildBlockOffsets(0, index => { throw new Exception("Unexpected empty-block access"); }).Length == 0 && PinForm.BuildBlockOffsets(1, index => 2000000).Single() == 0, "empty and single-block snapshots need no prefix text reads");
            const int count = 10000; int lengthReads = 0;
            int[] many = PinForm.BuildBlockOffsets(count, index => { lengthReads++; return 12; });
            long sum = 0;
            for (int pass = 0; pass < 10; pass++) for (int index = 0; index < many.Length; index++) sum += many[index];
            check(lengthReads <= count && sum == 10L * (12 + Environment.NewLine.Length) * count * (count - 1) / 2, "repeated full-block offset walks reuse a linear-size cache without reading preceding text");
        }

        // Requires the globally serialized UI slot. Existing PinTextTests/PinRotationTests cover actual glyph dragging and clipboard.
        internal static void RunUi(Action<bool, string> check)
        {
            var bitmap = new Bitmap(500, 250);
            using (var pin = new PinForm(bitmap, false, sourceLocation: new Point(-30000, -30000)))
            {
                var blocks = new[] { new OcrTextBlock("Original😀", new Rectangle(20, 20, 280, 30)), new OcrTextBlock("Second", new Rectangle(20, 100, 280, 30)) };
                pin.SetTextBlocks(blocks);
                int[] sourceOffsets = Field<int[]>(pin, "sourceOffsets");
                Select(pin, 0, sourceOffsets[1] + blocks[1].Text.Length);
                check(pin.SelectedImageText == "Original😀" + Environment.NewLine + "Second", "source prefix cache preserves selection and cross-block copied line breaks");
                string[] translation = { "译文", "第二段 e\u0301" };
                var layouts = ImageTranslation.Layout(bitmap, blocks, translation);
                pin.SetTranslation(new Bitmap(500, 250));
                pin.SetTextBlocks(blocks, translation, layouts);
                int[] translatedOffsets = Field<int[]>(pin, "translatedOffsets");
                int[] cachedSource = Field<int[]>(pin, "sourceOffsets");
                check(Offset(pin, 1) == translation[0].Length + Environment.NewLine.Length, "translation layer uses its own prefix offsets");
                Select(pin, 0, translatedOffsets[1] + translation[1].Length);
                check(pin.SelectedImageText == String.Join(Environment.NewLine, translation), "translation prefix cache preserves complete selected text");
                typeof(PinForm).GetMethod("ToggleOriginal", Private).Invoke(pin, null);
                check(Offset(pin, 1) == blocks[0].Text.Length + Environment.NewLine.Length && ReferenceEquals(cachedSource, Field<int[]>(pin, "sourceOffsets")), "layer toggle uses the retained source cache without recomputing offsets");
                typeof(PinForm).GetMethod("RotateImage", Private).Invoke(pin, new object[] { 1 });
                check(ReferenceEquals(translatedOffsets, Field<int[]>(pin, "translatedOffsets")) && Offset(pin, 1) == cachedSource[1], "rotation leaves text order and both prefix caches unchanged");
                pin.SetTextBlocks(new[] { new OcrTextBlock("X", Rectangle.Empty), new OcrTextBlock("Y", Rectangle.Empty) });
                check(Offset(pin, 1) == 1 + Environment.NewLine.Length && !ReferenceEquals(cachedSource, Field<int[]>(pin, "sourceOffsets")), "replacing text rebuilds offsets rather than retaining stale block lengths");
            }
        }

        internal static object[] Measure()
        {
            return new[] { 100, 1000, 5000, 10000 }.Select(count => {
                string[] text = Enumerable.Range(0, count).Select(index => new string('x', 4 + index % 29)).ToArray();
                int[] offsets = PinForm.BuildBlockOffsets(text.Length, index => text[index].Length);
                long expected = LegacySum(text), actual = CachedSum(offsets);
                if (expected != actual) throw new Exception("Prefix benchmark checksum mismatch.");
                var oldSamples = new double[7]; var cachedSamples = new double[7]; var buildSamples = new double[7];
                long checksum = 0;
                for (int sample = 0; sample < oldSamples.Length; sample++)
                {
                    var watch = Stopwatch.StartNew(); checksum ^= LegacySum(text); watch.Stop(); oldSamples[sample] = watch.Elapsed.TotalMilliseconds;
                    watch.Restart();
                    for (int repeat = 0; repeat < 100; repeat++) checksum ^= CachedSum(offsets);
                    watch.Stop(); cachedSamples[sample] = watch.Elapsed.TotalMilliseconds / 100;
                    watch.Restart();
                    for (int repeat = 0; repeat < 100; repeat++) checksum ^= PinForm.BuildBlockOffsets(text.Length, index => text[index].Length)[count - 1];
                    watch.Stop(); buildSamples[sample] = watch.Elapsed.TotalMilliseconds / 100;
                }
                GC.KeepAlive(checksum);
                return (object)new { blocks = count, legacy_length_reads_per_walk = (long)count * (count - 1) / 2, cache_length_reads_per_walk = 0,
                    legacy_walk_ms = Median(oldSamples), cached_walk_ms = Median(cachedSamples), one_time_build_ms = Median(buildSamples), checksum = expected };
            }).ToArray();
        }

        private static long LegacySum(string[] text)
        {
            long sum = 0;
            for (int index = 0; index < text.Length; index++)
            {
                int offset = 0;
                for (int previous = 0; previous < index; previous++) offset += text[previous].Length + Environment.NewLine.Length;
                sum += offset;
            }
            return sum;
        }
        private static long CachedSum(int[] offsets) { long sum = 0; for (int index = 0; index < offsets.Length; index++) sum += offsets[index]; return sum; }
        private static double Median(double[] values) { Array.Sort(values); return values[values.Length / 2]; }
        private static T Field<T>(object value, string name) { return (T)typeof(PinForm).GetField(name, Private).GetValue(value); }
        private static int Offset(PinForm pin, int index) { return (int)typeof(PinForm).GetMethod("BlockOffset", Private).Invoke(pin, new object[] { index }); }
        private static void Select(PinForm pin, int start, int end) { typeof(PinForm).GetField("selectionAnchor", Private).SetValue(pin, start); typeof(PinForm).GetField("selectionEnd", Private).SetValue(pin, end); }
    }
}
