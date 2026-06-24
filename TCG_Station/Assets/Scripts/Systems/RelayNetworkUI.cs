using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.UI;
using TMPro; // Wa�ne dla TextMeshPro

public class RelayNetworkUI : MonoBehaviour
{
    [Header("UI References")]
    public Button hostButton;
    public Button clientButton;
    public TMP_InputField joinCodeInput; // Tu klient wpisuje kod
    public TMP_Text joinCodeText;        // Tu hostowi wy�wietli si� kod do podania koledze

    // Inicjalizacja us�ug Unity (Wymagane do dzia�ania Relaya)
    private async void Awake()
    {
        try
        {
            await UnityServices.InitializeAsync();

            // Logowanie anonimowe (ka�dy gracz musi by� "zalogowany" w us�ugach Unity)
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            Debug.Log($"Zalogowano do Unity Services jako: {AuthenticationService.Instance.PlayerId}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"B��d inicjalizacji Unity Services: {e.Message}");
            return;
        }

        // Podpinamy przyciski
        hostButton.onClick.AddListener(StartHostWithRelay);
        clientButton.onClick.AddListener(StartClientWithRelay);
    }

    // --- LOGIKA HOSTA (Tworzenie serwera) ---
    private async void StartHostWithRelay()
    {
        try
        {
            // 1. Tworzymy alokacj� na serwerach Unity (dla 2 graczy: Ty + Przeciwnik)
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(2);

            // 2. Pobieramy kod ��czenia (np. "A1B2C")
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            Debug.Log($"<color=green>HOST UTWORZONY. Kod: {joinCode}</color>");
            if (joinCodeText != null) joinCodeText.text = $"KOD: {joinCode}";

            // 3. Konfigurujemy Unity Transport, �eby u�ywa� Relaya zamiast zwyk�ego IP
            // (To jest standardowa konfiguracja UTP pod Relay)
            var unityTransport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            unityTransport.SetRelayServerData(
                allocation.RelayServer.IpV4,
                (ushort)allocation.RelayServer.Port,
                allocation.AllocationIdBytes,
                allocation.Key,
                allocation.ConnectionData,
                null, // Host nie potrzebuje "HostConnectionData"
                isSecure: false // Dajemy false dla prostoty (http), true wymaga certyfikat�w (dtls)
            );

            // 4. Startujemy Hosta w Netcode
            NetworkManager.Singleton.StartHost();

            // --- TWOJA STARA LOGIKA ---
            // Poniewa� jeste�my hostem, musimy r�cznie "o�ywi�" obiekty sieciowe na scenie

            // A. BattleManager (je�li masz go na li�cie NetworkPrefabs, zespawnuje si� sam, 
            // ale je�li le�y na scenie - trzeba go zespawnowa�).
            var bm = FindFirstObjectByType<BattleManager>();
            if (bm != null && bm.GetComponent<NetworkObject>() != null)
            {
                bm.GetComponent<NetworkObject>().Spawn();
            }

            // Ukrywamy UI ��czenia
            gameObject.SetActive(false);
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"Relay Host Error: {e.Message}");
        }
    }

    // --- LOGIKA KLIENTA (Do��czanie) ---
    private async void StartClientWithRelay()
    {
        try
        {
            string joinCode = joinCodeInput.text;
            if (string.IsNullOrEmpty(joinCode))
            {
                Debug.LogWarning("Wpisz kod do��czenia!");
                return;
            }

            Debug.Log($"Pr�ba do��czenia z kodem: {joinCode}...");

            // 1. Do��czamy do alokacji u�ywaj�c kodu
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            // 2. Konfigurujemy Transport danymi, kt�re dostali�my z chmury
            var unityTransport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            unityTransport.SetRelayServerData(
                joinAllocation.RelayServer.IpV4,
                (ushort)joinAllocation.RelayServer.Port,
                joinAllocation.AllocationIdBytes,
                joinAllocation.Key,
                joinAllocation.ConnectionData,
                joinAllocation.HostConnectionData, // Klient potrzebuje danych hosta
                isSecure: false
            );

            // 3. Startujemy Klienta
            NetworkManager.Singleton.StartClient();

            // Ukrywamy UI
            gameObject.SetActive(false);
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"Relay Client Error: {e.Message}. Sprawd� czy kod jest poprawny.");
        }
    }
}