using System;
using System.Collections.Generic;
using System.Linq;

namespace SnapTransOCR
{
    internal static class TranslationModelTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var expected = new HashSet<string>(new[] {
                "doubao-seed-evolving", "doubao-seed-2-1-pro-260915", "doubao-seed-2-1-lite-260915", "doubao-seed-2-1-turbo-260628",
                "doubao-seed-2-1-pro-260628", "doubao-seed-2-0-lite-260428", "doubao-seed-2-0-mini-260428", "doubao-seed-2-0-pro-260215",
                "doubao-seed-2-0-lite-260215", "doubao-seed-2-0-mini-260215", "doubao-seed-2-0-code-preview-260215",
                "doubao-seed-character-260628", "doubao-seed-character-251128", "doubao-seed-translation-250915", "glm-5-3-flash-260828",
                "glm-5-2-260617", "kimi-k3-1-pro-261024", "deepseek-v4-1-flash-261017", "deepseek-v4-1-flash-260910",
                "deepseek-v4-1-flash-economy-261017", "deepseek-v4-pro-ga-260813", "deepseek-v4-flash-ga-260731", "deepseek-v4-pro-260425"
            }, StringComparer.Ordinal);
            var models = TranslationModels.Ark;
            check(models.Length == expected.Count && expected.SetEquals(models.Select(model => model.Id)), "Ark catalog matches the 23 exact official current text-generation model IDs");
            check(models.Select(model => model.Id).Distinct().Count() == models.Length, "Ark catalog has no duplicate aliases or invented version IDs");
            check(models.All(model => model.DisplayName.Contains(model.Id) && model.ToString() == model.DisplayName && (model.Protocol == "seed" || model.Protocol == "chat")), "Ark model labels retain API IDs and verified translation protocols");
            check(models[0].Id == "doubao-seed-translation-250915" && models[0].Protocol == "seed" && models[1].Id.Contains("lite"), "dedicated translation precedes lightweight general text presets");
            check(models.Skip(models.Length - 5).All(model => model.DisplayName.Contains("即将下线")) && models.Take(models.Length - 5).All(model => !model.DisplayName.Contains("即将下线")), "retiring models are explicitly labeled and ordered after active presets");
            check(models.All(model => !new[] { "seedance", "seedream", "seed3d", "hyper3d", "hitem3d", "embedding" }.Any(part => model.Id.Contains(part))), "translation catalog excludes generation and embedding endpoints");
            check(TranslationModels.Find("kimi-k3-1-pro-261024").Id == "kimi-k3-1-pro-261024" && TranslationModels.Find("deepseek-v4-1-flash-economy-261017") != null && TranslationModels.Find("unverified-display-name") == null, "model lookup uses verified canonical IDs and does not invent aliases");
            check(TranslationModels.ResolveProtocol("doubao-seed-translation-250915", "auto") == "seed" && TranslationModels.ResolveProtocol("kimi-k3-1-pro-261024", "auto") == "chat", "automatic protocol follows dedicated versus general model contracts");
            check(TranslationModels.ResolveProtocol("ep-custom", "seed") == "seed" && TranslationModels.ResolveProtocol("doubao-seed-translation-250915", "chat") == "chat" && TranslationModels.ResolveProtocol("ep-custom", "auto") == "chat", "custom IDs remain usable and explicit protocol overrides remain authoritative");
        }
    }
}
