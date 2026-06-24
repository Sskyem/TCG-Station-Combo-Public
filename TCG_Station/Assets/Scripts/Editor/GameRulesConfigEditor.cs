using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GameRulesConfig))]
public class GameRulesConfigEditor : Editor
{
    private List<string> deckNames = new();
    private double lastRefreshTime;
    // Reveal the JSON-overridden fields when loadFromJson is ON. Collapsed by default
    // so the Inspector isn't cluttered with values that don't apply at runtime.
    private bool showInactiveFields;

    private void OnEnable()
    {
        RefreshDeckNames();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        if (EditorApplication.timeSinceStartup - lastRefreshTime > 2.0)
            RefreshDeckNames();

        // loadFromJson is the master switch and the ONLY field never overwritten by the
        // JSON loader, so it always stays visible and active. Everything else (including
        // headlessMode) is read from StreamingAssets/GameRulesConfig.json on Awake when
        // loadFromJson is ON, which makes the Inspector values inactive.
        EditorGUILayout.PropertyField(serializedObject.FindProperty("loadFromJson"));

        GameRulesConfig config = (GameRulesConfig)target;
        bool jsonDriven = config.loadFromJson;

        if (jsonDriven)
        {
            EditorGUILayout.HelpBox(
                "loadFromJson is ON — at runtime every field below is overwritten by " +
                "GameRulesConfig.json (read from next to the build, NOT Assets/StreamingAssets). " +
                "These Inspector values are inactive. Turn loadFromJson OFF to edit them here.",
                MessageType.Info);

            showInactiveFields = EditorGUILayout.Foldout(
                showInactiveFields, "Inactive Inspector values (overwritten by JSON)", true);

            if (!showInactiveFields)
            {
                serializedObject.ApplyModifiedProperties();
                DrawRefreshButton();
                return;
            }
        }

        // Greyed out (but visible) when JSON-driven, fully editable otherwise.
        using (new EditorGUI.DisabledScope(jsonDriven))
        {
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                string path = iterator.propertyPath;
                if (path == "m_Script" || path == "loadFromJson")
                    continue;

                if (path == "player1DeckName" || path == "player2DeckName")
                    DrawDeckPopup(iterator);
                else
                    EditorGUILayout.PropertyField(iterator, true);
            }
        }

        serializedObject.ApplyModifiedProperties();
        DrawRefreshButton();
    }

    private void DrawRefreshButton()
    {
        EditorGUILayout.Space(6);
        if (GUILayout.Button("Refresh deck list"))
            RefreshDeckNames();
    }

    private void DrawDeckPopup(SerializedProperty property)
    {
        if (deckNames.Count == 0)
        {
            EditorGUILayout.PropertyField(property, true);
            EditorGUILayout.HelpBox("No deck JSON files found in the Decks folder.", MessageType.Warning);
            return;
        }

        string currentValue = property.stringValue;
        int currentIndex = deckNames.IndexOf(currentValue);
        bool currentMissing = currentIndex < 0 && !string.IsNullOrWhiteSpace(currentValue);

        List<string> options = deckNames;
        if (currentMissing)
        {
            options = new List<string> { $"{currentValue} (missing)" };
            options.AddRange(deckNames);
            currentIndex = 0;
        }
        else if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int selectedIndex = EditorGUILayout.Popup(property.displayName, currentIndex, options.ToArray());
        if (currentMissing)
        {
            if (selectedIndex > 0)
                property.stringValue = deckNames[selectedIndex - 1];
        }
        else
        {
            property.stringValue = deckNames[selectedIndex];
        }
    }

    private void RefreshDeckNames()
    {
        lastRefreshTime = EditorApplication.timeSinceStartup;
        deckNames = LoadDeckNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> LoadDeckNames()
    {
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(projectRoot))
            yield break;

        string decksPath = Path.Combine(projectRoot, "Decks");
        if (!Directory.Exists(decksPath))
            yield break;

        foreach (string file in Directory.GetFiles(decksPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            string deckName = TryReadDeckName(file);
            if (!string.IsNullOrWhiteSpace(deckName))
                yield return deckName;
        }
    }

    private static string TryReadDeckName(string file)
    {
        try
        {
            JObject obj = JObject.Parse(File.ReadAllText(file), new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore
            });

            return obj.Value<string>("deckName");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GameRulesConfigEditor] Failed to read deck name from {file}: {ex.Message}");
            return null;
        }
    }
}
