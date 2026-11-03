using System;

namespace SnapTransOCR
{
    internal sealed class TranslationModel
    {
        internal string DisplayName { get; private set; }
        internal string Name { get; private set; }
        internal string Id { get; private set; }
        internal string Protocol { get; private set; }

        internal TranslationModel(string name, string id, string protocol = "chat")
        { Name = name; DisplayName = name + " · " + id; Id = id; Protocol = protocol; }
        public override string ToString() { return DisplayName; }
    }

    internal static class TranslationModels
    {
        // Official public text-generation catalog, verified 2026-10-29. Order reflects task fit, not measured translation quality.
        internal static readonly TranslationModel[] Ark = {
            new TranslationModel("专用翻译 · 豆包 Seed Translation", "doubao-seed-translation-250915", "seed"),
            new TranslationModel("日常 · 豆包 Seed 2.1 Lite", "doubao-seed-2-1-lite-260915"),
            new TranslationModel("日常 · 豆包 Seed 2.0 Mini LTS", "doubao-seed-2-0-mini-260428"),
            new TranslationModel("日常 · 豆包 Seed 2.0 Lite LTS", "doubao-seed-2-0-lite-260428"),
            new TranslationModel("日常 · 豆包 Seed 2.1 Turbo", "doubao-seed-2-1-turbo-260628"),
            new TranslationModel("通用 · DeepSeek V4.1 Flash（1017）", "deepseek-v4-1-flash-261017"),
            new TranslationModel("通用 · DeepSeek V4.1 Flash 经济版（1017）", "deepseek-v4-1-flash-economy-261017"),
            new TranslationModel("通用 · GLM 5.3 Flash", "glm-5-3-flash-260828"),
            new TranslationModel("通用 · DeepSeek V4 Flash 正式版", "deepseek-v4-flash-ga-260731"),
            new TranslationModel("通用 · 豆包 Seed 2.1 Pro（0915）", "doubao-seed-2-1-pro-260915"),
            new TranslationModel("通用 · Kimi K3.1 Pro", "kimi-k3-1-pro-261024"),
            new TranslationModel("通用 · DeepSeek V4 Pro 正式版", "deepseek-v4-pro-ga-260813"),
            new TranslationModel("通用 · GLM 5.2", "glm-5-2-260617"),
            new TranslationModel("通用 · 豆包 Seed 2.1 Pro（0628）", "doubao-seed-2-1-pro-260628"),
            new TranslationModel("通用 · DeepSeek V4.1 Flash（0910）", "deepseek-v4-1-flash-260910"),
            new TranslationModel("其他文本 · 豆包 Seed Character（0628）", "doubao-seed-character-260628"),
            new TranslationModel("其他文本 · 豆包 Seed Character（251128）", "doubao-seed-character-251128"),
            new TranslationModel("快速迭代 · 豆包 Seed Evolving", "doubao-seed-evolving"),
            new TranslationModel("即将下线 · 豆包 Seed 2.0 Pro", "doubao-seed-2-0-pro-260215"),
            new TranslationModel("即将下线 · 豆包 Seed 2.0 Lite", "doubao-seed-2-0-lite-260215"),
            new TranslationModel("即将下线 · 豆包 Seed 2.0 Mini", "doubao-seed-2-0-mini-260215"),
            new TranslationModel("即将下线 · 豆包 Seed 2.0 Code Preview", "doubao-seed-2-0-code-preview-260215"),
            new TranslationModel("即将下线 · DeepSeek V4 Pro（0425）", "deepseek-v4-pro-260425")
        };
        internal static TranslationModel Find(string id) { return Array.Find(Ark, model => String.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)); }
        internal static string ResolveProtocol(string id, string configuredMode)
        {
            if (configuredMode == "seed" || configuredMode == "chat") return configuredMode;
            TranslationModel known = Find(id);
            return known != null ? known.Protocol : (id ?? "").StartsWith("doubao-seed-translation", StringComparison.Ordinal) ? "seed" : "chat";
        }
    }
}
