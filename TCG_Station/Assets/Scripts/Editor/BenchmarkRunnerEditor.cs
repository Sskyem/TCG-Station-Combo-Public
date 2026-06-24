using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Custom Inspector for BenchmarkRunner. Replaces the raw string-list editing of
/// 'participants' with a checkbox grid of decks discovered in the Decks/ folder,
/// so deck names can't be mistyped. Selected names that no longer match a deck file
/// are surfaced separately as "missing" so they can be removed.
/// </summary>
[CustomEditor(typeof(BenchmarkRunner))]
public class BenchmarkRunnerEditor : Editor
{
    private List<string> deckNames = new();
    private double lastRefreshTime;
    private bool participantsExpanded = true;
    // Reveal the JSON-overridden schedule fields when loadFromJson is ON. Collapsed by
    // default so the Inspector isn't cluttered with values that don't apply at runtime.
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

        BenchmarkRunner runner = (BenchmarkRunner)target;

        // Two master switches stay visible & active regardless of source:
        //  - runEnabled is a hard gate checked BEFORE JSON in Awake(): if it's OFF in the
        //    Inspector, JSON cannot re-enable the benchmark.
        //  - loadFromJson selects whether the schedule fields come from JSON or the Inspector.
        EditorGUILayout.PropertyField(serializedObject.FindProperty("runEnabled"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("loadFromJson"));

        bool jsonDriven = runner.loadFromJson;

        if (jsonDriven)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "loadFromJson is ON — the schedule fields below are overwritten at runtime by " +
                "BenchmarkConfig.json (read from next to the build) and are inactive here.\n" +
                "runEnabled above is still a hard gate: if it's OFF, JSON cannot start a benchmark.\n" +
                "The participants tool below stays active — author a selection and push it with " +
                "'Write selection to BenchmarkConfig.json'.",
                MessageType.Info);

            // Participants authoring is the deliberate bridge to the JSON, so keep it active.
            DrawParticipants(serializedObject.FindProperty("participants"));

            showInactiveFields = EditorGUILayout.Foldout(
                showInactiveFields, "Inactive Inspector values (overwritten by JSON)", true);

            if (!showInactiveFields)
            {
                serializedObject.ApplyModifiedProperties();
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
                if (path == "m_Script" || path == "runEnabled" || path == "loadFromJson")
                    continue;

                // When JSON-driven the participants tool was already drawn (active) above.
                if (path == "participants")
                {
                    if (!jsonDriven) DrawParticipants(iterator);
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    // =========================================================================
    // Participants checkbox grid
    // =========================================================================
    private void DrawParticipants(SerializedProperty prop)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < prop.arraySize; i++)
            selected.Add(prop.GetArrayElementAtIndex(i).stringValue);

        EditorGUILayout.Space(4);
        participantsExpanded = EditorGUILayout.Foldout(
            participantsExpanded,
            $"Participants  ({selected.Count} selected / {deckNames.Count} decks)",
            true);

        if (!participantsExpanded)
            return;

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Select all"))
            {
                SetSelection(prop, deckNames);
                ApplyAndExit();
            }
            if (GUILayout.Button("Deselect all"))
            {
                prop.ClearArray();
                ApplyAndExit();
            }
            if (GUILayout.Button("Refresh decks"))
                RefreshDeckNames();
            if (GUILayout.Button("Copy as JSON"))
                CopyAsJson(selected);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Write selection to BenchmarkConfig.json"))
                WriteToConfig(selected);
        }

        if (deckNames.Count == 0)
        {
            EditorGUILayout.HelpBox("No deck JSON files found in the Decks folder.", MessageType.Warning);
        }
        else
        {
            // Two-column toggle grid to keep the list compact.
            const int columns = 2;
            for (int i = 0; i < deckNames.Count; i += columns)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int c = 0; c < columns && i + c < deckNames.Count; c++)
                    {
                        string deck = deckNames[i + c];
                        bool isOn = selected.Contains(deck);
                        bool newOn = EditorGUILayout.ToggleLeft(deck, isOn);
                        if (newOn != isOn)
                        {
                            if (newOn) AddParticipant(prop, deck);
                            else RemoveParticipant(prop, deck);
                        }
                    }
                }
            }
        }

        // Surface selected names that no longer match any deck file.
        List<string> missing = selected
            .Where(s => !string.IsNullOrWhiteSpace(s) && !deckNames.Contains(s))
            .ToList();
        if (missing.Count > 0)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Not found in Decks/ (will be skipped at runtime):", EditorStyles.miniBoldLabel);
            foreach (string name in missing)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"⚠ {name}");
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    {
                        RemoveParticipant(prop, name);
                        ApplyAndExit();
                    }
                }
            }
        }
    }

    private static void AddParticipant(SerializedProperty arrayProp, string name)
    {
        int idx = arrayProp.arraySize;
        arrayProp.arraySize++;
        arrayProp.GetArrayElementAtIndex(idx).stringValue = name;
    }

    private static void RemoveParticipant(SerializedProperty arrayProp, string name)
    {
        for (int i = arrayProp.arraySize - 1; i >= 0; i--)
        {
            if (arrayProp.GetArrayElementAtIndex(i).stringValue == name)
                arrayProp.DeleteArrayElementAtIndex(i);
        }
    }

    private static void SetSelection(SerializedProperty arrayProp, IEnumerable<string> names)
    {
        arrayProp.ClearArray();
        foreach (string name in names)
            AddParticipant(arrayProp, name);
    }

    private void ApplyAndExit()
    {
        // Apply now and abort the current GUI pass so the toggle grid below redraws
        // from the updated array instead of a stale snapshot.
        serializedObject.ApplyModifiedProperties();
        GUIUtility.ExitGUI();
    }

    /// Selection in deck-list order, with any extra (missing) names appended.
    private List<string> OrderedSelection(IEnumerable<string> selected)
    {
        var set = new HashSet<string>(selected, StringComparer.Ordinal);
        var ordered = deckNames.Where(set.Contains).ToList();
        ordered.AddRange(set.Where(s => !deckNames.Contains(s)));
        return ordered;
    }

    private void CopyAsJson(IEnumerable<string> selected)
    {
        List<string> ordered = OrderedSelection(selected);

        var sb = new StringBuilder();
        sb.AppendLine("\"participants\": [");
        for (int i = 0; i < ordered.Count; i++)
            sb.AppendLine($"  \"{ordered[i]}\"{(i < ordered.Count - 1 ? "," : "")}");
        sb.Append("]");

        EditorGUIUtility.systemCopyBuffer = sb.ToString();
        Debug.Log($"[BenchmarkRunnerEditor] Copied {ordered.Count} participant(s) to clipboard as JSON.");
    }

    /// Rewrites only the "participants" array in StreamingAssets/BenchmarkConfig.json,
    /// leaving every other field — and all // comments — untouched. Done as a targeted
    /// text replacement rather than re-serializing, because the config is commented JSON.
    private void WriteToConfig(IEnumerable<string> selected)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "BenchmarkConfig.json");
        if (!File.Exists(path))
        {
            EditorUtility.DisplayDialog("BenchmarkConfig.json not found",
                $"Expected the config at:\n{path}", "OK");
            return;
        }

        List<string> ordered = OrderedSelection(selected);
        string text = File.ReadAllText(path);
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";

        // Match the "participants": [ ... ] block. Deck names never contain ']',
        // so a non-']' body match is safe and avoids over-reaching into the file.
        var regex = new Regex("\"participants\"\\s*:\\s*\\[[^\\]]*\\]");
        if (!regex.IsMatch(text))
        {
            EditorUtility.DisplayDialog("Could not update config",
                "No \"participants\": [ ... ] array found in BenchmarkConfig.json.\n" +
                "Add the key manually once, then this button will keep it in sync.", "OK");
            return;
        }

        // Rebuild the array using the file's existing 2-space indentation style.
        var block = new StringBuilder();
        block.Append("\"participants\": [").Append(nl);
        for (int i = 0; i < ordered.Count; i++)
            block.Append("    \"").Append(ordered[i]).Append('"')
                 .Append(i < ordered.Count - 1 ? "," : "").Append(nl);
        block.Append("  ]");

        if (!EditorUtility.DisplayDialog("Write to BenchmarkConfig.json?",
                $"Overwrite the \"participants\" array with {ordered.Count} deck(s)?\n" +
                "All other fields and comments are preserved.",
                "Write", "Cancel"))
            return;

        string replacement = block.ToString();
        string updated = regex.Replace(text, m => replacement, 1);
        File.WriteAllText(path, updated);
        AssetDatabase.Refresh();

        Debug.Log($"[BenchmarkRunnerEditor] Wrote {ordered.Count} participant(s) to {path}.");
    }

    // =========================================================================
    // Deck discovery (mirrors GameRulesConfigEditor)
    // =========================================================================
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
            Debug.LogWarning($"[BenchmarkRunnerEditor] Failed to read deck name from {file}: {ex.Message}");
            return null;
        }
    }
}
