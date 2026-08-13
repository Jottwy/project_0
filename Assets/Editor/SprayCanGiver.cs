#if UNITY_EDITOR
using PolymindGames;
using PolymindGames.InventorySystem;
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

            var character = Object.FindFirstObjectByType<Character>();
            if (character == null || character.Inventory == null)
            {
                Debug.LogError("[SprayCanGiver] No hay jugador con inventario en escena.");
                return;
            }

            var (added, reason) = character.Inventory.AddItemsById(definition.Id, 1);
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
