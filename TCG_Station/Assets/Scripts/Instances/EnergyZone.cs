using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class EnergyZone : MonoBehaviour
{
    public EnumPokemonType currentEnergy = EnumPokemonType.None;
    public EnumPokemonType nextEnergy = EnumPokemonType.None;

    private Button button;


    public event Action<EnergyZone> OnEnergyChanged;
    public event Action<EnergyZone> OnEnergyClicked;


    private void Start()
    {
        button = GetComponent<Button>();
        if (button != null)
        {
            button.onClick.AddListener(() => OnEnergyClicked?.Invoke(this));
        }
    }

    public void ConsumeCurrentEnergy()
    {
        currentEnergy = EnumPokemonType.None;
        OnEnergyChanged?.Invoke(this);
    }

    public void AdvanceEnergy(List<EnumPokemonType> pool)
    {
        currentEnergy = nextEnergy;
        nextEnergy = RandomizeEnergy(pool);

        OnEnergyChanged?.Invoke(this);
    }

    public void InitializeEnergy(List<EnumPokemonType> pool)
    {
        currentEnergy = EnumPokemonType.None;
        nextEnergy = RandomizeEnergy(pool);

        OnEnergyChanged?.Invoke(this);
    }

    public EnumPokemonType GetEnumEnergyType(string type)
    {
        if (type == "Current") return currentEnergy;
        else if (type == "Next") return nextEnergy;
        else return EnumPokemonType.None;
    }


    private EnumPokemonType RandomizeEnergy(List<EnumPokemonType> possiblePool)
    {
        if (possiblePool == null || possiblePool.Count == 0) return EnumPokemonType.None;
        int i = UnityEngine.Random.Range(0, possiblePool.Count);
        return possiblePool[i];
    }
}
