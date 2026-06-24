using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class AdvisorButtonPanel
{
    private const string PanelName = "TCG Advisor Buttons";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Register()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        CreatePanelIfPossible();
    }

    private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CreatePanelIfPossible();
    }

    private static void CreatePanelIfPossible()
    {
        if (Application.isBatchMode) return;
        if (Object.FindFirstObjectByType<BoardVisualizer>() == null) return;
        if (GameObject.Find(PanelName) != null) return;

        Canvas canvas = Object.FindFirstObjectByType<Canvas>();
        if (canvas == null) return;

        var host = new GameObject(PanelName, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(MLSuggestionButton), typeof(LLMSuggestionButton));
        host.transform.SetParent(canvas.transform, false);

        var rect = host.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(-22f, 22f);
        rect.sizeDelta = new Vector2(300f, 42f);

        var layout = host.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleRight;
        layout.childControlWidth = false;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var ml = host.GetComponent<MLSuggestionButton>();
        var llm = host.GetComponent<LLMSuggestionButton>();
        CreateButton(host.transform, "ML Advisor", new Color32(56, 214, 192, 220), ml.RequestMlSuggestion);
        CreateButton(host.transform, "LLM Advisor", new Color32(227, 107, 255, 220), llm.RequestLlmSuggestion);
    }

    private static void CreateButton(Transform parent, string label, Color color, UnityEngine.Events.UnityAction action)
    {
        var buttonObj = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObj.transform.SetParent(parent, false);

        var rect = buttonObj.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(138f, 38f);

        var layout = buttonObj.GetComponent<LayoutElement>();
        layout.preferredWidth = 138f;
        layout.preferredHeight = 38f;

        var image = buttonObj.GetComponent<Image>();
        image.color = color;

        var button = buttonObj.GetComponent<Button>();
        button.onClick.AddListener(action);

        var textObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObj.transform.SetParent(buttonObj.transform, false);
        var textRect = textObj.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        var text = textObj.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.color = Color.black;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 15f;
        text.fontStyle = FontStyles.Bold;
    }
}
