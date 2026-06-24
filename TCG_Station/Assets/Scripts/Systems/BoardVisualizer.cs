using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class BoardVisualizer : MonoBehaviour
{
    [Header("Player 1 Board Areas")]
    public Transform p1HandArea;
    public Transform p1BenchArea;
    public Transform p1ActiveSpot;
    public Transform p1DiscardPile;
    public Transform p1CurrentEnergyZone;
    public Transform p1NextEnergyZone;
    public EnergyZone p1EnergyZoneScript;

    [Header("Player 2 Board Areas")]
    public Transform p2HandArea;
    public Transform p2BenchArea;
    public Transform p2ActiveSpot;
    public Transform p2DiscardPile;
    public Transform p2CurrentEnergyZone;
    public Transform p2NextEnergyZone;
    public EnergyZone p2EnergyZoneScript;

    [Header("Prefabs")]
    public GameObject pokemonCardPrefab;
    public GameObject trainerCardPrefab;
    public GameObject cardBackPrefab;
    public GameObject energyPrefab;

    [Header("Settings")]
    public GameManager gameManager;
    public PlayerManager playerManager;
    public CardActions cardActions;

    [Header("UI")]
    public TMP_Text playerDeckCount;
    public TMP_Text enemyDeckCount;
    public TMP_Text Player1Buffs;
    public TMP_Text Player2Buffs;
	public TMP_Text PlayersTurn;
	public Button FallbackButton;
	public Transform CardPreview;
	public TMP_Text Points;

    [Header("Preview UI")]
    public GameObject actionCancelZone;
    public VisualCard bigCardPreviewPokemon;
    public VisualCard bigCardPreviewTrainer;

    public TMP_Text llmThinkingLog;

    public string cardIdToVisualizeFromDraw = ""; // CardActions saves here the last drawn card ID for visualization purposes.

    private readonly Dictionary<CardInstance, VisualCard> cardVisuals = new Dictionary<CardInstance, VisualCard>();
    private VisualCard _previewCard;
    private PlayerController _fallbackTargetPlayer;
    private EnumGeminiModel _fallbackToModel;


    private void OnEnable()
    {
        if (Application.isBatchMode || (GameRulesConfig.Instance != null && GameRulesConfig.Instance.IsHeadlessMode))
            return;

        PlayerManager.OnPokemonPlayedToBoard -= HandlePokemonPlayedVisuals;
        PlayerManager.OnTrainerPlayed -= HandleTrainerPlayedVisuals;
        PlayerManager.OnCardDiscardedFromHand -= HandleTrainerPlayedVisuals;
        PlayerManager.OnPokemonEvolved -= HandlePokemonEvolved;
        if (p1EnergyZoneScript != null) p1EnergyZoneScript.OnEnergyChanged -= HandleEnergyZoneChanged;
        if (p2EnergyZoneScript != null) p2EnergyZoneScript.OnEnergyChanged -= HandleEnergyZoneChanged;
        PlayerManager.OnPokemonRetreated -= HandlePokemonRetreatedVisuals;
        PlayerManager.OnPokemonEnergyChanged -= HandlePokemonEnergyChanged;
        CardActions.OnCardDrewFromEffect -= VisualizeDrawnCards;
        BattleManager.OnPokemonHpChanged -= HandlePokemonHpChanged;
        BattleManager.OnPokemonStatusChanged -= HandlePokemonStatusChanged;
        BattleManager.OnPokemonKnockedOut -= HandlePokemonKnockedOutVisuals;
        BattleManager.OnPokemonPromoted -= HandlePokemonPromotedVisuals;
        TurnManager.OnTurnStarted -= HandleTurnStarted;
        BattleManager.OnAttackExecuted -= HandleAttackExecuted;
        LLMBrain.OnGeminiApiFailed -= HandleGeminiApiFailed;

        PlayerManager.OnPokemonPlayedToBoard += HandlePokemonPlayedVisuals;
        PlayerManager.OnTrainerPlayed += HandleTrainerPlayedVisuals;
        PlayerManager.OnCardDiscardedFromHand += HandleTrainerPlayedVisuals;
        PlayerManager.OnPokemonEvolved += HandlePokemonEvolved;
        p1EnergyZoneScript.OnEnergyChanged += HandleEnergyZoneChanged;
        p2EnergyZoneScript.OnEnergyChanged += HandleEnergyZoneChanged;
        PlayerManager.OnPokemonRetreated += HandlePokemonRetreatedVisuals;
        PlayerManager.OnPokemonEnergyChanged += HandlePokemonEnergyChanged;
        CardActions.OnCardDrewFromEffect += VisualizeDrawnCards;
        BattleManager.OnPokemonHpChanged += HandlePokemonHpChanged;
        BattleManager.OnPokemonStatusChanged += HandlePokemonStatusChanged;
        BattleManager.OnPokemonKnockedOut += HandlePokemonKnockedOutVisuals;
        BattleManager.OnPokemonPromoted += HandlePokemonPromotedVisuals;
        TurnManager.OnTurnStarted += HandleTurnStarted;
        BattleManager.OnAttackExecuted += HandleAttackExecuted;
        LLMBrain.OnGeminiApiFailed += HandleGeminiApiFailed;
    }

    private void OnDisable()
    {
        PlayerManager.OnPokemonPlayedToBoard -= HandlePokemonPlayedVisuals;
        PlayerManager.OnTrainerPlayed -= HandleTrainerPlayedVisuals;
        PlayerManager.OnCardDiscardedFromHand -= HandleTrainerPlayedVisuals;
        PlayerManager.OnPokemonEvolved -= HandlePokemonEvolved;
        if (p1EnergyZoneScript != null) p1EnergyZoneScript.OnEnergyChanged -= HandleEnergyZoneChanged;
        if (p2EnergyZoneScript != null) p2EnergyZoneScript.OnEnergyChanged -= HandleEnergyZoneChanged;
        PlayerManager.OnPokemonRetreated -= HandlePokemonRetreatedVisuals;
        PlayerManager.OnPokemonEnergyChanged -= HandlePokemonEnergyChanged;
        CardActions.OnCardDrewFromEffect -= VisualizeDrawnCards;
        BattleManager.OnPokemonHpChanged -= HandlePokemonHpChanged;
        BattleManager.OnPokemonStatusChanged -= HandlePokemonStatusChanged;
        BattleManager.OnPokemonKnockedOut -= HandlePokemonKnockedOutVisuals;
        BattleManager.OnPokemonPromoted -= HandlePokemonPromotedVisuals;
        TurnManager.OnTurnStarted -= HandleTurnStarted;
        BattleManager.OnAttackExecuted -= HandleAttackExecuted;
        LLMBrain.OnGeminiApiFailed -= HandleGeminiApiFailed;
    }

    public void DisableForHeadless()
    {
        OnDisable();
        enabled = false;
    }

    public void StartBoardVisualizer()
    {
        gameManager = GameManager.Instance;
        playerManager = PlayerManager.Instance;
        cardActions = FindFirstObjectByType<CardActions>();

        playerDeckCount.text = GameRulesConfig.Instance.deckSize.ToString();
        enemyDeckCount.text = GameRulesConfig.Instance.deckSize.ToString();
        UpdateDeckCounts();
        UpdatePointsText();

        if (FallbackButton != null)
        {
            FallbackButton.gameObject.SetActive(false);
            FallbackButton.onClick.RemoveAllListeners();
            FallbackButton.onClick.AddListener(OnFallbackButtonClicked);
        }
    }

    public void UpdateDeckCounts()
    {
        if (playerDeckCount == null || enemyDeckCount == null)
            return;

        if (playerManager == null)
            playerManager = PlayerManager.Instance;

        if (playerManager?.player1 != null)
            playerDeckCount.text = playerManager.player1.deck.Count.ToString();

        if (playerManager?.player2 != null)
            enemyDeckCount.text = playerManager.player2.deck.Count.ToString();
    }

    public VisualCard CreateNewVisibleCardInParent(CardInstance cardInstance, Transform parent)
    {
        Debug.Log($"Creating {cardInstance.baseData.cardName} in {parent.name}");

        CardData cardData = cardInstance.baseData;
        GameObject prefabToUse;

        if (cardData is PokemonData)
        {
            prefabToUse = pokemonCardPrefab;
        }
        else
        {
            prefabToUse = trainerCardPrefab;
        }


        GameObject newCard = Instantiate(prefabToUse, parent);


        newCard.name = $"{cardData.cardId}_Inst_{cardInstance.instanceId}";

        newCard.transform.localPosition = Vector3.zero;
        newCard.transform.localScale = Vector3.one;

        VisualCard visual = newCard.GetComponent<VisualCard>();


        visual.cardInstance = cardInstance;

        if (cardData is TrainerData)
        {
            MatchPokemonHoverSettings(visual);
        }


        visual.SetupVisualCard(cardInstance);
        cardVisuals[cardInstance] = visual;

        return visual;
    }

    private void MatchPokemonHoverSettings(VisualCard visual)
    {
        if (visual == null || pokemonCardPrefab == null) return;

        VisualCard pokemonVisual = pokemonCardPrefab.GetComponent<VisualCard>();
        if (pokemonVisual == null) return;

        visual.scaleMultiplier = pokemonVisual.scaleMultiplier;
        visual.yOffset = pokemonVisual.yOffset;
        visual.animationSpeed = pokemonVisual.animationSpeed;
    }

    public void MoveCardToVisualSpot(Transform cardObj, Transform targetSpot)
    {
        cardObj.SetParent(targetSpot, false);
        cardObj.localPosition = Vector3.zero;
    }

    /// <summary> Kolejność dzieci pod ławką = sloty 0..n — spójnie z CardInstance.benchSlotIndex. </summary>
    private void ApplyBenchSiblingIndex(VisualCard visualCard, Transform benchArea)
    {
        if (gameManager == null || visualCard == null || visualCard.cardInstance == null || benchArea == null)
            return;

        int slot = visualCard.cardInstance.benchSlotIndex;
        if (slot < 0 || slot >= GameRulesConfig.Instance.benchSize)
            return;

        int last = Mathf.Max(0, benchArea.childCount - 1);
        visualCard.transform.SetSiblingIndex(Mathf.Clamp(slot, 0, last));
    }

    public void RemoveCardVisual(Transform area)
    {
        if (area.childCount > 0)
        {
            Destroy(area.GetChild(0).gameObject);
        }
    }

    public void RefreshAllPokemonVisuals(PlayerController p1, PlayerController p2)
    {
        RefreshSinglePlayer(p1);
        RefreshSinglePlayer(p2);
    }

    private void RefreshSinglePlayer(PlayerController playerController)
    {
        Transform targetActiveSpot = (playerController.playerId == 1) ? p1ActiveSpot : p2ActiveSpot;

        if (playerController.activePokemon != null && targetActiveSpot.childCount > 0)
        {
            VisualCard visualCard = targetActiveSpot.GetComponentInChildren<VisualCard>();
            if (visualCard != null)
            {
                visualCard.enabled = true;
                visualCard.SetupVisualCard(visualCard.cardInstance);
            }
        }
    }

    private void ShowCardPreview(VisualCard sourceCard)
    {
        actionCancelZone.SetActive(true);

        if (sourceCard.pokemon != null)
        {
            bigCardPreviewPokemon.gameObject.SetActive(true);
            bigCardPreviewTrainer.gameObject.SetActive(false);
            Debug.Log($"Previewing Pokemon: {sourceCard.pokemon.pokemonData.cardName}");

            bigCardPreviewPokemon.SetupVisualCard(sourceCard.cardInstance);

            bigCardPreviewPokemon.currentMode = VisualMode.BigPreview;

            bigCardPreviewPokemon.RefreshAttacks();
        }
        else if (sourceCard.trainerData != null)
        {
            bigCardPreviewPokemon.gameObject.SetActive(false);
            bigCardPreviewTrainer.gameObject.SetActive(true);
            Debug.Log($"Previewing Trainer: {sourceCard.trainerData.cardName}");

            bigCardPreviewTrainer.SetupVisualCard(sourceCard.cardInstance);

            bigCardPreviewPokemon.currentMode = VisualMode.BigPreview;
        }
    }

    public void HideCardPreview()
    {
        actionCancelZone.SetActive(false);
    }

    public void VisualizeDrawnCards(PlayerController player, List<CardInstance> newlyDrawnCards)
    {
        Transform targetHand = (player.playerId == 1) ? p1HandArea : p2HandArea;

        foreach (CardInstance card in newlyDrawnCards)
        {
            Debug.Log($"Gracz {player.playerId} dobrał kartę: {card.baseData.cardName}");
            VisualCard visual = CreateNewVisibleCardInParent(card, targetHand);

            // Karty przeciwnika trafiają do ręki rewersem do góry — gracz 1 widzi awersy
            if (player.playerId != 1)
                visual.SetVisibility(true);
            else
                visual.SetVisibility(false);
        }

        UpdateDeckCounts();
    }

    private void HandlePokemonPlayedVisuals(CardInstance cardInstance, string spotType, int playerId)
    {
        VisualCard visualCard = FindVisualCardByInstance(cardInstance);
        if (visualCard == null)
        {
            Debug.LogWarning($"[BoardVisualizer] Brak grafiki dla zagranej karty: {cardInstance.baseData.cardName}");
            return;
        }

        // Podczas setupu (tura 0) karty przeciwnika (gracz 2) są zakryte
        bool hideForSetup = TurnManager.Instance.activePlayerId == 0 && playerId == 2;

        // Zależnie od tego kto zagrał, dobieramy docelowe miejsca
        Transform targetActive = (playerId == 1) ? p1ActiveSpot : p2ActiveSpot;
        Transform targetBench = (playerId == 1) ? p1BenchArea : p2BenchArea;

        if (spotType == "Active")
        {
            visualCard.SetVisibility(hideForSetup);
            visualCard.SetOnBoardMode();
            MoveCardToVisualSpot(visualCard.transform, targetActive);
        }
        else if (spotType == "Bench")
        {
            MoveCardToVisualSpot(visualCard.transform, targetBench);
            ApplyBenchSiblingIndex(visualCard, targetBench);
            // SetVisibility i SetOnBoardMode po SetParent — tak samo jak w HandlePokemonEvolved.
            // Karta może być zagrana w tej samej klatce co dobrana (np. AlgorithmBrain),
            // więc canvas/masking muszą być odświeżone już po zmianie rodzica.
            visualCard.SetVisibility(hideForSetup);
            visualCard.SetOnBoardMode();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)targetBench);
        }

        // Reset skali i rotacji po zmianie rodzica — Unity może je zaburzyć
        visualCard.transform.localScale = Vector3.one;
        visualCard.transform.localRotation = Quaternion.identity;

        if (!hideForSetup)
            UpdateCardPreview(cardInstance);
    }

    private void HandleTrainerPlayedVisuals(CardInstance cardInstance, int playerId)
    {
        VisualCard visualCard = FindVisualCardByInstance(cardInstance);
        if (visualCard == null)
        {
            Debug.LogWarning($"[BoardVisualizer] Brak grafiki dla zagranej karty trenera: {cardInstance.baseData.cardName}");
            return;
        }

        visualCard.SetVisibility(false);
        visualCard.SetInDiscardMode();

        Transform targetDiscard = (playerId == 1) ? p1DiscardPile : p2DiscardPile;
        MoveCardToVisualSpot(visualCard.transform, targetDiscard);

        UpdateCardPreview(cardInstance);

        // Karta z ręki ma LayoutElement + anchorsy pod układ ręki; po przeniesieniu na stos
        // wyglądałaby dziwnie — rozciągamy ją na cały slot rodzica
        if (visualCard.transform is RectTransform cardRt)
        {
            cardRt.anchorMin = Vector2.zero;
            cardRt.anchorMax = Vector2.one;
            cardRt.pivot = new Vector2(0.5f, 0.5f);
            cardRt.offsetMin = Vector2.zero;
            cardRt.offsetMax = Vector2.zero;
        }

        LayoutElement layoutElement = visualCard.GetComponent<LayoutElement>();
        if (layoutElement != null)
            layoutElement.ignoreLayout = true;

        // Po zmianie rodzica na Canvasie Unity może zmienić skalę — wymuszamy reset
        visualCard.transform.localScale = Vector3.one;
        visualCard.transform.localRotation = Quaternion.identity;
    }

    private void HandlePokemonEvolved(CardInstance newEvolutionInstance, CardInstance oldTargetInstance, PlayerController owner)
    {
        VisualCard newEvolutionVisualCard = FindVisualCardByInstance(newEvolutionInstance);
        if (newEvolutionVisualCard == null)
        {
            Debug.LogWarning($"[BoardVisualizer] Brak grafiki dla ewolucji: {newEvolutionInstance.baseData.cardName}");
            return;
        }

        VisualCard oldVisualCard = FindVisualCardByInstance(oldTargetInstance);

        if (oldVisualCard != null)
        {
            // Zapamiętujemy slot, żeby nowa karta trafiła dokładnie w to samo miejsce
            Transform boardSlot = oldVisualCard.transform.parent;

            // Odłączamy starą kartę od rodzica zanim Destroy() ją usunie.
            // Destroy() jest odroczone do końca klatki — bez SetParent(null) LayoutGroup
            // przez jedną klatkę renderingu miałby N+1 dzieci i wypychał ostatnią poza Mask.
            cardVisuals.Remove(oldTargetInstance);
            oldVisualCard.transform.SetParent(null);
            Destroy(oldVisualCard.gameObject);

            // Nowa karta (z ręki) trafia na zwolniony slot
            newEvolutionVisualCard.transform.SetParent(boardSlot, false);
            newEvolutionVisualCard.transform.localPosition = Vector3.zero;

            // Karta AI była zakryta w ręce — teraz odkrywamy awers
            newEvolutionVisualCard.SetVisibility(false);
            newEvolutionVisualCard.SetOnBoardMode();

            // Odświeżamy UI — karta odziedziczyła obrażenia i energie w logice
            newEvolutionVisualCard.UpdateHpVisualsAfterEvolution();
            newEvolutionVisualCard.RefreshAttachedEnergy();
            newEvolutionVisualCard.RefreshSpecialConditionVisuals();

            if (owner.benchPokemons.Contains(newEvolutionVisualCard.cardInstance))
                ApplyBenchSiblingIndex(newEvolutionVisualCard, boardSlot);

            UpdateCardPreview(newEvolutionInstance);
            Debug.Log($"[BoardVisualizer] Pomyślnie podmieniono grafikę ewolucji na stole dla gracza {owner.playerName}.");
        }
        else
        {
            // Old card visual not found (may have already been replaced this turn).
            // Attempt to place the new evolution card in the correct board position anyway.
            Debug.LogWarning($"[BoardVisualizer] Old card visual for '{oldTargetInstance?.baseData?.cardName}' not found — placing evolution card directly.");

            bool isPlayer1 = owner == playerManager.player1;
            Transform benchArea = isPlayer1 ? p1BenchArea : p2BenchArea;
            Transform activeSpot = isPlayer1 ? p1ActiveSpot : p2ActiveSpot;

            Transform targetSlot = (owner.activePokemon == newEvolutionInstance) ? activeSpot : benchArea;

            newEvolutionVisualCard.transform.SetParent(targetSlot, false);
            newEvolutionVisualCard.transform.localPosition = Vector3.zero;
            newEvolutionVisualCard.SetVisibility(false);
            newEvolutionVisualCard.SetOnBoardMode();
            newEvolutionVisualCard.UpdateHpVisualsAfterEvolution();
            newEvolutionVisualCard.RefreshAttachedEnergy();
            newEvolutionVisualCard.RefreshSpecialConditionVisuals();

            if (owner.benchPokemons.Contains(newEvolutionVisualCard.cardInstance))
                ApplyBenchSiblingIndex(newEvolutionVisualCard, targetSlot);

            UpdateCardPreview(newEvolutionInstance);
        }
    }

    // Szuka VisualCard dla danej instancji — najpierw w cache, potem przeszukuje całą scenę
    private VisualCard FindVisualCardByInstance(CardInstance targetInstance)
    {
        if (targetInstance != null && cardVisuals.TryGetValue(targetInstance, out VisualCard cachedVisual) && cachedVisual != null)
        {
            return cachedVisual;
        }

        VisualCard[] allCards = FindObjectsByType<VisualCard>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (VisualCard visual in allCards)
        {
            if (visual.cardInstance == targetInstance)
            {
                cardVisuals[targetInstance] = visual;
                return visual;
            }
        }
        return null;
    }

    // Tylko do testów w edytorze — nie używać w produkcji
    public void TestCreateNewVisibleCardInParent(string cardId, Transform parent)
    {
        if (gameManager.jsonLoader.cardLibrary.TryGetValue(cardId, out CardData cardData))
        {
            GameObject prefabToUse;

            if (cardData is PokemonData)
            {
                prefabToUse = pokemonCardPrefab;
            }
            else
            {
                prefabToUse = trainerCardPrefab;
            }

            GameObject newCard = Instantiate(prefabToUse, parent);
            newCard.name = $"{cardData.cardId}";

            VisualCard visual = newCard.GetComponent<VisualCard>();
            visual.SetOnBoardMode();

            CardInstance testInstance = new CardInstance(cardData);
            visual.SetupVisualCard(testInstance);
            cardVisuals[testInstance] = visual;

            // Reset transformu na końcu — instantiate może ustawić losowe wartości
            newCard.transform.localPosition = Vector3.zero;
            newCard.transform.localScale = Vector3.one;
            newCard.transform.localRotation = Quaternion.identity;
        }
        else
        {
            Debug.LogError($"No {cardId} found");
        }
    }

    public void FlipAllHiddenCards()
    {
        // Odkrywamy karty zakryte w trakcie setupu (poza ręką Gracza 2 — ta zostaje zakryta)
        VisualCard[] allCards = FindObjectsByType<VisualCard>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (VisualCard visual in allCards)
        {
            if (visual.IsHidden() && !playerManager.player2.hand.Contains(visual.cardInstance))
            {
                visual.cardBackImage.SetActive(false);
            }
        }
    }

    private void HandleEnergyZoneChanged(EnergyZone zoneWhichChanged)
    {
        //Debug.Log($"[BoardVisualizer] Detected energy change in zone: {zoneWhichChanged.name}. Current: {zoneWhichChanged.currentEnergy}, Next: {zoneWhichChanged.nextEnergy}");
        if (zoneWhichChanged == p1EnergyZoneScript)
        {
            Debug.Log($"[BoardVisualizer] Detected energy change for {playerManager.player1.name} in zone: {zoneWhichChanged.name}. Current: {zoneWhichChanged.currentEnergy}, Next: {zoneWhichChanged.nextEnergy}");
            // Rysujemy dla Gracza 1
            DrawEnergyInZoneAndDestroyOld(zoneWhichChanged.currentEnergy, p1CurrentEnergyZone);
            DrawEnergyInZoneAndDestroyOld(zoneWhichChanged.nextEnergy, p1NextEnergyZone);
        }
        else if (zoneWhichChanged == p2EnergyZoneScript)
        {
            Debug.Log($"[BoardVisualizer] Detected energy change for {playerManager.player2.name} in zone: {zoneWhichChanged.name}. Current: {zoneWhichChanged.currentEnergy}, Next: {zoneWhichChanged.nextEnergy}");
            // Rysujemy dla Gracza 2
            DrawEnergyInZoneAndDestroyOld(zoneWhichChanged.currentEnergy, p2CurrentEnergyZone);
            DrawEnergyInZoneAndDestroyOld(zoneWhichChanged.nextEnergy, p2NextEnergyZone);
        }
    }

    private void DrawEnergyInZoneAndDestroyOld(EnumPokemonType energyType, Transform zoneParent)
    {
        // Czyścimy starą grafikę przed narysowaniem nowej
        foreach (Transform child in zoneParent)
            Destroy(child.gameObject);

        // None = puste pole (np. na starcie gry przed wylosowaniem energii)
        if (energyType == EnumPokemonType.None) return;

        GameObject newEnergyObj = Instantiate(energyPrefab, zoneParent);
        Energy energyComponent = newEnergyObj.GetComponent<Energy>();

        if (energyComponent != null && energyComponent.energyImage != null)
        {
            energyComponent.energyImage.sprite = VisualCard.GetEnergySprite(energyType.ToString());

            // W strefie energii pokazujemy tylko symbol — cyferkę ukrywamy
            if (energyComponent.energyAmount != null)
            {
                energyComponent.energyAmount.gameObject.SetActive(false);
            }
        }
    }

    private void HandlePokemonKnockedOutVisuals(Pokemon faintedPokemon, PlayerController owner)
    {
        UpdatePointsText();
        StartCoroutine(KnockedOutVisualsCoroutine(faintedPokemon, owner));
    }

    private IEnumerator KnockedOutVisualsCoroutine(Pokemon faintedPokemon, PlayerController owner)
    {
        // Wait for damage text to finish displaying before moving card to discard
        float delay = GameRulesConfig.Instance != null
            ? GameRulesConfig.Instance.damageTextDisplayDuration
            : 1.2f;
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        // Szukamy po pokemonLogic — w momencie eventu owner.activePokemon jest jeszcze ustawiony
        VisualCard visual = null;
        foreach (var kv in cardVisuals)
        {
            if (kv.Key.pokemonLogic == faintedPokemon)
            {
                visual = kv.Value;
                break;
            }
        }

        if (visual == null)
        {
            Debug.LogWarning($"[BoardVisualizer] Nie znaleziono grafiki dla pokonanego: {faintedPokemon.pokemonData.cardName}");
            yield break;
        }

        Transform targetDiscard = (owner.playerId == 1) ? p1DiscardPile : p2DiscardPile;
        visual.SetInDiscardMode();
        MoveCardToVisualSpot(visual.transform, targetDiscard);
        visual.transform.localScale = Vector3.one;

        // Rozciągamy kartę na slot kosza (tak samo jak Trainer)
        if (visual.transform is RectTransform koRt)
        {
            koRt.anchorMin = Vector2.zero;
            koRt.anchorMax = Vector2.one;
            koRt.pivot = new Vector2(0.5f, 0.5f);
            koRt.offsetMin = Vector2.zero;
            koRt.offsetMax = Vector2.zero;
        }
        LayoutElement koLayout = visual.GetComponent<LayoutElement>();
        if (koLayout != null) koLayout.ignoreLayout = true;

        Debug.Log($"[BoardVisualizer] {faintedPokemon.pokemonData.cardName} przeniesiony wizualnie na stos odrzutków.");
        UpdateActiveBuffsText();
    }

    private void HandlePokemonPromotedVisuals(CardInstance promotedCard, PlayerController owner)
    {
        VisualCard visual = FindVisualCardByInstance(promotedCard);
        if (visual == null)
        {
            Debug.LogWarning($"[BoardVisualizer] Nie znaleziono grafiki dla awansowanego Pokemona: {promotedCard.baseData.cardName}");
            return;
        }

        Transform targetActiveSpot = (owner.playerId == 1) ? p1ActiveSpot : p2ActiveSpot;
        MoveCardToVisualSpot(visual.transform, targetActiveSpot);
        visual.SetOnBoardMode();
        visual.transform.localScale = Vector3.one;
        Debug.Log($"[BoardVisualizer] {promotedCard.baseData.cardName} awansowany wizualnie na Active Spot.");
        UpdateActiveBuffsText();
    }

    private void HandlePokemonRetreatedVisuals(PlayerController owner, CardInstance oldActive, CardInstance newActive)
    {
        VisualCard oldVisual = FindVisualCardByInstance(oldActive);
        VisualCard newVisual = FindVisualCardByInstance(newActive);

        if (oldVisual != null && newVisual != null)
        {
            Transform targetActiveSpot = (owner.playerId == 1) ? p1ActiveSpot : p2ActiveSpot;
            Transform targetBenchArea = (owner.playerId == 1) ? p1BenchArea : p2BenchArea;

            // Zamieniamy miejsca — stary Active idzie na ławkę, nowy wkracza do walki
            MoveCardToVisualSpot(oldVisual.transform, targetBenchArea);
            MoveCardToVisualSpot(newVisual.transform, targetActiveSpot);
            ApplyBenchSiblingIndex(oldVisual, targetBenchArea);

            // LayoutGroup może zmienić skalę — wymuszamy reset
            oldVisual.transform.localScale = Vector3.one;
            newVisual.transform.localScale = Vector3.one;

            // Odświeżamy energie na karcie, która uciekła (część mogła zostać zużyta)
            oldVisual.RefreshAttachedEnergy();
            UpdateCardPreview(oldActive);

            Debug.Log($"[BoardVisualizer] Animacja wycofania: {oldActive.pokemonLogic.pokemonData.cardName} ucieka na ławkę, {newActive.pokemonLogic.pokemonData.cardName} wkracza do walki!");
        }
        else
        {
            Debug.LogWarning("[BoardVisualizer] Nie można wykonać animacji wycofania! Brakuje wizualnej reprezentacji jednej z kart.");
        }
        UpdateActiveBuffsText();
    }

    private void HandlePokemonEnergyChanged(CardInstance cardInstance)
    {
        VisualCard visual = FindVisualCardByInstance(cardInstance);
        if (visual == null)
        {
            Debug.LogWarning($"[BoardVisualizer] Nie znaleziono grafiki do odświeżenia energii: {cardInstance.baseData.cardName}");
            return;
        }

        visual.RefreshAttachedEnergy();
    }

    private void HandlePokemonHpChanged(Pokemon pokemon)
    {
        VisualCard[] allCards = FindObjectsByType<VisualCard>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        bool foundAny = false;
        foreach (VisualCard visual in allCards)
        {
            if (visual.cardInstance?.pokemonLogic == pokemon)
            {
                visual.UpdateHpVisuals();
                foundAny = true;
            }
        }

        if (!foundAny)
            Debug.LogWarning($"[BoardVisualizer] Nie znaleziono żadnej grafiki do odświeżenia HP dla {pokemon?.pokemonData?.cardName ?? "NULL"}.");
    }

    private void HandlePokemonStatusChanged(Pokemon pokemon)
    {
        VisualCard visual = FindVisualCardByPokemon(pokemon);
        visual?.RefreshSpecialConditionVisuals();

        // Also refresh the preview panel if it's currently showing this card.
        if (_previewCard?.cardInstance?.pokemonLogic == pokemon)
            _previewCard.RefreshSpecialConditionVisuals();

        UpdateActiveBuffsText();
    }

    private void HandleAttackExecuted(Pokemon attacker, PlayerController attackerOwner, Pokemon defender, string moveName, int damage)
    {
        float scale = GameRulesConfig.Instance != null ? GameRulesConfig.Instance.attackPunchScale : 1.25f;
        float duration = GameRulesConfig.Instance != null ? GameRulesConfig.Instance.attackPunchDuration : 0.15f;

        VisualCard visual = FindVisualCardByPokemon(attacker);
        visual?.PlayAttackPunch(scale, duration);

        if (visual != null) UpdateCardPreview(visual.cardInstance);
    }

    private VisualCard FindVisualCardByPokemon(Pokemon pokemon)
    {
        foreach (var kv in cardVisuals)
            if (kv.Key?.pokemonLogic == pokemon) return kv.Value;
        return null;
    }

    private void HandleTurnStarted(int turnNumber, PlayerController activePlayer)
    {
        if (PlayersTurn == null) return;
        PlayersTurn.text = $"Turn {turnNumber} — {activePlayer.playerName} [{GetPlayerTypeLabel(activePlayer)}]";
    }

    private string GetPlayerTypeLabel(PlayerController player)
    {
        switch (player.playerType)
        {
            case EnumPlayerType.Human:
                return "Human";
            case EnumPlayerType.Algorithm:
                return "Algorithm";
            case EnumPlayerType.LLM:
                if (GameRulesConfig.Instance == null) return "LLM";
                bool isP1 = player.playerId == 1;
                var provider = isP1
                    ? GameRulesConfig.Instance.player1LlmProvider
                    : GameRulesConfig.Instance.player2LlmProvider;
                if (provider == EnumLlmProvider.Ollama)
                {
                    var model = isP1
                        ? GameRulesConfig.Instance.player1OllamaModel
                        : GameRulesConfig.Instance.player2OllamaModel;
                    return $"Ollama/{model}";
                }
                else if (provider == EnumLlmProvider.OpenAI)
                {
                    var model = isP1
                        ? GameRulesConfig.Instance.player1OpenAiModel
                        : GameRulesConfig.Instance.player2OpenAiModel;
                    return $"OpenAI/{model}";
                }
                else
                {
                    var model = isP1
                        ? GameRulesConfig.Instance.player1GeminiModel
                        : GameRulesConfig.Instance.player2GeminiModel;
                    return $"Gemini/{model}";
                }
            default:
                return player.playerType.ToString();
        }
    }

    private void UpdateActiveBuffsText()
    {
        if (Player1Buffs != null)
            Player1Buffs.text = BuildBuffsText(playerManager?.player1?.activePokemon?.pokemonLogic);
        if (Player2Buffs != null)
            Player2Buffs.text = BuildBuffsText(playerManager?.player2?.activePokemon?.pokemonLogic);
    }

    private void UpdatePointsText()
    {
        if (Points == null || playerManager == null) return;
        int p1 = playerManager.player1?.score ?? 0;
        int p2 = playerManager.player2?.score ?? 0;
        Points.text = $"P2: {p2}\nP1: {p1}";
    }

    private void HandleGeminiApiFailed(PlayerController failedPlayer, EnumGeminiModel failedModel)
    {
        if (FallbackButton == null)
        {
            Debug.LogWarning("[BoardVisualizer] FallbackButton not assigned in Inspector — cannot show fallback UI.");
            return;
        }
        _fallbackTargetPlayer = failedPlayer;
        _fallbackToModel = LLMBrain.GetFallbackGeminiModel(failedModel);
        FallbackButton.gameObject.SetActive(true);
        TMP_Text label = FallbackButton.GetComponentInChildren<TMP_Text>();
        if (label != null)
            label.text = $"P{failedPlayer.playerId}: {LLMBrain.GeminiModelDisplayName(failedModel)} failed\n→ {LLMBrain.GeminiModelDisplayName(_fallbackToModel)}";
        Debug.LogWarning($"[BoardVisualizer] Gemini model failed for P{failedPlayer.playerId} ({failedModel}). Offering fallback: {_fallbackToModel}");
    }

    public void OnFallbackButtonClicked()
    {
        if (_fallbackTargetPlayer == null || GameRulesConfig.Instance == null) return;
        if (_fallbackTargetPlayer.playerId == 1)
            GameRulesConfig.Instance.player1GeminiModel = _fallbackToModel;
        else
            GameRulesConfig.Instance.player2GeminiModel = _fallbackToModel;
        if (FallbackButton != null) FallbackButton.gameObject.SetActive(false);
        Debug.Log($"[BoardVisualizer] Fallback applied: P{_fallbackTargetPlayer.playerId} now uses {_fallbackToModel}.");
        _fallbackTargetPlayer = null;
    }

    private void UpdateCardPreview(CardInstance cardInstance)
    {
        if (CardPreview == null || cardInstance == null) return;

        foreach (Transform child in CardPreview)
            Destroy(child.gameObject);
        _previewCard = null;

        CardData cardData = cardInstance.baseData;
        GameObject prefabToUse = (cardData is PokemonData) ? pokemonCardPrefab : trainerCardPrefab;
        if (prefabToUse == null) return;

        GameObject previewObj = Instantiate(prefabToUse, CardPreview);
        previewObj.name = $"Preview_{cardData.cardId}";
        previewObj.transform.localPosition = Vector3.zero;
        previewObj.transform.localScale = Vector3.one;

        _previewCard = previewObj.GetComponent<VisualCard>();
        if (_previewCard != null)
        {
            _previewCard.SetupVisualCard(cardInstance);
            _previewCard.enabled = false; // disable hover/interaction, keep display
        }
    }

    private static string BuildBuffsText(Pokemon p)
    {
        if (p == null) return "";

        var b = p.tempBuffsData;
        var lines = new System.Text.StringBuilder();

        if (b.doMoreDamageToActive != 0)
            lines.AppendLine(b.doMoreDamageToActive > 0 ? $"+{b.doMoreDamageToActive} DMG dealt" : $"{b.doMoreDamageToActive} DMG dealt");
        if (b.takeMoreDamageFromAttacks != 0)
            lines.AppendLine(b.takeMoreDamageFromAttacks > 0 ? $"+{b.takeMoreDamageFromAttacks} DMG taken" : $"{b.takeMoreDamageFromAttacks} DMG taken");
        if (b.counterAttackDamage != 0)
            lines.AppendLine($"Counter: {b.counterAttackDamage}");
        if (b.recoilDamage != 0)
            lines.AppendLine($"Recoil: {b.recoilDamage}");
        if (b.attackEnergyCostChange != 0)
            lines.AppendLine(b.attackEnergyCostChange > 0 ? $"Atk cost +{b.attackEnergyCostChange}" : $"Atk cost {b.attackEnergyCostChange}");
        if (b.retreatEnergyCostChange != 0)
            lines.AppendLine(b.retreatEnergyCostChange > 0 ? $"Retreat cost +{b.retreatEnergyCostChange}" : $"Retreat cost {b.retreatEnergyCostChange}");
        if (b.doubleEnergyValue)
            lines.AppendLine("2x Energy");
        if (!b.canAddEnergy)
            lines.AppendLine("No Energy attach");
        if (!b.canBeKnockedOut)
            lines.AppendLine("Can't KO");
        if (!b.canBeAttacked)
            lines.AppendLine("Untargetable");
        if (b.healPerTurn != 0)
            lines.AppendLine($"Heal {b.healPerTurn}/turn");
        if (b.hpChange != 0)
            lines.AppendLine(b.hpChange > 0 ? $"HP +{b.hpChange}" : $"HP {b.hpChange}");
        if (!b.canEvolve)
            lines.AppendLine("Can't evolve");
        if (!b.canBeDamagedByAbility)
            lines.AppendLine("Ability immune");
        if (!b.canBeDamagedByEffectOfAttack)
            lines.AppendLine("Effect immune");
        if (!b.canBeAffectedByTrainers)
            lines.AppendLine("Trainer immune");
        if (b.takeMoreDamageFromPoison != 0)
            lines.AppendLine($"Poison dmg/turn: {b.takeMoreDamageFromPoison}");
        if (b.takeMoreDamageFromBurn != 0)
            lines.AppendLine($"Burn dmg/turn: {b.takeMoreDamageFromBurn}");
        if (b.rooted)
            lines.AppendLine("Rooted (no retreat)");

        return lines.ToString().TrimEnd();
    }
}
