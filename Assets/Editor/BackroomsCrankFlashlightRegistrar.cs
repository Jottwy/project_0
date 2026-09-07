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
    /// ADR-133 — lo único que separa «la linterna existe» de «la linterna se puede empuñar».
    ///
    /// EL FALLO QUE CIERRA: equipar la linterna no sacaba nada en la mano, y el backend no tenía
    /// culpa (la linterna no toca el wire). <c>WieldableInventory.OnBehaviourStart</c> llama UNA vez
    /// a <c>InitializeWieldablesCache</c>, que escanea los hijos directos del root de wieldables
    /// del jugador buscando <c>WieldableItem</c> YA instanciados y monta con ellos el diccionario
    /// item→wieldable. No es una lista de prefabs que se resuelva bajo demanda: un prefab que no
    /// cuelgue de ahí no entra, y equipar su item se queda en silencio. Es exactamente lo que le
    /// pasó al bote (ADR-068 S3) y lo que arregla <see cref="SprayCanWieldableRegistrar"/>, que
    /// por eso expone <c>RegisterWieldablePrefab</c> para cualquier wieldable nuestro.
    ///
    /// COSTE ACEPTADO, EL MISMO QUE EL BOTE: `FPS_Player.prefab` y `STP_Player.prefab` son del
    /// VENDOR. Un reimport del `.unitypackage` de PolymindGames se lleva esta alta en silencio y la
    /// linterna deja de equiparse sin error. Si pasa, se vuelve a ejecutar este menú.
    ///
    /// Y el «dar una linterna», calcado de <see cref="SprayCanGiver"/>: mientras
    /// `RestrictCacheCatalog` siga en true la linterna no sale en el mundo, y sin esto la única
    /// forma de tener una en la mano es no tenerla.
    /// </summary>
    public static class BackroomsCrankFlashlightRegistrar
    {
        private const string ItemName = "Crank Flashlight";
        private const string NodeName = "BR_Wieldable_CrankFlashlight";

        [MenuItem("Backrooms/Linterna/Registrar linterna en el jugador", false, 94)]
        public static void Register()
            => SprayCanWieldableRegistrar.RegisterWieldablePrefab(
                BackroomsCrankFlashlightCreator.PrefabPath, NodeName);

        [MenuItem("Backrooms/Linterna/Dar una linterna al jugador", false, 95)]
        private static void Give()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[CrankFlashlightGiver] Solo en Play: el inventario no existe fuera de una partida.");
                return;
            }

            var definition = ItemDefinition.GetWithName(ItemName);
            if (definition == null)
            {
                Debug.LogError($"[CrankFlashlightGiver] No hay ItemDefinition llamada '{ItemName}'. " +
                               "Ejecuta antes 'Backrooms/Linterna/Crear linterna de manivela'.");
                return;
            }

            // LocalPlayerLocator y NO FindFirstObjectByType<Character>: los proxies y el robapieles
            // también son Character, con un Inventory sin contenedores que revienta en AddItemsById.
            var motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
            var character = motor != null ? motor.GetComponentInParent<ICharacter>() : null;
            if (character == null)
            {
                Debug.LogError("[CrankFlashlightGiver] No se encuentra al jugador LOCAL. ¿Estás dentro de " +
                               "una partida, no solo en el menú?");
                return;
            }

            var inventory = character.Inventory;
            if (inventory == null || inventory.Containers == null || inventory.Containers.Count == 0)
            {
                Debug.LogError("[CrankFlashlightGiver] El jugador local no tiene inventario todavía. " +
                               "Espera a que la partida termine de cargar y reintenta.");
                return;
            }

            var (added, reason) = inventory.AddItemsById(definition.Id, 1);
            if (added <= 0)
            {
                Debug.LogError($"[CrankFlashlightGiver] No entró en el inventario: {reason ?? "sin motivo"}. " +
                               "Suele ser que no hay hueco libre.");
                return;
            }

            Debug.Log($"[CrankFlashlightGiver] Linterna añadida (def_id={definition.Id}). Equípala: botón " +
                      "derecho enciende/apaga, mantener el izquierdo da cuerda (la barra sube a saltos, " +
                      "andas al 40 % y la luz parpadea).");
        }
    }
}
#endif
