using TMPro;
using UnityEditor;
using UnityEngine;

public static class TmpPolishGlyphFixer
{
    private const string FontAssetPath = "Assets/UNITY Stuff/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
    private const string PolishGlyphs = "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ";

    [InitializeOnLoadMethod]
    private static void FixOnLoad()
    {
        EditorApplication.delayCall += EnsurePolishGlyphs;
    }

    [MenuItem("Tools/TCG Station/Fix TMP Polish Glyphs")]
    public static void EnsurePolishGlyphs()
    {
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font == null)
        {
            Debug.LogWarning($"[TmpPolishGlyphFixer] Font asset not found: {FontAssetPath}");
            return;
        }

        font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
        font.isMultiAtlasTexturesEnabled = true;

        if (!font.TryAddCharacters(PolishGlyphs, out string missing) || !string.IsNullOrEmpty(missing))
            Debug.LogWarning($"[TmpPolishGlyphFixer] Missing glyphs after update: {missing}");

        EditorUtility.SetDirty(font);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        foreach (TMP_Text text in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (text == null || text.font != font) continue;
            text.SetAllDirty();
        }

        Debug.Log("[TmpPolishGlyphFixer] LiberationSans SDF updated with Polish glyphs.");
    }
}
