#if UNITY_EDITOR || DEVELOPMENT_BUILD
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Gameplay.Debug_
{
    /// <summary>
    /// AYUDA DE PLAYTEST — F6: destornillador al inventario y salto al hueco de oficina más
    /// cercano, para probar ADR-145 (atrezo de oficina desmontable/looteable) sin recorrer el
    /// mundo a pie buscando una.
    ///
    /// Mismo patrón que <c>FreeBuildMode</c> (F9): compilado SOLO en editor y en builds de
    /// desarrollo, tecla leída directa del Input System (nada de acción del vendor ni mapa de
    /// controles), sin tocar backend ni protocolo — el destornillador se da por el mismo camino
    /// que ya usa <c>InventoryRestorer</c> para reponer inventario, y el salto usa la extensión
    /// del vendor <c>ICharacter.SetPositionAndRotation</c> (mueve el motor, no el transform a
    /// pelo) para no dejar el `CharacterController` en un estado inconsistente.
    ///
    /// La búsqueda de oficina recorre chunks en anillos crecientes desde el jugador
    /// (<see cref="Wg3ChunkStreamer.TryGetStyle"/>, estilo 0 = oficina — mismo mapeo que
    /// <c>ChunkContainerRoll.PropForStyle</c>/<c>ChunkDismantleRoll.PropForStyle</c>) y sólo mira
    /// chunks ya montados: no fuerza a WG3 a generar nada por delante del streaming normal.
    /// </summary>
    public sealed class Adr145PlaytestHelper : MonoBehaviour
    {
        private const string ScrewdriverName = "Screwdriver";

        /// <summary>Anillos de chunk a mirar antes de rendirse. Con radio de streaming normal,
        /// ~31 % de los chunks son oficina (ADR-103 enm. 2): 6 anillos (169 chunks) es de sobra.</summary>
        private const int MaxRingRadius = 6;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<Adr145PlaytestHelper>() != null)
                return;

            var go = new GameObject("[Adr145PlaytestHelper]");
            go.AddComponent<Adr145PlaytestHelper>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f6Key.wasPressedThisFrame)
                RunHelper();
        }

        private void RunHelper()
        {
            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
            if (character == null)
            {
                UnityEngine.Debug.LogWarning("[ADR145] sin jugador local todavía (¿estás en Play?).");
                return;
            }

            string message = GiveScrewdriver(character) + "\n" + JumpToNearestOffice(character);
            MessageDispatcher.Instance.Dispatch(character, MsgType.Info, message);
        }

        private static string GiveScrewdriver(ICharacter character)
        {
            var def = ItemDefinition.GetWithName(ScrewdriverName);
            if (def == null)
                return "ADR145: destornillador no encontrado en el catálogo.";

            var (added, rejectReason) = character.Inventory.AddItemsById(def.Id, 1);
            if (added <= 0)
                return $"ADR145: no cupo el destornillador ({rejectReason}).";

            // AddItemsById sólo lo mete en la mochila (Inventory.cs no llama a SelectAtIndex en
            // ningún punto) — equiparlo es SIEMPRE una acción aparte del jugador (tecla de hotbar,
            // clic en "Equip", o recogerlo del mundo por WieldableItemPickup). Sin esto el
            // jugador sigue golpeando con lo que tuviera antes en la mano, y el destornillador se
            // queda de adorno en la mochila — exactamente lo que pasó en el primer playtest real.
            var holster = character.Inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.WieldableTag));
            var wieldableInventory = character.GetCC<IWieldableInventoryCC>();
            if (holster == null || wieldableInventory == null)
                return "ADR145: destornillador en la mochila, sin holster/CC para equiparlo solo (equípalo a mano).";

            var slot = holster.FindSlot(ItemSlotFilters.WithItemId(def.Id));
            if (!slot.IsValid())
                return "ADR145: destornillador en la mochila, no entró en el holster (equípalo a mano).";

            wieldableInventory.SelectAtIndex(slot.Index, false);
            return "ADR145: destornillador equipado en la mano.";
        }

        private static string JumpToNearestOffice(ICharacter character)
        {
            var streamer = BackroomsSurvival.WorldGen3.Wg3ChunkStreamer.Active;
            if (streamer == null)
            {
                UnityEngine.Debug.LogWarning("[ADR145] Wg3ChunkStreamer.Active es null (¿WG3 apagado?).");
                return "ADR145: WG3 no está activo, no se puede buscar oficina.";
            }

            float side = BackroomsSurvival.WorldGen3.Wg3ChunkStreamer.ChunkSize;
            Vector3 from = character.transform.position;
            int px = Mathf.FloorToInt(from.x / side);
            int pz = Mathf.FloorToInt(from.z / side);

            for (int ring = 0; ring <= MaxRingRadius; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        // Sólo el BORDE del anillo: el interior ya se miró en un anillo anterior.
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring)
                            continue;

                        Vector3 centre = new Vector3((px + dx + 0.5f) * side, from.y, (pz + dz + 0.5f) * side);
                        if (!streamer.TryGetStyle(centre, out byte style) || style != 0)
                            continue;

                        if (!BackroomsSurvival.Net.LootPlacement.TryFindWalkablePoint(centre, 0f, side * 0.4f, out Vector3 at))
                            continue;

                        character.SetPositionAndRotation(at + Vector3.up * 0.2f, character.transform.rotation);
                        return $"ADR145: oficina encontrada a {ring} chunk(s), saltado.";
                    }
                }
            }

            return $"ADR145: ninguna oficina montada en {MaxRingRadius} anillos — camina un poco y reintenta (F6).";
        }
    }
}
#endif
