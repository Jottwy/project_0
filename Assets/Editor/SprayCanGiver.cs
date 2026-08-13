#if UNITY_EDITOR
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.MovementSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-068 S3 — mete un bote de spray en el inventario del jugador local, en Play.
    ///
    /// Existe SOLO para poder verificar el slice antes de que S4 ponga el bote en las tablas de
    /// loot: sin esto, la única forma de tener uno en la mano sería encontrarlo, y todavía no
    /// aparece en ningún sitio del mundo.
    ///
    /// Usa <c>IInventory.AddItemsById</c>, el API nativo de STP que ya usa
    /// <c>InventoryRestorer</c>: respeta el enrutado a contenedor, el apilado y las
    /// restricciones del vendor, así que el bote entra exactamente igual que si se hubiera
    /// recogido del suelo. Nada de esto toca el backend ni el save.
    /// </summary>
    public static class SprayCanGiver
    {
        private const string ItemName = "Spray Can";

        [MenuItem("Backrooms/Spray/Dar un bote al jugador", false, 99)]
        private static void Give()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[SprayCanGiver] Solo en Play: el inventario no existe fuera de una partida.");
                return;
            }

            var definition = ItemDefinition.GetWithName(ItemName);
            if (definition == null)
            {
                Debug.LogError($"[SprayCanGiver] No hay ItemDefinition llamada '{ItemName}'. " +
                               "Ejecuta antes 'Backrooms/Spray/Crear bote de spray'.");
                return;
            }

            // LocalPlayerLocator y NO FindFirstObjectByType<Character>: los avatares de los peers
            // remotos y el robapieles TAMBIÉN son Character, y el primero que aparece suele ser
            // uno de ésos — con su Inventory sin contenedores, que revienta dentro de
            // AddItemsById con un NullReferenceException del vendor. Misma resolución que
            // InventoryRestorer, que es el otro sitio del proyecto que escribe en el inventario.
            var motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
            var character = motor != null ? motor.GetComponentInParent<ICharacter>() : null;
            if (character == null)
            {
                Debug.LogError("[SprayCanGiver] No se encuentra al jugador LOCAL. ¿Estás dentro de " +
                               "una partida, no solo en el menú?");
                return;
            }

            var inventory = character.Inventory;
            if (inventory == null || inventory.Containers == null || inventory.Containers.Count == 0)
            {
                Debug.LogError("[SprayCanGiver] El jugador local no tiene inventario inicializado todavía. " +
                               "Espera a que la partida termine de cargar y reintenta.");
                return;
            }

            var (added, reason) = inventory.AddItemsById(definition.Id, 1);
            if (added <= 0)
            {
                Debug.LogError($"[SprayCanGiver] No entró en el inventario: {reason ?? "sin motivo"}. " +
                               "Suele ser que no hay hueco libre.");
                return;
            }

            Debug.Log($"[SprayCanGiver] Bote añadido (def_id={definition.Id}). Equípalo, apunta a una " +
                      "pared a menos de 5 m y mantén el botón izquierdo para pintar. La pintada se " +
                      "manda sola 1,2 s después de soltar.");
        }
    }
}
#endif
