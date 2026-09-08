using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace NebulaWorld.MonoBehaviours.Local.Chat;

/// <summary>Chinese glyphs for mod-owned chat UI, including messages received in other languages.</summary>
public static class ChatLocalization
{
    private static Font chineseFont;
    private static TMP_FontAsset chineseFontAsset;

    public static Font Font
    {
        get
        {
            if (chineseFont == null)
            {
                chineseFont = UnityEngine.Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "WenQuanYi Micro Hei" }, 20);
                Object.DontDestroyOnLoad(chineseFont);
            }
            return chineseFont;
        }
    }

    public static void ApplyFont(TMP_Text text)
    {
        if (text == null || text.font == null) return;
        if (chineseFontAsset == null)
        {
            chineseFontAsset = TMP_FontAsset.CreateFontAsset(Font);
            if (chineseFontAsset == null) return;
            chineseFontAsset.isMultiAtlasTexturesEnabled = true;
            Object.DontDestroyOnLoad(chineseFontAsset);
        }
        var fallbacks = text.font.fallbackFontAssetTable ??= new List<TMP_FontAsset>();
        if (text.font != chineseFontAsset && !fallbacks.Contains(chineseFontAsset))
            fallbacks.Add(chineseFontAsset);
    }

    public static void InitializeWindow(GameObject root)
    {
        // Player text is data: only input placeholders may become localized labels.
        foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
        {
            ApplyFont(text);
        }
        foreach (var input in root.GetComponentsInChildren<TMP_InputField>(true))
        {
            if (input.placeholder is TMP_Text placeholder && placeholder.text is "Enter message" or "Search")
                NebulaLocalizedText.Set(placeholder, placeholder.text);
        }
    }
}
