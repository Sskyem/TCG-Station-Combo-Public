using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class Energy : MonoBehaviour
{
    public EnumPokemonType energyType = EnumPokemonType.Colorless;
    public int energyAmountValue = 1;

    public Image energyImage;
    public TMP_Text energyAmount;
    public EnumEnergySource energySource = EnumEnergySource.None;
}
