using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern IntPtr GetOpenClipboardWindow();
        [DllImport("user32.dll")] internal static extern int CountClipboardFormats();
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
        [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFO
        {
            internal uint Size; internal int Width, Height; internal ushort Planes, BitCount;
            internal uint Compression, SizeImage; internal int XPels, YPels; internal uint ClrUsed, ClrImportant, Color;
        }
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RECT rectangle, int size);
        internal delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } } }
        [DllImport("user32.dll")] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] internal static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public Point Point; public uint Mouse, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT Keyboard;
            [FieldOffset(0)] public MOUSEINPUT Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int X, Y; public uint Mouse, Flags, Time; public UIntPtr Extra; }

        internal static float MonitorScale(Point point)
        {
            int scale;
            return GetScaleFactorForMonitor(MonitorFromPoint(point, 2), out scale) == 0 ? scale / 100f : 1f;
        }
        internal static bool ModifiersDown()
        {
            return GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0 || GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x5b) < 0 || GetAsyncKeyState(0x5c) < 0;
        }
        internal static void CopyKey()
        {
            var keys = new[] { Key(0x11, false), Key(0x43, false), Key(0x43, true), Key(0x11, true) };
            if (SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT))) != keys.Length)
                throw new InvalidOperationException("无法向当前窗口发送复制操作，请使用同等权限运行，或先复制文字再粘贴翻译。");
        }
        private static INPUT Key(ushort key, bool up)
        {
            return new INPUT { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { Key = key, Flags = up ? 2u : 0u } } };
        }
    }

    internal sealed class Hotkey
    {
        public uint Modifiers;
        public Keys Key;
        public static Hotkey Parse(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            var result = new Hotkey();
            foreach (string raw in value.Split('+'))
            {
                string part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) result.Modifiers |= 2;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) result.Modifiers |= 1;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) result.Modifiers |= 4;
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) result.Modifiers |= 8;
                else
                {
                    Keys key;
                    if (part.Length == 1 && part[0] >= '0' && part[0] <= '9') part = "D" + part;
                    if (result.Key != Keys.None || !Enum.TryParse<Keys>(part, true, out key) || (int)key < 8 || (int)key > 254 || key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey)
                        throw new FormatException("快捷键格式不正确：" + value + "。示例：Ctrl+Alt+A。");
                    result.Key = key;
                }
            }
            if ((result.Modifiers & 11) == 0 || result.Key == Keys.None) throw new FormatException("快捷键需包含 Ctrl、Alt 或 Win 和一个按键。");
            return result;
        }
        public static void Validate(string[] values)
        {
            var used = new HashSet<string>();
            foreach (string value in values)
            {
                Hotkey hotkey = Parse(value);
                if (hotkey != null && !used.Add(hotkey.Modifiers + ":" + (int)hotkey.Key)) throw new FormatException("各项操作的快捷键不能重复。");
            }
        }
    }

    internal static class Clip
    {
        public static async Task SetText(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            await Retry(delegate { Clipboard.SetText(text); });
        }
        public static async Task SetImage(Image image)
        {
            await Retry(delegate { Clipboard.SetImage(image); });
        }
        private static async Task Retry(Action action)
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    using (ClipboardRecording.Suppress()) { action(); ClipboardRecording.Ignore(Native.GetClipboardSequenceNumber()); }
                    return;
                }
                catch (ExternalException) { if (i == 5) throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后再试。"); }
                await Task.Delay(45);
            }
        }
    }

    internal sealed class SelectionReader
    {
        private Task<string> automation;
        private async Task<string> ReadAutomationSelection(IntPtr foreground)
        {
            // A stalled accessibility provider must not create an unbounded queue of worker threads.
            if (automation != null && !automation.IsCompleted) return null;
            automation = Task.Run(delegate
            {
                try
                {
                    if (Native.GetForegroundWindow() != foreground) return "";
                    AutomationElement focused = AutomationElement.FocusedElement;
                    if (focused == null || focused.Current.IsPassword) return "";
                    object pattern;
                    // null means accessibility is unavailable; empty means a confirmed empty selection.
                    if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                    {
                        // Chromium can focus a non-text group while its Document owns the selection.
                        for (int depth = 0; depth < 12 && focused.Current.ControlType != ControlType.Window; depth++)
                        {
                            focused = TreeWalker.ControlViewWalker.GetParent(focused);
                            if (focused == null) break;
                            if (focused.Current.ControlType == ControlType.Document && focused.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) break;
                        }
                        if (pattern == null) return null;
                    }
                    var ranges = ((TextPattern)pattern).GetSelection();
                    if (ranges == null) return null;
                    if (ranges.Length == 0) return "";
                    return ranges[0].GetText(100001).Trim();
                }
                catch (Exception) { return null; }
            });
            Task<string> pending = automation;
            if (await Task.WhenAny(pending, Task.Delay(600)) != pending) return null;
            return await pending;
        }

        public async Task<string> ReadExplicit(IntPtr foreground)
        {
            string selected = await ReadAutomationSelection(foreground);
            if (Native.GetForegroundWindow() != foreground) return "";
            // Do not send Ctrl+C for a known empty selection: some editors copy the entire current line.
            if (selected != null) return selected;
            return await ReadCopy(foreground);
        }
        public async Task<string> TryReadExplicit(IntPtr foreground)
        {
            try { return await ReadExplicit(foreground); }
            catch (InvalidOperationException) { return ""; }
            catch (ExternalException) { return ""; }
        }
        internal async Task<string> ReadCopy(IntPtr foreground)
        {
            for (int i = 0; i < 80 && Native.ModifiersDown(); i++) await Task.Delay(15);
            if (Native.ModifiersDown()) throw new InvalidOperationException("请松开快捷键后重试。");
            if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("当前窗口已切换，请重新选中文字。");
            ClipboardSnapshot previous = null;
            uint before = Native.GetClipboardSequenceNumber();
            // Windows IDataObject can read the *current* clipboard; materialize all formats before Ctrl+C.
            for (int i = 0; i < 6; i++)
            {
                if (Native.GetForegroundWindow() != foreground || Native.GetClipboardSequenceNumber() != before) return "";
                try { previous = ClipboardSnapshot.Capture(); break; }
                catch (ExternalException) { await Task.Delay(25); }
            }
            if (previous == null) return "";
            using (previous)
            using (ClipboardRecording.Suppress())
            {
            if (Native.GetForegroundWindow() != foreground || Native.GetClipboardSequenceNumber() != before) return "";
            uint copied = before;
            int readAttempts = 0;
            try
            {
                Native.CopyKey();
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(25);
                    uint current = Native.GetClipboardSequenceNumber();
                    if (copied == before) { copied = current; if (copied != before) ClipboardRecording.Ignore(copied); }
                    else if (current != copied) return "";
                    if (copied != before)
                    {
                        if (++readAttempts > 10) return "";
                        if (Native.GetForegroundWindow() != foreground) return "";
                        if (Native.GetOpenClipboardWindow() != IntPtr.Zero) continue;
                        try
                        {
                            string text = Clipboard.GetText().Trim();
                            if (Native.GetClipboardSequenceNumber() != copied) return "";
                            // WinForms can return an empty string instead of throwing while another reader holds the clipboard.
                            if (text.Length > 0) return text;
                        }
                        catch (ExternalException) { continue; }
                    }
                }
                return "";
            }
            finally
            {
                if (copied != before && Native.GetClipboardSequenceNumber() == copied)
                {
                    // Restore only our temporary copy; never overwrite a newer clipboard change.
                    for (int i = 0; i < 6; i++)
                    {
                        try
                        {
                            if (Native.GetClipboardSequenceNumber() != copied) break;
                            if (previous.Data != null) Clipboard.SetDataObject(previous.Data, true);
                            else Clipboard.Clear();
                            ClipboardRecording.Ignore(Native.GetClipboardSequenceNumber());
                            break;
                        }
                        catch (ExternalException) { await Task.Delay(40); }
                    }
                }
            }
            }
        }
    }

    internal sealed class ClipboardSnapshot : IDisposable
    {
        public DataObject Data;
        private readonly List<IDisposable> owned = new List<IDisposable>();
        internal static ClipboardSnapshot Capture()
        { return Capture(null); }
        internal static ClipboardSnapshot Capture(IDataObject original)
        {
            uint sequence = Native.GetClipboardSequenceNumber();
            if (Native.GetOpenClipboardWindow() != IntPtr.Zero) throw new ExternalException("剪贴板正被占用。");
            var snapshot = new ClipboardSnapshot();
            try
            {
                if (original == null) original = Clipboard.GetDataObject();
                string[] formats = original == null ? new string[0] : original.GetFormats(false);
                if (formats.Length == 0 && Native.CountClipboardFormats() != 0) throw new ExternalException("剪贴板格式暂时无法读取。");
                if (original == null) return snapshot;
                snapshot.Data = new DataObject();
                long size = 0;
                foreach (string format in formats)
                {
                    if (Native.GetOpenClipboardWindow() != IntPtr.Zero) throw new ExternalException("剪贴板正被占用。");
                    object value = original.GetData(format, false);
                    if (Native.GetOpenClipboardWindow() != IntPtr.Zero) throw new ExternalException("剪贴板正被占用。");
                    // An empty result is ambiguous: OLE can silently return it during a competing clipboard read.
                    // Refuse the temporary copy rather than risk replacing existing content with an incomplete snapshot.
                    if (value == null || value is string && ((string)value).Length == 0) throw new ExternalException("剪贴板内容暂时无法完整读取。");
                    if (value is Image)
                    {
                        var image = (Image)value;
                        size += (long)image.Width * image.Height * 4;
                        if (size > 64 * 1024 * 1024) throw new InvalidOperationException("剪贴板图片较大，为保留原内容，请手动复制选中文字到主窗口翻译。");
                        value = image.Clone();
                    }
                    else if (value is MemoryStream) { value = new MemoryStream(((MemoryStream)value).ToArray()); size += ((MemoryStream)value).Length; }
                    else if (value is byte[]) { value = ((byte[])value).Clone(); size += ((byte[])value).Length; }
                    else if (value is string) size += ((string)value).Length * 2L;
                    else if (value is string[]) value = ((string[])value).Clone();
                    else if (!value.GetType().IsPrimitive)
                        throw new InvalidOperationException("当前剪贴板含无法完整恢复的格式，为保留原内容，请手动复制选中文字到主窗口翻译。");
                    if (value is IDisposable) snapshot.owned.Add((IDisposable)value);
                    if (size > 64 * 1024 * 1024) throw new InvalidOperationException("剪贴板内容较大，为保留原内容，请手动复制选中文字到主窗口翻译。");
                    snapshot.Data.SetData(format, false, value);
                }
                if (Native.GetClipboardSequenceNumber() != sequence) throw new InvalidOperationException("剪贴板内容已更新。");
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
        }
        public void Dispose() { foreach (var item in owned) item.Dispose(); owned.Clear(); Data = null; }
    }

    internal sealed class ScrollWheelHook : IDisposable
    {
        private IntPtr handle;
        private readonly Native.HookProc callback;
        public event Action<Point> Wheel;
        public ScrollWheelHook()
        {
            callback = OnMouse;
            handle = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("无法监听长截图区域的滚轮操作。");
        }
        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && message.ToInt32() == 0x20a && Wheel != null)
            {
                var wheel = (Native.MouseData)Marshal.PtrToStructure(data, typeof(Native.MouseData));
                Wheel(wheel.Point);
            }
            return Native.CallNextHookEx(handle, code, message, data);
        }
        public void Dispose() { if (handle != IntPtr.Zero) { Native.UnhookWindowsHookEx(handle); handle = IntPtr.Zero; } }
    }
}
