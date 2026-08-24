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
        /// so this file is still the single place that wires the game up. Fixed, hand-picked
        /// anchors, not seed-sorted placement (that is E3's own documented simplification —
        /// see docs/LEVEL4-ROADMAP.md "Nota de ejecución E3"): the Entry anchor sits inside the
        /// flat, always-open starter cluster (Phase 2.6 guarantees no verticality within two
        /// chunks of spawn); the Return anchor is the world-space CENTER of the Level 4 reserve
        /// (`grid_gen::level4::REGION_ORIGIN_CHUNK`/`REGION_CHUNKS`, chunk size 50 m —
        /// (2000,2000)..(2003,2003) chunks ⇒ world (100000,100000)..(100150,100150), center
        /// (100075,100075)) — a placeholder until authored room content (E6) gives the region
        /// something to hang a real door off.
        /// </summary>
        private void SpawnLevel4Doors()
        {
            if (FindFirstObjectByType<Level4DoorTrigger>() != null)
                return; // domain reload / re-entry into the same scene

            const float doorRadius = 2f;

            var entryGo = new GameObject("Level4EntryDoor (ADR-093, placeholder)");
            entryGo.transform.position = new Vector3(3f, 1f, 0f);
            entryGo.AddComponent<Level4DoorTrigger>().Configure(Level4Door.Entry, doorRadius);

            var returnGo = new GameObject("Level4ReturnDoor (ADR-093, placeholder)");
            returnGo.transform.position = new Vector3(100075f, 1f, 100075f);
            returnGo.AddComponent<Level4DoorTrigger>().Configure(Level4Door.Return, doorRadius);
        }
    }
}
