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

        [Header("WorldGen3 (ADR-106)")]
        [Tooltip("Arranca el backend sirviendo WorldGen3 en vez de WG2. Se reenvía al " +
                 "NetworkInitializer, que es quien lanza el proceso con BACKROOMS_WG3=1. " +
                 "Necesita el manifiesto exportado en StreamingAssets/wg3_manifest.json " +
                 "(Backrooms ▸ WorldGen3 ▸ Exportar manifiesto).")]
        [SerializeField] private bool _enableWorldGen3 = false;

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
                // ADR-106 — y AQUÍ el interruptor de WorldGen3, por el mismo motivo y en el mismo
                // sitio que el del fantasma: `NetworkInitializer` no vive en la escena, lo crean en
                // runtime `AutoConnect` / `NetworkMenuBootstrap` / `JoinSessionUI`, así que su propia
                // casilla del inspector **no se puede marcar en ninguna parte** y `enableWorldGen3`
                // nacía siempre en false. Sin esto, WG3 no se puede encender en una sesión normal:
                // el backend arranca en WG2, el saludo dice `wg3_enabled: false`, y el cliente monta
                // el mundo de siempre — que es exactamente el síntoma de «no carga WorldGen3».
                //
                // A diferencia del fantasma, esto NO se apaga en build: servir WG3 es una decisión
                // de mundo, no una comodidad de depuración.
                // ADR-109: el defecto de `NetworkInitializer` es ENCENDIDO, así que esta línea ya no
                // enciende nada — sólo puede APAGAR. Y apagarlo hoy no devuelve «el mundo anterior»:
                // con la etapa 1 de la retirada, WG2 ya no se genera ni se manda. Por eso el aviso:
                // una escena que sirva un mundo muerto tiene que decirlo, no descubrirse jugando.
                //
                // Se conserva la escritura en los dos sentidos porque las escenas de prueba de WG2
                // siguen queriendo apagarlo a propósito — lo que no puede es pasar en silencio.
                if (!_enableWorldGen3 && ni.enableWorldGen3)
                {
                    Debug.LogWarning(
                        "[GameBootstrap] Esta escena APAGA WorldGen3 (casilla 'Enable World Gen 3' " +
                        "sin marcar). Desde ADR-109 el backend en WG2 no genera nada: si no es una " +
                        "escena de prueba del mundo viejo, marca la casilla.");
                }
                ni.enableWorldGen3 = _enableWorldGen3;
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
        /// Ninguna de las dos va EN su punto de aterrizaje, sino `RETURN_DOOR_OFFSET_M` detrás
        /// (`level4::return_door_world_pos` / `entry_door_arrival_pos`): plantarla justo donde
        /// apareces te deja dentro de su plano el frame de llegada, que es lo que obligaba al
        /// enfriamiento de 3 s que la detección por cruce ya no necesita.
        ///
        /// ORIENTACIONES OPUESTAS, y es lo que hace funcionar el portal. Cada puerta mira hacia
        /// su CARA FRONTAL: la de entrada hacia el spawn (−Z), porque es por donde se llega a
        /// ella; la de vuelta hacia el centro del vestíbulo (+Z), porque es por donde se sale.
        /// La cámara del portal se coloca detrás de la gemela mirando hacia SU cara frontal, así
        /// que orientar las dos igual hace que enseñe la pared del fondo en vez del sitio al que
        /// vas. Cada punto de aterrizaje cae en la cara frontal de su puerta, y un test en Rust
        /// ata las dos cosas.
        /// </summary>
        private void SpawnLevel4Doors()
        {
            if (FindFirstObjectByType<Level4DoorTrigger>() != null)
                return; // domain reload / re-entry into the same scene

            // Centro del tile (0,2) del chunk (0,0): ese tile forma con (0,1) y (0,3) un tramo
            // norte-sur continuo, así que el marco exento se cruza por los DOS lados. El ancla
            // vieja (3,0,0) caía en un fondo de saco con pared al norte, este y oeste — media
            // puerta daba contra roca. Medido con la sonda de `walls`, no deducido.
            var entryGo = new GameObject("Level4EntryDoor (ADR-093, placeholder)");
            entryGo.transform.position = new Vector3(2.5f, 0f, 12.5f);
            var entry = entryGo.AddComponent<Level4DoorTrigger>();
            entry.Configure(Level4Door.Entry, new Vector3(0f, 0f, -1f));

            var returnGo = new GameObject("Level4ReturnDoor (ADR-093, placeholder)");
            returnGo.transform.position = new Vector3(10075f, 0f, 70f);
            var back = returnGo.AddComponent<Level4DoorTrigger>();
            back.Configure(Level4Door.Return, new Vector3(0f, 0f, 1f));

            // El par: cada puerta enseña por su hueco lo que hay al otro lado de la otra. Se
            // empareja aquí y no en Configure porque hasta esta línea no existen las dos.
            entry.PairWith(back);
            back.PairWith(entry);
        }
    }
}
