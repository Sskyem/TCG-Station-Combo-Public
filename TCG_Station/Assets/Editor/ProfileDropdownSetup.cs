#if UNITY_EDITOR
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Editor utility that wires the per-seat AlgorithmBrain profile dropdowns into the Initialization
/// menu scene by cloning an existing, already-styled type dropdown. Idempotent: skips a seat whose
/// profile dropdown is already assigned.
///
/// Run from the menu (Tools ▸ TCG ▸ Add profile dropdowns to menu) or in batch mode via
///   -executeMethod ProfileDropdownSetup.Run
/// </summary>
public static class ProfileDropdownSetup
{
    private const string ScenePath = "Assets/Scenes/Initialization.unity";

    [MenuItem("Tools/TCG/Add profile dropdowns to menu")]
    public static void Run()
    {
        Scene_OpenAndWire();
    }

    private static void Scene_OpenAndWire()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var controller = Object.FindFirstObjectByType<StartupMenuController>(FindObjectsInactive.Include);
        if (controller == null)
        {
            Debug.LogError("[ProfileDropdownSetup] No StartupMenuController in the scene — aborting.");
            return;
        }

        var so = new SerializedObject(controller);
        var oppType = so.FindProperty("opponentTypeDropdown").objectReferenceValue as TMP_Dropdown;
        var p1Type = so.FindProperty("player1TypeDropdown").objectReferenceValue as TMP_Dropdown;
        SerializedProperty p1Prof = so.FindProperty("player1ProfileDropdown");
        SerializedProperty p2Prof = so.FindProperty("player2ProfileDropdown");

        if (p1Prof == null || p2Prof == null)
        {
            Debug.LogError("[ProfileDropdownSetup] StartupMenuController has no player1/2ProfileDropdown fields — recompile first.");
            return;
        }
        if (oppType == null)
        {
            Debug.LogError("[ProfileDropdownSetup] opponentTypeDropdown is not assigned — need a template dropdown to clone.");
            return;
        }

        int created = 0;
        if (p2Prof.objectReferenceValue == null)
        {
            p2Prof.objectReferenceValue = CloneDropdown(oppType, "Player2ProfileDropdown", "Opponent profile");
            created++;
        }
        if (p1Prof.objectReferenceValue == null)
        {
            TMP_Dropdown template = p1Type != null ? p1Type : oppType;
            p1Prof.objectReferenceValue = CloneDropdown(template, "Player1ProfileDropdown", "Player 1 profile");
            created++;
        }

        so.ApplyModifiedProperties();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[ProfileDropdownSetup] Done — created {created} profile dropdown(s), saved {ScenePath}.");
    }

    private static TMP_Dropdown CloneDropdown(TMP_Dropdown template, string name, string headerText)
    {
        GameObject go = Object.Instantiate(template.gameObject, template.transform.parent);
        go.name = name;

        // Place the clone one row below the template. If the parent uses a LayoutGroup this is ignored
        // (the layout positions it), which is fine; otherwise it gives a sensible non-overlapping start.
        var rt = go.GetComponent<RectTransform>();
        var srcRt = template.GetComponent<RectTransform>();
        if (rt != null && srcRt != null)
            rt.anchoredPosition = srcRt.anchoredPosition + new Vector2(0f, -75f);

        var dd = go.GetComponent<TMP_Dropdown>();
        // Set the descriptive header (a direct-child TMP_Text that is neither the caption nor item label).
        foreach (TMP_Text t in go.GetComponentsInChildren<TMP_Text>(true))
        {
            if (t == dd.captionText || t == dd.itemText) continue;
            if (t.transform.parent == go.transform) { t.text = headerText; break; }
        }
        return dd;
    }
}
#endif
