using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SnapTransOCR
{
    public sealed class AppSettings
    {
        internal const int HotkeyCount = 9;
        internal static readonly string[] TranslationProviders = { "volc", "baidu", "bing", "ark", "baidu_llm", "baidu_domain" };
        internal static readonly string[] TranslationFlags = { "VolcTranslateEnabled", "BaiduTranslateEnabled", "BingTranslateEnabled", "ArkEnabled", "BaiduLlmEnabled", "BaiduDomainEnabled" };
        internal static readonly string[] BaiduDomains = { "academic", "it", "finance", "machinery", "senimed", "novel", "aerospace", "wiki", "news", "law", "contract" };
        public string OcrProvider { get; set; }
        public string TranslateProvider { get; set; }
        public string[] TranslateServiceOrder { get; set; }
        public string[] OcrServiceOrder { get; set; }
        public bool BaiduOcrEnabled { get; set; }
        public bool TencentOcrEnabled { get; set; }
        public bool VolcTranslateEnabled { get; set; }
        public bool BaiduTranslateEnabled { get; set; }
        public bool BingTranslateEnabled { get; set; }
        public bool ArkEnabled { get; set; }
        public string ArkApiKey { get; set; }
        public string ArkModel { get; set; }
        public string ArkApiMode { get; set; }
        public bool BaiduLlmEnabled { get; set; }
        public string BaiduLlmApiKey { get; set; }
        public string BaiduLlmInstruction { get; set; }
        public bool BaiduDomainEnabled { get; set; }
        public string BaiduDomain { get; set; }
        public bool BaiduImageTranslateEnabled { get; set; }
        public string ImageTranslateProvider { get; set; }
        public string BaiduImageTargetLanguage { get; set; }
        public bool BaiduQrEnabled { get; set; }
        public bool BaiduHandwritingEnabled { get; set; }
        public bool BaiduTableEnabled { get; set; }
        public bool TencentTableEnabled { get; set; }
        public bool AccurateOcr { get; set; }
        public string TargetLanguage { get; set; }
        public string SourceLanguage { get; set; }
        public bool TranslateNearCursor { get; set; }
        public bool TranslateTopmost { get; set; }
        public bool TranslateAutoHeight { get; set; }
        public bool TranslateHideOnBlur { get; set; }
        public int TranslateWindowWidth { get; set; }
        public int TranslateWindowHeight { get; set; }
        public bool ScreenshotCopyOnConfirm { get; set; }
        public bool PinTopmost { get; set; }
        public bool CaptureMagnifier { get; set; }
        public int CaptureStroke { get; set; }
        public int CaptureColorArgb { get; set; }
        public int CaptureTextSize { get; set; }
        public bool CaptureTextBold { get; set; }
        public bool StartWithWindows { get; set; }
        public bool HistoryEnabled { get; set; }
        public int HistoryRetentionDays { get; set; }
        public int HistoryShortcutVersion { get; set; }
        public string[] Hotkeys { get; set; }
        public string BaiduOcrKey { get; set; }
        public string BaiduOcrSecret { get; set; }
        public string TencentId { get; set; }
        public string TencentSecret { get; set; }
        public string BaiduTranslateId { get; set; }
        public string BaiduTranslateSecret { get; set; }
        public string VolcId { get; set; }
        public string VolcSecret { get; set; }

        public AppSettings()
        {
            OcrProvider = "baidu";
            TranslateProvider = "volc";
            TranslateServiceOrder = (string[])TranslationProviders.Clone();
            OcrServiceOrder = new[] { "baidu", "tencent" };
            BaiduOcrEnabled = TencentOcrEnabled = VolcTranslateEnabled = BaiduTranslateEnabled = true;
            ArkModel = "doubao-seed-translation-250915";
            ArkApiMode = "auto";
            BaiduDomain = "academic";
            ImageTranslateProvider = "pipeline";
            BaiduImageTargetLanguage = "zh";
            AccurateOcr = true;
            TargetLanguage = "smart";
            SourceLanguage = "auto";
            TranslateNearCursor = true;
            TranslateAutoHeight = true;
            ScreenshotCopyOnConfirm = true;
            PinTopmost = true;
            CaptureMagnifier = true;
            CaptureStroke = 2;
            CaptureColorArgb = unchecked((int)0xffef4a40);
            CaptureTextSize = 24;
            HistoryEnabled = true;
            HistoryRetentionDays = 7;
            HistoryShortcutVersion = 1;
            Hotkeys = new[] { "Alt+1", "", "", "Ctrl+Shift+A", "", "", "", "Ctrl+Alt+V", "" };
        }

        public AppSettings Copy()
        {
            var copy = (AppSettings)MemberwiseClone();
            copy.Hotkeys = Hotkeys == null ? null : (string[])Hotkeys.Clone();
            copy.TranslateServiceOrder = TranslateServiceOrder == null ? null : (string[])TranslateServiceOrder.Clone();
            copy.OcrServiceOrder = OcrServiceOrder == null ? null : (string[])OcrServiceOrder.Clone();
            return copy;
        }

        internal void PreserveLiveLanguages(AppSettings original, AppSettings current)
        {
            if (SourceLanguage == original.SourceLanguage) SourceLanguage = current.SourceLanguage;
            if (TargetLanguage == original.TargetLanguage) TargetLanguage = current.TargetLanguage;
        }

        internal void PreserveLiveWindowSize(AppSettings current)
        {
            TranslateWindowWidth = current.TranslateWindowWidth;
            TranslateWindowHeight = !current.TranslateAutoHeight && TranslateAutoHeight ? 0 : current.TranslateWindowHeight;
        }

        internal void PreserveLiveAnnotationStyle(AppSettings original, AppSettings current)
        {
            if (CaptureStroke == original.CaptureStroke) CaptureStroke = current.CaptureStroke;
            if (CaptureColorArgb == original.CaptureColorArgb) CaptureColorArgb = current.CaptureColorArgb;
            if (CaptureTextSize == original.CaptureTextSize) CaptureTextSize = current.CaptureTextSize;
            if (CaptureTextBold == original.CaptureTextBold) CaptureTextBold = current.CaptureTextBold;
        }

        internal void NormalizeHotkeys()
        {
            if (Hotkeys == null || (Hotkeys.Length != 4 && Hotkeys.Length != HotkeyCount)) throw new FormatException("快捷键配置格式不正确。");
            if (Hotkeys.Length == HotkeyCount) return;
            var expanded = new string[HotkeyCount];
            Array.Copy(Hotkeys, expanded, Hotkeys.Length);
            for (int index = Hotkeys.Length; index < expanded.Length; index++) expanded[index] = "";
            Hotkeys = expanded;
        }

        public bool IsOcrEnabled(string provider) { return provider == "baidu" ? BaiduOcrEnabled : provider == "tencent" && TencentOcrEnabled; }
        public bool IsTranslationEnabled(string provider)
        {
            switch (provider)
            {
                case "volc": return VolcTranslateEnabled;
                case "baidu": return BaiduTranslateEnabled;
                case "bing": return BingTranslateEnabled;
                case "ark": return ArkEnabled;
                case "baidu_llm": return BaiduLlmEnabled;
                case "baidu_domain": return BaiduDomainEnabled;
                default: return false;
            }
        }
        internal static string TranslationName(string provider)
        {
            switch (provider)
            {
                case "volc": return "火山引擎翻译";
                case "baidu": return "百度翻译";
                case "bing": return "必应翻译";
                case "ark": return "豆包 / Ark 翻译";
                case "baidu_llm": return "百度大模型翻译";
                case "baidu_domain": return "百度领域翻译";
                default: return provider ?? "";
            }
        }
        public void NormalizeProviders()
        {
            if (String.IsNullOrWhiteSpace(ArkApiMode)) ArkApiMode = "auto";
            OcrServiceOrder = NormalizeOrder(OcrServiceOrder, "baidu", "tencent");
            TranslateServiceOrder = NormalizeOrder(TranslateServiceOrder, TranslationProviders);
            if (!IsOcrEnabled(OcrProvider)) OcrProvider = Array.Find(OcrServiceOrder, IsOcrEnabled) ?? "";
            if (!IsTranslationEnabled(TranslateProvider)) TranslateProvider = Array.Find(TranslateServiceOrder, IsTranslationEnabled) ?? "";
        }

        internal static string[] NormalizeOrder(string[] order, params string[] known)
        {
            var result = new List<string>();
            foreach (string provider in order ?? new string[0])
                if (Array.IndexOf(known, provider) >= 0 && !result.Contains(provider)) result.Add(provider);
            foreach (string provider in known) if (!result.Contains(provider)) result.Add(provider);
            return result.ToArray();
        }

        public string EffectiveTarget(string text)
        {
            if (TargetLanguage != "smart") return TargetLanguage;
            foreach (char ch in text)
                if (ch >= '\u3400' && ch <= '\u9fff') return "en";
            return "zh";
        }

        public void RequireOcr()
        {
            if (!IsOcrEnabled(OcrProvider)) throw new InvalidOperationException("请先在「识别设置」中启用 OCR 服务。");
            if (OcrProvider == "baidu") Require(BaiduOcrKey, BaiduOcrSecret, "百度 OCR 的 API Key / Secret Key");
            else Require(TencentId, TencentSecret, "腾讯 OCR 的 SecretId / SecretKey");
        }

        public void RequireTranslation()
        {
            if (!IsTranslationEnabled(TranslateProvider)) throw new InvalidOperationException("请先在「翻译设置」中启用翻译服务。");
            if (TranslateProvider == "baidu") Require(BaiduTranslateId, BaiduTranslateSecret, "百度翻译的 APP ID / 密钥");
            else if (TranslateProvider == "volc") Require(VolcId, VolcSecret, "火山引擎翻译的 Access Key ID / Secret Access Key");
            else if (TranslateProvider == "ark")
            {
                Require(ArkApiKey, ArkModel, "豆包 / Ark 的 API Key / Model ID");
                if (ArkApiMode != "auto" && ArkApiMode != "seed" && ArkApiMode != "chat") throw new InvalidOperationException("请在「翻译设置」中选择 Ark 调用协议。");
            }
            else if (TranslateProvider == "baidu_llm") Require(BaiduTranslateId, String.IsNullOrWhiteSpace(BaiduLlmApiKey) ? BaiduTranslateSecret : BaiduLlmApiKey, "百度大模型翻译的 APP ID，以及 API Key 或原翻译密钥");
            else if (TranslateProvider == "baidu_domain")
            {
                Require(BaiduTranslateId, BaiduTranslateSecret, "百度领域翻译共用的 APP ID / 翻译密钥");
                if (Array.IndexOf(BaiduDomains, BaiduDomain) < 0) throw new InvalidOperationException("请在「翻译设置」中选择百度翻译领域。");
            }
        }

        private static void Require(string id, string secret, string name)
        {
            if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("请先在「API 设置」中填写" + name + "。");
        }
    }

    internal static class Json
    {
        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 24 * 1024 * 1024, RecursionLimit = 64 };
        }
        public static string Write(object value) { return Serializer().Serialize(value); }
        public static T Read<T>(string value) { return Serializer().Deserialize<T>(value); }
        public static Dictionary<string, object> Object(string value) { return Read<Dictionary<string, object>>(value); }
        public static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
        public static object Get(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }
        public static string Text(Dictionary<string, object> map, string key)
        {
            object value = Get(map, key);
            return value == null ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        public static string Lines(Dictionary<string, object> map, string arrayName, string textName)
        {
            var result = new List<string>();
            var items = Get(map, arrayName) as IEnumerable;
            if (items == null) throw new InvalidOperationException("服务响应缺少结果字段，请检查服务权限和 API 类型。");
            foreach (object item in items)
            {
                string text = Text(Map(item), textName);
                if (!String.IsNullOrWhiteSpace(text)) result.Add(text);
            }
            return String.Join(Environment.NewLine, result).Trim();
        }
    }

    internal static class SettingsStore
    {
        public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapTransOCR");
        public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

        public static AppSettings Load()
        {
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingSnap", "settings.json");
            return LoadMigrating(FilePath, legacy);
        }
        internal static AppSettings LoadMigrating(string path, string legacy)
        {
            if (File.Exists(path) || !File.Exists(legacy)) return Load(path);
            AppSettings settings = Load(legacy);
            Save(settings, path);
            return settings;
        }
        public static AppSettings Load(string path)
        {
            if (!File.Exists(path)) return new AppSettings();
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                AppSettings settings = Json.Read<AppSettings>(json);
                if (settings == null) throw new FormatException();
                settings.NormalizeHotkeys();
                if (!Json.Object(json).ContainsKey("HistoryShortcutVersion") && String.IsNullOrWhiteSpace(settings.Hotkeys[7]))
                {
                    settings.Hotkeys[7] = "Ctrl+Alt+V";
                    try { Hotkey.Validate(settings.Hotkeys); }
                    catch (FormatException) { settings.Hotkeys[7] = ""; }
                }
                settings.HistoryShortcutVersion = 1;
                if (settings.HistoryRetentionDays < 1 || settings.HistoryRetentionDays > 3650) settings.HistoryRetentionDays = 7;
                settings.NormalizeProviders();
                return settings;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法读取本地设置。原文件已保留：\n" + path + "\n请检查配置格式，或移走此文件后重新配置。", ex);
            }
        }

        public static void Save(AppSettings settings) { Save(settings, FilePath); }
        public static void Save(AppSettings settings, string path)
        {
            byte[] plain = Encoding.UTF8.GetBytes(Json.Write(settings));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(plain, 0, plain.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
