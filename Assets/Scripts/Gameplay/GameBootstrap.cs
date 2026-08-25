using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    public sealed class GameBootstrap : MonoBehaviour
    {
        [Header("Debug")]
        [Tooltip("Spawn the robapieles (phantom peer) on the host for play-testing. Forwarded to the runtime-added NetworkInitializer (which injects DEBUG_SPAWN_PHANTOM=1).")]
        [SerializeField] private bool _debugSpawnPhantom = false;

        private void Awake()
        {
            //EnsureComponent<ChunkRenderer>();
            //EnsureComponent<EntityRenderer>();
            //EnsureComponent<ItemRenderer>(); // items v1 desactivados: cubo placeholder sin arte propio.
            EnsureComponent<WorldInteractor>();
            //EnsureComponent<SanityEffects>();
            EnsureComponent<TeleportationVFX>();
            EnsureComponent<MinimapRenderer>();
            EnsureComponent<PoiDebugHud>();
            // PLACEHOLDER (2026-08-15): reloj de muñeca diegético, tecla T. Aditivo — las barras
            // del vendor siguen en pantalla y no se quita ninguna hasta que el reloj demuestre
            // que se lee en movimiento.
            EnsureComponent<WristWatchHandler>();
            EnsureComponent<VerticalDebugMarkerRenderer>();
            EnsureComponent<NetworkInitializer>();
            // ADR-041: turns a local gunshot into a noise the robapieles can hear. Hooks the
            // vendor's own trigger event; harmless when no firearm is equipped.
            EnsureComponent<NoiseReporter>();
            // ADR-046: captura de voz. Arranca CERRADA (el micrófono es opt-in y se persiste en
            // PlayerPrefs), así que añadir el componente no abre nada. Su pareja, RemoteVoicePlayer,
            // NO se añade aquí: vive en Assembly-CSharp porque necesita ProxyAudioCurves, y este
            // asmdef no puede verlo — se auto-registra, como CorpseSpawner.
            EnsureComponent<VoiceCapture>();
            // Panel de ajustes de voz (F7): dispositivo, medidor de nivel y auto-test. Es lo que
            // hace diagnosticable "no me oyen" sin leerse un log de 40 MB.
            EnsureComponent<BackroomsSurvival.UI.VoiceSettingsUI>();
            // EnsureComponent added it to THIS GameObject (it's in no scene/prefab), so forward
            // the inspector toggle before NetworkInitializer launches the backend (in Start).
            //
            // EDITOR-ONLY BY CONSTRUCTION (ADR-016): that ADR deliberately moved the phantom spawn
            // from a hardcoded `true` to an opt-in flag so no normal build auto-spawns. The toggle
            // is a play-test convenience, and gating it here means a scene saved with the box
            // TICKED can be committed without ever leaking the robapieles into a release.
            var ni = GetComponent<NetworkInitializer>();
            if (ni != null)
            {
#if UNITY_EDITOR
                ni.debugSpawnPhantom = _debugSpawnPhantom;
#else
                ni.debugSpawnPhantom = false;
                // Read in both branches on purpose: it keeps the field from going unused in a
                // player build, and it answers the "why did nothing spawn?" question out loud.
                if (_debugSpawnPhantom)
                    Debug.Log("[GameBootstrap] Debug Spawn Phantom is ticked in the scene but IGNORED: " +
                              "builds never auto-spawn the robapieles (ADR-016). Use the editor to play-test it.");
#endif
            }
            // Gate player spawn on the IPC connection (10 s offline fallback lives in
            // GameMode). Restores the always-ready default on teardown, so non-networked
            // scenes are unaffected.
            EnsureComponent<GameBootGateBinder>();
            EnsureComponent<RemotePlayerManager>();
            EnsureComponent<JoinSessionUI>();
            SpawnLevel4Doors();
        }

        private void EnsureComponent<T>() where T : Component
        {
            if (FindFirstObjectByType<T>() == null)
                gameObject.AddComponent<T>();
        }

        /// <summary>
        /// ADR-093 E3: the two Level 4 door triggers. Instantiated (never scene/prefab-authored)
        /// so this file is still the single place that wires the game up.
        ///
        /// The Entry anchor sits inside the flat, always-open starter cluster (Phase 2.6
        /// guarantees no verticality within two chunks of spawn).
        ///
        /// The Return anchor is the centre of the region's ENTRY HALL — the fixed 8×8-cell room
        /// (`grid_gen::level4::ENTRY_HALL`) that every epoch's layout contains verbatim, which is
        /// also where crossing the Entry door drops you. It replaced the reserve's GEOMETRIC
        /// centre, which the room draw could leave solid — an exit door buried inside rock.
        ///
        /// MIRRORS de `grid_gen::level4`, y la aritmética tiene que ir en paso: chunk de origen
        /// (200, 0) × 50 m = mundo (10000, 0); centro del vestíbulo en la celda (30, 30) × 2,5 m
        /// = +75 m por lado; capa 0 ⇒ suelo en Y 0. El desparejo lo caza un test en Rust
        /// (`the_entry_hall_matches_the_hardcoded_csharp_door_anchor`), no la buena suerte.
        ///
        /// La de VUELTA no va en el punto de aterrizaje sino 5 m al norte
        /// (`level4::return_door_world_pos`, `RETURN_DOOR_OFFSET_M`): plantarla justo donde
        /// apareces te deja dentro de su plano el frame de llegada, que es lo que obligaba al
        /// enfriamiento de 3 s que la detección por cruce ya no necesita.
        ///
        /// Las dos miran al SUR (−Z), o sea: aterrizas mirando la de vuelta de frente.
        /// </summary>
        private void SpawnLevel4Doors()
        {
            if (FindFirstObjectByType<Level4DoorTrigger>() != null)
                return; // domain reload / re-entry into the same scene

            var facing = new Vector3(0f, 0f, -1f);

            var entryGo = new GameObject("Level4EntryDoor (ADR-093, placeholder)");
            entryGo.transform.position = new Vector3(3f, 0f, 0f);
            entryGo.AddComponent<Level4DoorTrigger>().Configure(Level4Door.Entry, facing);

            var returnGo = new GameObject("Level4ReturnDoor (ADR-093, placeholder)");
            returnGo.transform.position = new Vector3(10075f, 0f, 70f);
            returnGo.AddComponent<Level4DoorTrigger>().Configure(Level4Door.Return, facing);
        }
    }
}
