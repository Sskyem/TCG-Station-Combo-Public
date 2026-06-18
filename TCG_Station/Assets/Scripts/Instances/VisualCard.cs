using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class VisualCard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Header("Hover Animation")]
    public Transform visualRoot;
    public float scaleMultiplier = 1.2f;
    public float yOffset = 0.5f;
    public float animationSpeed = 15f;

    public Vector3 originalScale = Vector3.one;
    public Vector3 originalPosition;
    private bool hasCachedOriginalTransform;
    private Canvas canvas;
    private int originalSortingOrder;
    private Coroutine currentAnimation;
    private Coroutine damageTextCoroutine;
    private int lastKnownHp = -1;

    [Header("UI Color To Change")]
    [SerializeField] TMP_Text hpPrefix;
    public Image cardOutline;
    public Color damageTakenColor;
    public Color damageHealedColor;

    [Header("Other UI")]
    public Image toolImage;
    public TMP_Text damageText;
    public TMP_Text artAuthor;
    [SerializeField] TMP_Text trainerDescriptionText;
    [SerializeField] HorizontalLayoutGroup retreatCostArea;

    [Header("Basic Info")]
    public TMP_Text nameText;
    public TMP_Text hpText;
    public TMP_Text biggerHpText;

    [Header("Art")]
    public Image cardArt;
    public Image template;
    public Image specialConditionOther;
    public Image specialConditionPoison;
    // Dedicated Burn slot so Burn can show alongside Poison and a trio status (Paralyze/Sleep/Confuse).
    public Image specialConditionBurn;
    public GameObject cardBackImage;

    [Header("Dynamic Content References")]
    public GameObject energyPrefab;
    public GameObject energyPrefabNoText;
    public GameObject attackPrefab;
    public GameObject previewAttackPrefab;
    public Transform energyParent;
    public Transform attackParent;

    [Header("Modules")]
    public PlayerManager playerManager;
    public TurnManager turnManager;
    [SerializeField] Button cardButton;

    


    // --- ZMIENNE DANYCH ---
    public CardInstance cardInstance;

    // Dynamiczne skróty (Gettery). Nie przechowują danych, tylko w locie czytają je z CardInstance!
    public CardData cardData => cardInstance?.baseData;
    public PokemonData PokemonData => cardInstance?.pokemonLogic?.pokemonData;
    public Pokemon pokemon => cardInstance?.pokemonLogic;
    public TrainerData trainerData => cardInstance?.baseData as TrainerData;

    [Header("State Flags")]
    public VisualMode currentMode = VisualMode.InHand; // Domyślnie karta rodzi się w ręce

    private Coroutine biggerHpAnim;
    private int biggerHpDisplayed = -1;


    private void Awake()
    {
        CacheOriginalVisualTransform(force: true);
    }

    private void OnValidate()
    {
        CacheOriginalVisualTransform(force: originalScale == Vector3.zero);
    }

    void Start()
    {
        playerManager = PlayerManager.Instance;

        // Statusy specjalne domyślnie ukryte — widoczne tylko gdy Pokemon faktycznie je posiada
        if (specialConditionPoison != null) specialConditionPoison.gameObject.SetActive(false);
        if (specialConditionOther != null) specialConditionOther.gameObject.SetActive(false);
        SetBurnSlotActive(false);

        // Zapisujemy początkowe ułożenie grafiki do animacji hover
        CacheOriginalVisualTransform();

        // Dodajemy Canvas, żeby karta mogła wyjść przed inne
        canvas = GetComponent<Canvas>();
        if (canvas != null)
        {
            originalSortingOrder = canvas.sortingOrder;
        }

        if (cardButton != null)
        {
            cardButton.onClick.RemoveAllListeners();
            cardButton.onClick.AddListener(HandleCardClick);
        }
        else
        {
            Debug.LogWarning("Na tym obiekcie brakuje komponentu Button!");
        }

        HideDamageText();

    }


    #region Card Hover/Preview Animation

    private void CacheOriginalVisualTransform(bool force = false)
    {
        if (visualRoot == null)
        {
            return;
        }

        if (hasCachedOriginalTransform && !force)
        {
            return;
        }

        Vector3 detectedScale = visualRoot.localScale;
        if (detectedScale == Vector3.zero)
        {
            detectedScale = Vector3.one;
            visualRoot.localScale = detectedScale;
        }

        originalScale = detectedScale;
        originalPosition = visualRoot.localPosition;
        hasCachedOriginalTransform = true;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        CacheOriginalVisualTransform();

        // Twarda blokada: Jeśli karta jest zakryta lub nie ma grafiki, ignorujemy
        if (IsHidden() || visualRoot == null)
        {
            return;
        }

        // Logika TYLKO dla ręki (powiększanie przy najechaniu)
        if (currentMode == VisualMode.InHand)
        {
            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;

            Vector3 targetScale = originalScale * scaleMultiplier;
            Vector3 targetPosition = originalPosition + new Vector3(0, yOffset, 0);

            if (currentAnimation != null)
            {
                StopCoroutine(currentAnimation);
            }
            currentAnimation = StartCoroutine(AnimateCard(targetScale, targetPosition));
        }
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        CacheOriginalVisualTransform();

        // Twarda blokada
        if (IsHidden() || visualRoot == null)
        {
            return;
        }

        // Powrót do normy TYLKO dla ręki
        if (currentMode == VisualMode.InHand)
        {
            canvas.overrideSorting = false;
            canvas.sortingOrder = originalSortingOrder;

            if (currentAnimation != null)
            {
                StopCoroutine(currentAnimation);
            }
            currentAnimation = StartCoroutine(AnimateCard(originalScale, originalPosition));
        }
    }

    // Coroutine odpowiadające za płynne przejście (interpolację)
    public IEnumerator AnimateCard(Vector3 targetScale, Vector3 targetPosition)
    {
        while (Vector3.Distance(visualRoot.localScale, targetScale) > 0.01f ||
               Vector3.Distance(visualRoot.localPosition, targetPosition) > 0.01f)
        {
            // Płynne zbliżanie się do celu
            visualRoot.localScale = Vector3.Lerp(visualRoot.localScale, targetScale, Time.deltaTime * animationSpeed);
            visualRoot.localPosition = Vector3.Lerp(visualRoot.localPosition, targetPosition, Time.deltaTime * animationSpeed);

            yield return null; // Czekamy do następnej klatki
        }

        // Twarde przypisanie wartości na koniec, żeby zniwelować mikro-niedokładności
        visualRoot.localScale = targetScale;
        visualRoot.localPosition = targetPosition;
    }


    #endregion


    public void PlayAttackPunch(float scaleMult, float duration)
    {
        if (visualRoot == null) return;
        StartCoroutine(AttackPunchCoroutine(scaleMult, duration));
    }

    private IEnumerator AttackPunchCoroutine(float scaleMult, float duration)
    {
        Vector3 punchScale = originalScale * scaleMult;
        float half = duration * 0.5f;
        float elapsed = 0f;

        while (elapsed < half)
        {
            visualRoot.localScale = Vector3.Lerp(originalScale, punchScale, elapsed / half);
            elapsed += Time.deltaTime;
            yield return null;
        }
        visualRoot.localScale = punchScale;

        elapsed = 0f;
        while (elapsed < half)
        {
            visualRoot.localScale = Vector3.Lerp(punchScale, originalScale, elapsed / half);
            elapsed += Time.deltaTime;
            yield return null;
        }
        visualRoot.localScale = originalScale;
    }

    private void HandleCardClick()
    {
        Debug.Log($"[VisualCard] Kliknięto kartę: {this.cardData.cardId}");

        // Widok zgłasza tylko dane karty. Logika gry nie powinna znać VisualCard.
        CardInputEvents.RaiseCardClicked(cardInstance);
    }







    public void SetupVisualCard(CardInstance instance)
    {
        if (instance == null || instance.baseData == null) return;

        // 1. Przypisujemy TYLKO unikalny kartonik. Skróty (gettery) od razu zaczną działać!
        this.cardInstance = instance;

        string templateName = string.Empty;

        // 2. Rozgałęzienie dla specyficznej logiki wizualnej
        if (this.pokemon != null)
        {
            templateName = this.pokemon.pokemonData.type.ToString();
            SetupPokemonUI(this.pokemon);
        }
        else if (this.trainerData != null)
        {
            templateName = "Trainer";
            SetupTrainerUI();
        }


        // BASIC INFO ON CARD FOR POKEMONS AND TRAINERS
        if (nameText) nameText.text = this.cardData.cardName;
        SetCardArt(this.cardData.cardName, this.cardData.imageName);
        SetCardTemplate(templateName);
        artAuthor.text = $"Art by: {this.cardData.artAuthor}";
    }

    private void SetupPokemonUI(Pokemon pokemon)
    {
        if (hpText) hpText.text = pokemon.pokemonData.hp.ToString();
        SetBiggerHp(pokemon.currentHp, pokemon.pokemonData.hp, animate: false);
        lastKnownHp = pokemon.currentHp;
        HideDamageText();
        CheckIfToChangeTextColorIfDarknessType();
        RefreshAttacks();
        RefreshAttachedEnergy();
        RefreshRetreatCost();
        RefreshSpecialConditionVisuals();
        ApplyBiggerHpVisibility();
    }

    // biggerHpText is meant to be a large, human-readable HP overlay shown ONLY while the
    // card sits on the board. In the hand, preview, or discard it must stay hidden.
    private void ApplyBiggerHpVisibility()
    {
        if (biggerHpText == null) return;
        biggerHpText.gameObject.SetActive(currentMode == VisualMode.OnBoard);
    }

    // Green at full HP, orange below 50%, red below 25%.
    private static Color GetBiggerHpColor(int currentHp, int maxHp)
    {
        if (maxHp <= 0) return Color.green;
        float ratio = (float)currentHp / maxHp;
        if (ratio < 0.25f) return Color.red;
        if (ratio < 0.5f)  return new Color(1f, 0.55f, 0f); // orange
        return Color.green;
    }

    private void SetBiggerHp(int targetHp, int maxHp, bool animate)
    {
        if (biggerHpText == null) return;

        if (biggerHpAnim != null)
        {
            StopCoroutine(biggerHpAnim);
            biggerHpAnim = null;
        }

        int from = biggerHpDisplayed >= 0 ? biggerHpDisplayed : targetHp;
        if (!animate || from == targetHp || !isActiveAndEnabled)
        {
            ApplyBiggerHp(targetHp, maxHp);
            return;
        }

        biggerHpAnim = StartCoroutine(AnimateBiggerHp(from, targetHp, maxHp));
    }

    private IEnumerator AnimateBiggerHp(int from, int to, int maxHp)
    {
        float duration = GameRulesConfig.Instance != null
            ? Mathf.Max(0.05f, GameRulesConfig.Instance.minAiDelay * 0.75f)
            : 0.75f;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            int v = Mathf.RoundToInt(Mathf.Lerp(from, to, t));
            ApplyBiggerHp(v, maxHp);
            yield return null;
        }

        ApplyBiggerHp(to, maxHp);
        biggerHpAnim = null;
    }

    private void ApplyBiggerHp(int hp, int maxHp)
    {
        if (biggerHpText == null) return;
        biggerHpText.text = $"{hp}/{maxHp}";
        biggerHpText.color = GetBiggerHpColor(hp, maxHp);
        biggerHpDisplayed = hp;
    }

    private void SetupTrainerUI()
    {
        DestroyChildrenOfParent(attackParent);
        DestroyChildrenOfParent(energyParent);
        ResetTextColor();

        string description = trainerData?.effectDescription ?? "";

        if (trainerDescriptionText != null)
        {
            trainerDescriptionText.text = description;
            return;
        }

        if (attackParent == null || string.IsNullOrEmpty(description)) return;

        GameObject prefab = currentMode == VisualMode.BigPreview ? previewAttackPrefab : attackPrefab;
        if (prefab == null) return;

        GameObject obj = Instantiate(prefab, attackParent);
        Attack attack = obj.GetComponent<Attack>();
        if (attack == null) return;

        if (attack.attackName)   attack.attackName.text   = "";
        if (attack.attackDamage) attack.attackDamage.text = "";
        if (attack.attackDescription) attack.attackDescription.text = description;
    }



    public void RefreshRetreatCost()
    {
        if (retreatCostArea == null) return;
        int cost = cardInstance.pokemonLogic.pokemonData.retreatCost;
        var colorlessCost = new System.Collections.Generic.List<EnumPokemonType>();
        for (int i = 0; i < cost; i++) colorlessCost.Add(EnumPokemonType.Colorless);
        DrawEnergyIcons(retreatCostArea.transform, colorlessCost);
    }

    private void DrawEnergyIcons(Transform container, System.Collections.Generic.List<EnumPokemonType> energyTypes)
    {
        ClearChildren(container);
        if (energyPrefabNoText == null)
        {
            RebuildLayoutNowAndNextFrame(container);
            return;
        }

        foreach (var energyType in energyTypes)
        {
            GameObject icon = Instantiate(energyPrefabNoText, container);
            icon.transform.localScale = Vector3.one;
            Energy e = icon.GetComponent<Energy>();
            if (e != null && e.energyImage != null)
            {
                e.energyType = energyType;
                e.energyImage.sprite = GetEnergySprite(energyType.ToString());
            }
        }

        RebuildLayoutNowAndNextFrame(container);
    }

    public void RefreshAttacks()
    {
        DestroyChildrenOfParent(attackParent);

        if (PokemonData == null || PokemonData.attacks == null) return;
        if (attackPrefab == null || attackParent == null) return;

        Color textColor = Color.black;
        if (PokemonData.type.ToString() == "Darkness") textColor = Color.white;

        foreach (var attackInfo in PokemonData.attacks)
        {
            GameObject newAttackObj = null;
            if (currentMode == VisualMode.BigPreview)
            {
                newAttackObj = Instantiate(previewAttackPrefab, attackParent);
            }
            else
            {
                newAttackObj = Instantiate(attackPrefab, attackParent);
            }
            
            Attack attack = newAttackObj.GetComponent<Attack>();

            if (attack != null)
            {
                // 1. Ustawiamy wygląd (Teksty)
                if (attack.attackName)
                {
                    attack.attackName.text = attackInfo.attackName;
                    attack.attackName.color = textColor;
                }

                if (attack.attackDamage)
                {
                    attack.attackDamage.text = attackInfo.damage.ToString();
                    attack.attackDamage.color = textColor;
                }

                if (attack.attackDescription)
                {
                    attack.attackDescription.text = attackInfo.attackDescription;
                    attack.attackDescription.color = textColor;
                }

                if (attack.energyCostArea != null && attackInfo.attackCost != null)
                {
                    DrawEnergyIcons(attack.energyCostArea.transform, attackInfo.attackCost);
                    attack.RebuildEnergyCostLayout();
                }

                // 2. Przekazujemy LOGIKĘ (Efekty)
                // Nie wyświetlamy tego, po prostu zapisujemy w obiekcie,
                // żeby BattleManager mógł to potem pobrać (np. storedEffects[0].effectType)
                attack.storedEffects = attackInfo.effects;
            }
        }
    }


    public void RefreshAttachedEnergy()
    {
        if (pokemon == null || pokemon.energyEquipped == null) return;
        if (energyPrefab == null || energyParent == null) return;

        // Faza 1: aktualizuj istniejące ikony lub twórz nowe dla każdego typu
        foreach (var kvp in pokemon.energyEquipped)
        {
            EnumPokemonType type = kvp.Key;
            int amount = kvp.Value;

            if (amount <= 0) continue;

            // Szukaj istniejącej ikony tego samego typu
            Energy existingIcon = null;
            foreach (Transform child in energyParent)
            {
                Energy e = child.GetComponent<Energy>();
                if (e != null && e.energyType == type)
                {
                    existingIcon = e;
                    break;
                }
            }

            if (existingIcon != null)
            {
                // Ten typ już jest — zaktualizuj tylko licznik
                if (existingIcon.energyAmount != null)
                    existingIcon.energyAmount.text = amount.ToString();
            }
            else
            {
                // Nowy typ — instancjonuj ikonę
                string typeName = type.ToString();
                GameObject newObj = Instantiate(energyPrefab, energyParent);
                newObj.transform.localScale = Vector3.one;
                var rootImg = newObj.GetComponent<Image>();
                if (rootImg != null) rootImg.sprite = GetEnergySprite(typeName);

                Energy energy = newObj.GetComponent<Energy>();
                if (energy != null)
                {
                    energy.energyType = type;
                    if (energy.energyAmount != null)
                        energy.energyAmount.text = amount.ToString();
                    if (energy.energyImage != null)
                        energy.energyImage.sprite = GetEnergySprite(typeName);
                }
            }
        }

        // Faza 2: usuń ikony typów których Pokemon już nie posiada (np. po wycofaniu)
        for (int i = energyParent.childCount - 1; i >= 0; i--)
        {
            Transform child = energyParent.GetChild(i);
            Energy e = child.GetComponent<Energy>();
            if (e == null) continue;

            if (!pokemon.energyEquipped.TryGetValue(e.energyType, out int val) || val <= 0)
            {
                child.SetParent(null);
                Destroy(child.gameObject);
            }
        }

        RebuildLayoutNowAndNextFrame(energyParent);
    }

    /// <summary>
    /// Odświeża ikonę statusu specjalnego. Wywołuj po każdej zmianie statusu Pokemona.
    /// specialConditionPoison — ikona trucizny (sprite ustawiony w inspektorze).
    /// specialConditionOther  — ikona pozostałych statusów (Burn/Paralysis/Sleep/Confusion),
    ///                          sprite ładowany dynamicznie z Resources/Special_Conditions_Statutes.
    /// </summary>
    public void RefreshSpecialConditionVisuals()
    {
        if (pokemon == null)
        {
            if (specialConditionPoison != null) specialConditionPoison.gameObject.SetActive(false);
            if (specialConditionOther != null) specialConditionOther.gameObject.SetActive(false);
            SetBurnSlotActive(false);
            return;
        }

        // --- Trucizna ---
        bool showPoison = pokemon.isPoisoned;
        if (specialConditionPoison != null)
            specialConditionPoison.gameObject.SetActive(showPoison);

        // --- Burn (własny slot) ---
        // Burn ma teraz dedykowany Image, więc może współistnieć z trucizną i z grupą trio.
        SetBurnSlotActive(pokemon.isBurned);

        // --- Grupa trio (Paralyze/Sleep/Confuse) — wspólny "other" slot, niezależny od trucizny i Burn ---
        string spriteName = pokemon.otherSpecialCondition switch
        {
            EnumSpecialConditionType.Paralyzed => "paralysis_status",
            EnumSpecialConditionType.Asleep    => "sleep_status",
            EnumSpecialConditionType.Confused  => "confusion_status",
            _                                  => null
        };

        bool showOther = spriteName != null;
        if (specialConditionOther != null)
        {
            specialConditionOther.gameObject.SetActive(showOther);

            if (showOther)
            {
                Sprite statusSprite = Resources.Load<Sprite>($"Special_Conditions_Statutes/{spriteName}");
                if (statusSprite != null)
                    specialConditionOther.sprite = statusSprite;
                else
                    Debug.LogWarning($"[VisualCard] Nie znaleziono sprite'a statusu: {spriteName}");
            }
        }
    }

    /// <summary>
    /// Toggles the Burn status icon. The Image lives inside a layout-element wrapper,
    /// so we toggle the wrapper (parent) when present to free its layout slot when hidden.
    /// </summary>
    private void SetBurnSlotActive(bool active)
    {
        if (specialConditionBurn == null) return;
        Transform parent = specialConditionBurn.transform.parent;
        GameObject slot = parent != null ? parent.gameObject : specialConditionBurn.gameObject;
        slot.SetActive(active);
    }

    private void DestroyChildrenOfParent(Transform parent)
    {
        if (parent == null) return;
        ClearChildren(parent);
    }

    private void ClearChildren(Transform parent)
    {
        if (parent == null) return;

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            child.SetParent(null, false);
            Destroy(child.gameObject);
        }
    }

    private void RebuildLayoutNowAndNextFrame(Transform target)
    {
        RebuildLayoutChain(target);
        if (isActiveAndEnabled)
            StartCoroutine(RebuildLayoutNextFrame(target));
    }

    private IEnumerator RebuildLayoutNextFrame(Transform target)
    {
        yield return null;
        RebuildLayoutChain(target);
    }

    private static void RebuildLayoutChain(Transform target)
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

    public static Sprite GetEnergySprite(string type)
    {
        string cleanType = type.ToLower();
        string path = $"Energy_images/{cleanType}_energy";

        Sprite sprite = Resources.Load<Sprite>(path);

        if (sprite == null)
        {
            sprite = Resources.Load<Sprite>("Energy_images/colorless_energy");
            Debug.LogWarning($"Nie znaleziono sprite'a dla energii '{type}' pod ścieżką '{path}'. Użyto fallback.");
        }
        return sprite;
    }

    public void UpdateHpVisuals()
    {
        if (pokemon == null) return;

        int currentHp = pokemon.currentHp;
        int hpDelta = lastKnownHp >= 0 ? currentHp - lastKnownHp : 0;

        if (hpText != null) hpText.text = pokemon.pokemonData.hp.ToString();
        SetBiggerHp(currentHp, pokemon.pokemonData.hp, animate: true);

        if (hpDelta != 0)
            ShowDamageText(hpDelta);

        lastKnownHp = currentHp;
    }

    // Call after evolution: resets the HP baseline so the damage delta text
    // doesn't show the difference between hand HP and post-transfer HP.
    public void UpdateHpVisualsAfterEvolution()
    {
        lastKnownHp = -1;
        HideDamageText();
        UpdateHpVisuals();
    }

    private void ShowDamageText(int hpDelta)
    {
        if (damageText == null)
        {
            Debug.LogWarning($"[VisualCard] Cannot show HP delta {hpDelta} for {cardData?.cardName ?? "Unknown"} because damageText is not assigned.");
            return;
        }

        damageText.text = hpDelta < 0 ? hpDelta.ToString() : $"+{hpDelta}";
        damageText.color = GetVisibleDamageTextColor(hpDelta < 0 ? damageTakenColor : damageHealedColor);
        damageText.gameObject.SetActive(true);

        if (damageTextCoroutine != null)
            StopCoroutine(damageTextCoroutine);

        float duration = GameRulesConfig.Instance != null
            ? Mathf.Max(0f, GameRulesConfig.Instance.damageTextDisplayDuration)
            : 1.2f;

        if (duration <= 0f)
        {
            HideDamageText();
            return;
        }

        damageTextCoroutine = StartCoroutine(HideDamageTextAfterDelay(duration));
    }

    private IEnumerator HideDamageTextAfterDelay(float duration)
    {
        yield return new WaitForSeconds(duration);
        HideDamageText();
        damageTextCoroutine = null;
    }

    private void HideDamageText()
    {
        if (damageText == null) return;
        damageText.text = string.Empty;
        damageText.gameObject.SetActive(false);
    }

    private static Color GetVisibleDamageTextColor(Color color)
    {
        if (color.a <= 0f)
            color.a = 1f;
        return color;
    }

    void CheckIfToChangeTextColorIfDarknessType()
    {
        if (PokemonData == null) return;
        Color newColor = Color.black;
        if (PokemonData.type.ToString() == "Darkness") newColor = Color.white;
        SetTextsColor(newColor);
    }

    void ResetTextColor()
    {
        SetTextsColor(Color.black);
    }

    void SetTextsColor(Color color)
    {
        // biggerHpText is intentionally excluded — its color encodes HP %, not card type.
        TMP_Text[] textsToColor = { nameText, hpText, hpPrefix };
        foreach (var txt in textsToColor) if (txt != null) txt.color = color;
    }

    void SetCardArt(string cardNameForPath, string imageName)
    {
        if (cardArt == null) return;
        Sprite spriteToDisplay = null;
        bool isTrainer = PokemonData == null;
        string rootFolder = isTrainer ? "Trainer_images" : "Pokemon_Images";

        if (!string.IsNullOrEmpty(imageName))
        {
            string cleanName = System.IO.Path.GetFileNameWithoutExtension(imageName);

            foreach (string path in BuildCardArtResourcePaths(rootFolder, cardNameForPath, cleanName, isTrainer))
            {
                spriteToDisplay = Resources.Load<Sprite>(path);
                if (spriteToDisplay != null) break;
            }

            if (spriteToDisplay == null)
            {
                spriteToDisplay = LoadSpriteFromDisk(imageName);
            }
        }

        if (spriteToDisplay == null)
        {
            spriteToDisplay = Resources.Load<Sprite>($"{rootFolder}/defaultSprite")
                ?? Resources.Load<Sprite>("Pokemon_Images/defaultSprite");
        }

        if (spriteToDisplay != null) cardArt.sprite = spriteToDisplay;
    }

    private static IEnumerable<string> BuildCardArtResourcePaths(string rootFolder, string cardNameForPath, string cleanName, bool isTrainer)
    {
        if (isTrainer)
        {
            string trainerFolder = GetImageFamilyFolder(cleanName);
            yield return $"{rootFolder}/{trainerFolder}/{cleanName}";
            yield return $"{rootFolder}/{cardNameForPath}/{cleanName}";
            yield return $"{rootFolder}/{cleanName}";
            yield break;
        }

        yield return $"{rootFolder}/{cardNameForPath}/{cleanName}";
        yield return $"{rootFolder}/{GetImageFamilyFolder(cleanName)}/{cleanName}";
        yield return $"{rootFolder}/{cleanName}";
    }

    private static string GetImageFamilyFolder(string cleanName)
    {
        int underscoreIndex = cleanName.LastIndexOf('_');
        if (underscoreIndex <= 0 || underscoreIndex == cleanName.Length - 1)
        {
            return cleanName;
        }

        for (int i = underscoreIndex + 1; i < cleanName.Length; i++)
        {
            if (!char.IsDigit(cleanName[i]))
            {
                return cleanName;
            }
        }

        return cleanName.Substring(0, underscoreIndex);
    }

    private static readonly System.Collections.Generic.Dictionary<string, string> PokemonTypeTemplates = new()
    {
        { "Fire",      "regular_template_fire" },
        { "Water",     "regular_template_water" },
        { "Grass",     "regular_template_grass" },
        { "Lightning", "regular_template_lightning" },
        { "Psychic",   "regular_template_psychic" },
        { "Fighting",  "regular_template_fighting" },
        { "Darkness",  "regular_template_darkness" },
        { "Metal",     "regular_template_metal" },
        { "Dragon",    "regular_template_dragon" },
    };

    private static readonly System.Collections.Generic.Dictionary<string, string> TrainerTypeTemplates = new()
    {
        { "Supporter", "regular_template_supporter" },
        { "Item",      "regular_template_item" },
        { "Stadium",   "regular_template_stadium" },
        { "Tool",      "regular_template_tool" },
    };

    private static readonly string[] StageFolders = { "Templates/Basic_NonEX", "Templates/Stage1_NonEX", "Templates/Stage2_NonEX" };

    void SetCardTemplate(string type)
    {
        if (template == null) return;

        string folderPath;
        string fileName;

        if (type == "Trainer")
        {
            folderPath = "Templates/Trainers";
            string subType = (cardData as TrainerData)?.trainerSubType.ToString() ?? "";
            fileName = TrainerTypeTemplates.TryGetValue(subType, out string t) ? t : "regular_template_item";
        }
        else
        {
            int stage = Mathf.Clamp(cardInstance.pokemonLogic.pokemonData.stage, 0, 2);
            folderPath = StageFolders[stage];
            fileName = PokemonTypeTemplates.TryGetValue(type, out string t) ? t : "regular_template_colorless";
        }

        Sprite templateSprite = Resources.Load<Sprite>($"{folderPath}/{fileName}")
            ?? Resources.Load<Sprite>("Templates/Basic_NonEX/regular_template_colorless");

        if (templateSprite != null) template.sprite = templateSprite;
    }

    private Sprite LoadSpriteFromDisk(string imageName)
    {
        string externalPath = System.IO.Path.Combine(RuntimePaths.CardsRoot(), "IMAGES", imageName);

        // Sprawdzamy czy plik istnieje (obsługa .png lub .jpg)
        if (!System.IO.File.Exists(externalPath)) return null;

        byte[] fileData = System.IO.File.ReadAllBytes(externalPath);
        Texture2D tex = new Texture2D(2, 2);

        if (tex.LoadImage(fileData)) // Dekoduje dane graficzne
        {
            // Tworzymy sprite'a z wczytanej tekstury
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        return null;
    }



    // Ta metoda resetuje kartę i zmienia jej zachowanie po zagraniu na stół
    // Ta metoda zamienia kartę z "karty w ręce" na element planszy
    // Ta metoda zamienia kartę na "martwy" element stosu odrzutków
    // 1. Metoda zamieniająca kartę na element planszy (tej ci teraz brakuje!)
    public void SetOnBoardMode()
    {
        currentMode = VisualMode.OnBoard;
        CacheOriginalVisualTransform();

        if (canvas != null)
        {
            canvas.overrideSorting = false;
            canvas.sortingOrder = originalSortingOrder;
        }

        if (currentAnimation != null)
        {
            StopCoroutine(currentAnimation);
            currentAnimation = null;
        }

        if (visualRoot != null)
        {
            visualRoot.localScale = originalScale;
            visualRoot.localPosition = originalPosition;
        }

        RebuildCostLayouts();
        ApplyBiggerHpVisibility();
    }

    private void RebuildCostLayouts()
    {
        if (retreatCostArea != null)
            RebuildLayoutNowAndNextFrame(retreatCostArea.transform);

        if (attackParent != null)
        {
            foreach (Transform child in attackParent)
            {
                Attack atk = child.GetComponent<Attack>();
                atk?.RebuildEnergyCostLayout();
                atk?.RebuildHeaderLayout();
            }
        }

        // Energy icons attached to the Pokemon also live in a HorizontalLayoutGroup that
        // does not auto-rebuild when the parent card resizes between zones (hand → board,
        // bench → active, etc.). Force a rebuild so icons don't overlap or scatter.
        if (energyParent != null)
            RebuildLayoutNowAndNextFrame(energyParent);
    }

    // 2. Metoda zamieniająca kartę na element stosu odrzutków (robiliśmy ją krok wcześniej)
    public void SetInDiscardMode()
    {
        currentMode = VisualMode.InDiscard;
        CacheOriginalVisualTransform();

        if (canvas != null)
        {
            canvas.overrideSorting = false;
            canvas.sortingOrder = originalSortingOrder;
        }

        if (currentAnimation != null)
        {
            StopCoroutine(currentAnimation);
            currentAnimation = null;
        }

        if (visualRoot != null)
        {
            visualRoot.localScale = originalScale;
            visualRoot.localPosition = originalPosition;
        }

        // If the HP tween is still running (e.g. KO damage just landed), let it finish
        // so the player sees the number drop to 0 before the card hides.
        if (biggerHpAnim != null && isActiveAndEnabled)
            StartCoroutine(HideBiggerHpAfterAnim());
        else
            ApplyBiggerHpVisibility();
    }

    private IEnumerator HideBiggerHpAfterAnim()
    {
        while (biggerHpAnim != null) yield return null;
        ApplyBiggerHpVisibility();
    }


    public void SetVisibility(bool hide)
    {
        if (cardBackImage != null)
        {
            cardBackImage.SetActive(hide);
        }
    }


    public void ToggleHidden()
    {
        if (cardBackImage != null)
        {
            bool currentlyHidden = cardBackImage.activeSelf;
            cardBackImage.SetActive(!currentlyHidden);
        }
    }


    public bool IsHidden()
    {
        if (cardBackImage == null)
            return false; // albo true — zależy od logiki

        return cardBackImage.gameObject.activeSelf;
    }


}


public enum VisualMode 
{ InHand, 
  OnBoard, 
  BigPreview,
  InDiscard
}
