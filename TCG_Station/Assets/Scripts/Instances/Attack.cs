using System.Collections.Generic;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Attack : MonoBehaviour
{
    [Header("UI Elements")]
    public TMP_Text attackName;
    public TMP_Text attackDamage;
    public TMP_Text attackDescription;
    public HorizontalLayoutGroup energyCostArea;
    public HorizontalLayoutGroup headerArea;

    [Header("Logic Data (Invisible)")]
    public List<EffectData> storedEffects;

    public void RebuildEnergyCostLayout()
    {
        if (energyCostArea == null) return;

        RebuildLayout(energyCostArea.transform);
        if (isActiveAndEnabled)
            StartCoroutine(RebuildEnergyCostLayoutNextFrame());
    }

    private IEnumerator RebuildEnergyCostLayoutNextFrame()
    {
        yield return null;

        if (energyCostArea != null)
            RebuildLayout(energyCostArea.transform);
    }

    // Called when the card moves between board zones (hand -> board, bench -> active).
    // The Header layout group does not auto-rebuild on reparent/resize, so the name,
    // energy cost and damage can end up misaligned without this.
    public void RebuildHeaderLayout()
    {
        Transform header = headerArea != null ? headerArea.transform : transform.Find("Header");
        if (header == null) return;

        RefreshHeaderTextSizes();
        RebuildLayout(header);
        if (isActiveAndEnabled)
            StartCoroutine(RebuildHeaderLayoutNextFrame(header));
    }

    private IEnumerator RebuildHeaderLayoutNextFrame(Transform header)
    {
        yield return null;

        if (header != null)
        {
            RefreshHeaderTextSizes();
            RebuildLayout(header);
        }
    }

    // TMP reports a correct preferred width to the layout group only after its mesh is
    // regenerated. Force that here so the HorizontalLayoutGroup distributes width
    // consistently — otherwise identical cards can get different spacing.
    private void RefreshHeaderTextSizes()
    {
        if (attackName != null) attackName.ForceMeshUpdate();
        if (attackDamage != null) attackDamage.ForceMeshUpdate();
    }

    private static void RebuildLayout(Transform target)
    {
        Canvas.ForceUpdateCanvases();

        Transform current = target;
        while (current != null)
        {
            if (current is RectTransform rt)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

            current = current.parent;
        }
    }
}
