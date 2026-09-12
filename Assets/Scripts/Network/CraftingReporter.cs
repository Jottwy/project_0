using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-064 enm. 1 (E1.1/E1.5) — le cuenta al backend cada crafteo TERMINADO con la acción
    /// <c>craft_item</c>, para que valide la receta contra el inventario reportado y mute su
    /// espejo. El crafteo en sí lo hace STP en el cliente (descuento del blueprint y alta del
    /// producto, §4 del ADR); esto es un informe, no una petición: fire-and-forget, canal IPC
    /// ordenado, sin <c>request_id</c>, mismo nivel de confianza que <c>consume_item</c>.
    ///
    /// La fuente es el evento estático de <see cref="NetworkedCraftingManager"/>, el sustituto
    /// del gestor del vendor que vive en territorio vendor porque no puede vivir aquí (ver su
    /// doc). Sólo el jugador LOCAL craftea —la UI de crafteo es suya— pero se comprueba igual que
    /// el personaje del evento es el que lleva el motor local, por si algún día un proxy monta el
    /// componente. Regla #3: no muta nada. Se arranca solo; quitable.
    /// </summary>
    public sealed class CraftingReporter : MonoBehaviour
    {
        private static CraftingReporter _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[CraftingReporter]");
            _instance = go.AddComponent<CraftingReporter>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            NetworkedCraftingManager.ItemCrafted += OnItemCrafted;
        }

        private void OnDestroy()
        {
            if (_instance == this)
                NetworkedCraftingManager.ItemCrafted -= OnItemCrafted;
        }

        private static void OnItemCrafted(ICharacter character, ItemDefinition item, int amount)
        {
            if (character == null || item == null || amount <= 0)
                return;
            if (!IsLocalPlayer(character))
                return;
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return; // sin backend no hay a quién informar; el crafteo local ya ocurrió

            ipc.SendCraftItem(item.Id, amount);
        }

        /// <summary>El local es el que lleva el motor de primera persona; un proxy remoto no lo
        /// monta (misma resolución que <see cref="InventoryReporter"/> y <c>DeathLootReporter</c>).</summary>
        private static bool IsLocalPlayer(ICharacter character)
        {
            return character is Component c &&
                   c.GetComponentInParent<PolymindGames.MovementSystem.CharacterControllerMotor>() != null;
        }
    }
}
