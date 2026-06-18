using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class HumanBrain : PlayerBrain
{
    private bool isSetupConfirmedByHuman = false;

    public override IEnumerator PerformSetupPhase(System.Action<List<CardInstance>> onSetupComplete)
    {
        Debug.Log($"[HumanBrain] Oczekuję na kliknięcie 'Done Setup' przez gracza {myPlayer.playerName}...");
        isSetupConfirmedByHuman = false;

        // Czekamy aż człowiek kliknie "Done Setup" i potwierdzi swój wybór
        yield return new WaitUntil(() => isSetupConfirmedByHuman);

        // Człowiek zagrał karty sam klikając po stole, więc nie ma listy do zwrócenia
        onSetupComplete?.Invoke(null);
    }

    // Wywoływana przez przycisk "Done Setup" w UI (tylko dla Gracza 1)
    public void ConfirmSetupButtonClicked()
    {
        if (myPlayer.activePokemon != null)
        {
            isSetupConfirmedByHuman = true;
        }
        else
        {
            Debug.LogWarning($"[HumanBrain] {myPlayer.playerName} musi najpierw zagrać aktywnego Pokemona!");
            // TODO: dać sygnał w UI, np. turnInfoText.text = "Musisz wystawić aktywnego Pokemona!"
        }
    }
}