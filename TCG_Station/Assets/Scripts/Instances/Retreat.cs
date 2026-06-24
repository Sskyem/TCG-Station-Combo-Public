using System;
using UnityEngine;
using UnityEngine.UI;

public class Retreat : MonoBehaviour
{
    private Button button;

    public event Action<Retreat> OnRetreatClicked;

    private void Start()
    {
        button = GetComponent<Button>();
        if (button != null)
        {
            button.onClick.AddListener(RetreatClicked);
        }
        else 
        {
            Debug.LogError("Retreat script requires a Button component on the same GameObject.");
        }
    }

    private void RetreatClicked()
    {
        Debug.Log("[Retreat Button] Retreat button clicked!");
        OnRetreatClicked?.Invoke(this);
    }
}