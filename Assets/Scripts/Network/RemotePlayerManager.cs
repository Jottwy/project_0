using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Gameplay.GridWorld;
using TMPro;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    public sealed class RemotePlayerManager : MonoBehaviour
    {
        [Header("Prefab Settings")]
        [Tooltip("If null, a default capsule is created at runtime.")]
        public GameObject remotePlayerPrefab;

        [Tooltip("ADR-094: prefab for a species==1 (adult faceling) peer. If null, falls back to " +
                 "remotePlayerPrefab — the same peer just looks like a default player until this " +
                 "is built (Backrooms ▸ Facelings ▸ Build Adult Avatar Prefab).")]
        public GameObject facelingAdultPrefab;

        [Tooltip("ADR-094: prefab for a species==2 (child faceling) peer. If null, falls back to " +
                 "remotePlayerPrefab (Backrooms ▸ Facelings ▸ Build Child Avatar Prefab).")]
        public GameObject facelingChildPrefab;

        [Header("Interpolation")]
        [Min(0f)] public float positionSmoothing = 22f;

        [Tooltip("DEPRECATED (kept only so existing scenes don't lose a serialized value): the old " +
                 "exponential yaw factor. Yaw now uses SmoothDampAngle with rotationSmoothTime; this " +
                 "field is unused.")]
        [Min(0f)] public float rotationSmoothing = 18f;

        [Tooltip("Yaw smoothing as seconds-to-target (SmoothDampAngle). Lower = snappier.")]
        [Min(0f)] public float rotationSmoothTime = 0.1f;

        [Tooltip("If the yaw error exceeds this (degrees) the proxy SNAPS instead of sweeping " +
                 "(respawn / chunk displacement / instant 180° turns).")]
        [Min(0f)] public float yawSnapThreshold = 120f;

        [Tooltip("If the position error exceeds this (metres) the proxy SNAPS instead of " +
                 "interpolating. Covers re-entry into the pose area of interest (ADR-074), " +
                 "respawn and chunk displacement. At sprint speed a peer only moves ~0.73 m " +
                 "between 10 Hz poses, so several metres is always a discontinuity.")]
        [Min(0f)] public float positionSnapThreshold = 5f;

        [Header("Name Tag")]
        [Min(0f)] public float nameTagHeight = 2.2f;
        [Min(0.1f)] public float nameTagFontSize = 3f;

        [Header("Default Avatar")]
        public Color defaultAvatarColor = new Color(0.3f, 0.6f, 1f, 1f);
        public Color remoteMarkerColor = new Color(0.1f, 0.95f, 1f, 1f);
        [Min(0f)] public float missingRemoteGraceSeconds = 3f;

        private readonly Dictionary<int, RemotePlayerView> _active = new Dictionary<int, RemotePlayerView>();
        private readonly Queue<RemotePlayerView> _pool = new Queue<RemotePlayerView>();
        private readonly HashSet<int> _idsThisFrame = new HashSet<int>();
        private readonly List<int> _toRemove = new List<int>();
        private float _nextReceiveLogTime;
        private float _nextProxyLogTime;
        private float _nextUpdateLogTime;
        private IPCClient _ipc;

        public IReadOnlyDictionary<int, RemotePlayerView> ActivePlayers => _active;
        public int ActiveCount => _active.Count;
        public int PoolCount => _pool.Count;

        private void OnDisable()
        {
            if (_ipc != null)
                _ipc.RemoveStateListener(OnWorldState);

            _ipc = null;
        }

        private void TrySubscribe()
        {
            if (_ipc != null || !IPCClient.TryGetInstance(out var ipc))
                return;

            _ipc = ipc;
            _ipc.AddStateListener(OnWorldState);
        }

        private void OnWorldState(WorldStateMsg state)
        {
            if (state != null)
                UpdateFromWorldState(state.remotePlayers);
        }

        /// Comma-joined remote ids for the MPTRACE logs. Called only from the throttled/cold log
        /// paths — it used to be built unconditionally on every state update and thrown away.
        private static string JoinIds(List<RemotePlayerMsg> remotePlayers)
        {
            return string.Join(",", remotePlayers.ConvertAll(r => r.id.ToString()));
        }

        /// <summary>
        /// Cuánto por detrás del presente se dibuja a los demás. Es el mismo principio que el
        /// «frame pacing» de un juego a 30 fps clavados: **lo que se nota no es la tasa, es la
        /// irregularidad**. Persiguiendo la última pose recibida, el proxy reproduce fielmente el
        /// jitter de la red; retrasando el dibujo lo justo para tener siempre DOS muestras que
        /// rodeen el instante que toca, sale movimiento a ritmo constante aunque los paquetes
        /// lleguen a trompicones.
        ///
        /// 150 ms sale de lo medido el 10-09 en partida real por Steam: poses cada 100 ms y una
        /// varianza de latencia que llegó a **70 ms** (`Max latency variance` en el diagnóstico de
        /// Valve). El retardo tiene que cubrir un intervalo de envío más el jitter, o el buffer se
        /// queda seco justo cuando más falta hace. Es el precio: se ve a los demás 150 ms en el
        /// pasado — irrelevante aquí, donde nada se resuelve por posición del cliente (el disparo
        /// lo valida el host contra su roster).
        /// </summary>
        public const float InterpolationDelay = 0.15f;

        /// <summary>
        /// Pose de <paramref name="view"/> en el instante «ahora − <see cref="InterpolationDelay"/>»,
        /// interpolada entre las dos muestras que lo rodean. Devuelve false mientras no haya dos
        /// (proxy recién creado, o un hueco largo sin recibir), y entonces el llamante se queda con
        /// el último valor conocido.
        ///
        /// De paso descarta lo ya consumido: se conserva UNA muestra por detrás del instante
        /// dibujado, que es la que hace de extremo izquierdo de la interpolación.
        /// </summary>
        private static bool TrySamplePlaybackPose(RemoteView view, out Vector3 position, out float yaw)
        {
            position = default;
            yaw = default;

            var samples = view.samples;
            float renderTime = Time.unscaledTime - InterpolationDelay;

            int newestOlder = -1;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].time <= renderTime)
                    newestOlder = i;
                else
                    break; // llegan en orden, así que la primera posterior corta la búsqueda
            }

            if (newestOlder < 0 || newestOlder + 1 >= samples.Count)
                return false; // sin par que rodee el instante: aún no, o nos hemos quedado secos

            if (newestOlder > 0)
                samples.RemoveRange(0, newestOlder);

            var older = samples[0];
            var newer = samples[1];

            float span = newer.time - older.time;
            // Dos muestras con el mismo sello no definen un tramo; se toma la más nueva y ya.
            float t = span > 1e-5f ? Mathf.Clamp01((renderTime - older.time) / span) : 1f;

            position = Vector3.Lerp(older.position, newer.position, t);
            yaw = Mathf.LerpAngle(older.yaw, newer.yaw, t);
            return true;
        }

        /// Cuántas de estas entradas son PERSONAS. El resto son criaturas, que comparten stream y
        /// mensaje con los jugadores (ver <see cref="NetIdentity.CreatureIdBase"/>): contarlas
        /// juntas es lo que hacía que el log dijera 24 conectados habiendo uno.
        internal static int CountHumans(List<RemotePlayerMsg> remotePlayers)
        {
            int humans = 0;
            for (int i = 0; i < remotePlayers.Count; i++)
                if (NetIdentity.IsHuman(remotePlayers[i].id))
                    humans++;
            return humans;
        }

        /// <summary>
        /// Si una entrada de `world_state.remote_players` debe tener proxy. **Siempre sí**, y el
        /// parámetro del id local se conserva sólo para que el nombre diga qué NO se hace.
        ///
        /// **El backend es la autoridad sobre quién es remoto.** `build_world_state`
        /// (`game_loop.rs:7185`) recorre `net.peers`, y un nodo nunca se registra a sí mismo como
        /// peer: `allocate_peer_id` evita `self.local_id` y el manejador de `PeerList` hace
        /// `if info.id == self.local_id { continue }`. La lista que llega por IPC **ya excluye al
        /// local**.
        ///
        /// Aquí había un segundo filtro contra `NetworkInitializer.LastSelectedNetId`, y no podía
        /// aportar nada correcto — sólo quitar. Ese campo guarda el id que Unity **propuso** por
        /// `NET_ID`; el de verdad lo **asigna el host** (`allocate_peer_id`) y lo adopta el backend
        /// del joiner (`self.local_id = assigned_id`), **sin que nada se lo cuente a Unity**:
        /// `WorldState` no tiene ningún campo con el id local. Cuando el propuesto y el asignado
        /// difieren y el propuesto coincide con el de un peer real, ese peer **desaparecía en
        /// silencio**. El valor por defecto de `NetworkInitializer.netId` es **1**, que es el id
        /// del host — de ahí el síntoma reportado: «el host es invisible para los joiners» mientras
        /// los joiners se veían entre sí.
        ///
        /// Regresión: `RemotePlayerRosterTests`.
        /// </summary>
        public static bool ShouldTrackRemote(int remoteId, int unitySelfId) => true;

        public void UpdateFromWorldState(List<RemotePlayerMsg> remotePlayers)
        {
            if (remotePlayers == null)
                return;

            int selfId = NetIdentity.Local;

            if (Time.unscaledTime >= _nextReceiveLogTime)
            {
                Debug.Log($"[RemotePlayerManager] remote count={remotePlayers.Count} (personas={CountHumans(remotePlayers)})");
                Debug.Log($"MPTRACE step=K event=remote_player_manager_receive self_id={selfId} sender_id=<none> assigned_id=<none> peer_id=<none> endpoint=<unity> peer_count=<unknown> players={CountHumans(remotePlayers)} remote_players_count={remotePlayers.Count} remote_players_ids=[{JoinIds(remotePlayers)}]");
                _nextReceiveLogTime = Time.unscaledTime + 2f;
            }

            // Per-proxy diagnostic logs used to run EVERY frame per remote (one string alloc plus
            // a GetComponentsInChildren each). Same 2 s cadence as the receive log above: the
            // traces stay available, the per-frame cost does not.
            bool logProxy = Time.unscaledTime >= _nextProxyLogTime;
            if (logProxy)
                _nextProxyLogTime = Time.unscaledTime + 2f;

            _idsThisFrame.Clear();

            foreach (var rp in remotePlayers)
            {
                if (rp == null)
                    continue;

                if (!ShouldTrackRemote(rp.id, selfId))
                {
                    if (logProxy)
                        Debug.Log($"[RemotePlayerManager] ignored local id={rp.id}");
                    continue;
                }

                // Un peer TODAVÍA SIN POSE llega en el origen literal, y crear su proxy ahí lo
                // planta en (0,0,0) hasta que llegue la primera pose de verdad: es el bug que se
                // veía como «al conectarse tarda en actualizar su posición». Medido el 10-09 en
                // partida real — `spawned id=23612, name=jottwydev, pos=(0.00, 0.00, 0.00)` para
                // quien entraba, mientras el que ya estaba dentro nacía en su sitio.
                //
                // El origen no es una posición legítima que se pueda confundir con ésta: el emisor
                // lo descarta explícitamente antes de enviar (`PlayerPoseTransmitter`, «discard the
                // literal origin only»), así que en el wire sólo significa «aún no sé dónde estoy».
                // Se espera a la primera pose real; sin ella no hay nada que dibujar en su sitio.
                if (rp.position == Vector3.zero)
                {
                    if (logProxy)
                        Debug.Log($"[RemotePlayerManager] id={rp.id} sin pose todavía (origen): no se crea proxy aún");
                    continue;
                }

                _idsThisFrame.Add(rp.id);

                // Bug fix (remote proxy floating): the backend relays the player-pivot Y
                // (transform sits PlayerBaseY above the floor — see GridConstants.PlayerBaseY),
                // but the RemotePlayerAvatar root pivot is at the FEET. Drop the pose to the floor
                // by subtracting PlayerBaseY so the feet land on the rendered floor; ProxyGroundingHook
                // then only absorbs the small render/backend residual (< groundSnapMax).
                Vector3 groundedPosition = rp.position;
                groundedPosition.y -= GridConstants.PlayerBaseY;

                if (!_active.TryGetValue(rp.id, out var view))
                {
                    Debug.Log(
                        $"MPTRACE step=PVP event=remote_proxy_create_begin remote_id={rp.id} name={rp.name} " +
                        $"pos=({groundedPosition.x:F2},{groundedPosition.y:F2},{groundedPosition.z:F2})");
                    view = Acquire(rp.id, rp.name, rp.species);
                    _active[rp.id] = view;
                    if (view.root != null)
                    {
                        view.root.position = groundedPosition;
                        view.root.rotation = Quaternion.Euler(0f, rp.rotation, 0f);
                    }
                    Debug.Log(
                        $"[RemotePlayerManager] spawned id={rp.id}, name={rp.name}, " +
                        $"pos={groundedPosition}");
                    Debug.Log($"MPTRACE step=K event=remote_player_manager_spawn self_id={selfId} sender_id=<none> assigned_id=<none> peer_id={rp.id} endpoint=<unity> peer_count=<unknown> players={CountHumans(remotePlayers)} remote_players_count={remotePlayers.Count} remote_players_ids=[{JoinIds(remotePlayers)}]");
                }
                else if (logProxy)
                {
                    int handlerCount = view.root != null
                        ? view.root.GetComponentsInChildren<RemotePvpHitbox>(true).Length
                        : 0;
                    Debug.Log(
                        $"MPTRACE step=PVP event=remote_proxy_update remote_id={rp.id} " +
                        $"root={(view.root != null ? view.root.name : "<null>")} pvp_hitboxes={handlerCount}");
                }

                view.targetPosition = groundedPosition;
                view.targetRotation = rp.rotation;
                // Se guarda la pose con su hora de llegada en vez de perseguirla directamente: el
                // dibujo va por detrás y la reproduce a ritmo constante (ver InterpolationDelay).
                view.samples.Add(new RemoteView.PoseSample
                {
                    time = Time.unscaledTime,
                    position = groundedPosition,
                    yaw = rp.rotation,
                });
                // Cota dura por si el consumidor no drenara (proxy fuera de pantalla, pausa larga):
                // el buffer nunca es historia, sólo el tramo que hace falta para interpolar.
                if (view.samples.Count > 16)
                    view.samples.RemoveRange(0, view.samples.Count - 16);
                view.animationState = string.IsNullOrWhiteSpace(rp.animation) ? "idle" : rp.animation;
                view.crouch = rp.crouch; // ADR-020
                view.pitch = rp.pitch;   // ADR-021
                view.equipment = rp.equipment; // ADR-022 (rp is fresh per parse → no aliasing)
                view.heldItem = rp.heldItem; // ADR-023
                view.hitSeq = rp.hitSeq; // ADR-024
                view.revealed = rp.revealed; // ADR-038
                view.lightOn = rp.lightOn; // ADR-042
                view.fireSeq = rp.fireSeq; // ADR-042
                view.buttons = rp.buttons; // ADR-044
                view.meleeSeq = rp.meleeSeq; // ADR-044
                view.vocalSeq = rp.vocalSeq; // ADR-048
                view.vocalKind = rp.vocalKind; // ADR-048
                view.carryDef = rp.carryDef; // ADR-049
                view.carryCount = rp.carryCount; // ADR-049
                view.species = rp.species; // ADR-094
                // ADR-028 post-E3: hide the standing proxy while its owner is dead (the corpse
                // is the visible body); it reappears at the respawn position on dead→false.
                // Change-detected so SetActive only fires on the edge.
                if (view.dead != rp.dead)
                {
                    view.dead = rp.dead;
                    if (view.root != null)
                        view.root.gameObject.SetActive(!rp.dead);
                }
                view.lastSeenTime = Time.unscaledTime;

                // ADR-094: "los facelings no llevan nombre de disfraz (nametag vacío)".
                string nameTagText = rp.species == 0 ? FormatNameTag(rp.id, rp.name) : string.Empty;
                if (view.nameTag != null && view.nameTag.text != nameTagText)
                    view.nameTag.text = nameTagText;
            }

            _toRemove.Clear();

            foreach (var kvp in _active)
            {
                if (!_idsThisFrame.Contains(kvp.Key) &&
                    Time.unscaledTime - kvp.Value.lastSeenTime >= missingRemoteGraceSeconds)
                    _toRemove.Add(kvp.Key);
            }

            foreach (var id in _toRemove)
            {
                if (_active.TryGetValue(id, out var view))
                {
                    Debug.Log($"[RemotePlayerManager] despawned id={id} reason=missing_grace_elapsed");
                    Release(view);
                }

                _active.Remove(id);
            }
        }

        private void Update()
        {
            TrySubscribe();

            float dt = Time.deltaTime;

            foreach (var kvp in _active)
            {
                var view = kvp.Value;

                if (view == null || view.root == null)
                    continue;

                // E1 (ADR-074): snap por error grande, mismo criterio que el yaw de abajo y por la
                // misma razón. Con el área de interés, un peer deja de relayarse al alejarse y
                // vuelve al acercarse; si reentra DENTRO de los 3 s de gracia (missingRemote-
                // GraceSeconds) su proxy sigue vivo en su última posición conocida, y sin esto lo
                // veríamos cruzar el mapa interpolando. También cubre lo que ya pasaba antes de
                // E1: respawn y desplazamiento de chunk.
                //
                // El umbral no puede confundirse con movimiento normal: a sprint (7,29 m/s) y con
                // poses a 10 Hz un peer avanza ~0,73 m entre muestras, así que cualquier salto de
                // varios metros es discontinuidad, no carrera.
                // Pose a dibujar ESTE frame, reproducida con retardo (ver InterpolationDelay). Si
                // todavía no hay dos muestras que rodeen ese instante, se cae al último valor
                // conocido y el suavizado exponencial de abajo hace de red.
                bool played = TrySamplePlaybackPose(view, out Vector3 playbackPos, out float playbackYaw);
                Vector3 aimPosition = played ? playbackPos : view.targetPosition;
                float aimYaw = played ? playbackYaw : view.targetRotation;

                if ((view.root.position - aimPosition).sqrMagnitude >
                    positionSnapThreshold * positionSnapThreshold)
                {
                    view.root.position = aimPosition;
                }
                else if (played)
                {
                    // Con reproducción diferida el valor YA viene interpolado a ritmo constante:
                    // volver a suavizarlo por encima sólo añadiría retraso y desharía justo la
                    // uniformidad que se busca.
                    view.root.position = aimPosition;
                }
                else
                {
                    float posT = 1f - Mathf.Exp(-Mathf.Max(0f, positionSmoothing) * dt);
                    view.root.position = Vector3.Lerp(view.root.position, aimPosition, posT);
                }

                // [C] Critically-damped yaw (SmoothDampAngle) — less lag in sustained turns than the
                // old exponential lerp, no overshoot. Snap past a large error so respawn / chunk
                // displacement / instant 180° turns don't sweep the long way around.
                float currentY = view.root.eulerAngles.y;
                float newY;
                if (Mathf.Abs(Mathf.DeltaAngle(currentY, aimYaw)) > yawSnapThreshold)
                {
                    newY = aimYaw;
                    view.yawVelocity = 0f;
                }
                else if (played)
                {
                    newY = aimYaw; // ya viene a ritmo constante, igual que la posición
                    view.yawVelocity = 0f;
                }
                else
                {
                    newY = Mathf.SmoothDampAngle(currentY, aimYaw,
                        ref view.yawVelocity, rotationSmoothTime, Mathf.Infinity, dt);
                }
                view.root.rotation = Quaternion.Euler(0f, newY, 0f);
            }

            // Mismo gate que el espejo del log del backend (ver NetworkInitializer
            // .VerboseBackendLog): son dos Debug.Log POR PEER, y aunque van estrangulados a
            // uno cada 2 s, cada uno arrastra su stack trace y se suman al mismo Editor.log
            // que reventó el editor. Con BACKROOMS_VERBOSE_LOG=1 vuelven.
            if (NetworkInitializer.VerboseBackendLog &&
                _active.Count > 0 && Time.unscaledTime >= _nextUpdateLogTime)
            {
                foreach (var kvp in _active)
                {
                    var view = kvp.Value;
                    if (view != null)
                    {
                        Debug.Log($"[RemotePlayerManager] updated id={kvp.Key} pos={view.targetPosition}");
                        int selfId = NetIdentity.Local;
                        Debug.Log($"MPTRACE step=U event=remote_transform_apply self_id={selfId} remote_id={kvp.Key} pos=({view.targetPosition.x:F2},{view.targetPosition.y:F2},{view.targetPosition.z:F2})");
                    }
                }
                _nextUpdateLogTime = Time.unscaledTime + 2f;
            }
        }

        private RemotePlayerView Acquire(int id, string playerName, int species)
        {
            RemotePlayerView view = null;

            // ADR-094: species picks a DIFFERENT prefab/hierarchy, so a pooled view can only be
            // reused for the species it was BUILT for — scanned rather than a straight Dequeue
            // because the pool mixes species once any faceling proxy has ever been released.
            // Pools here are small (remote peer count), so a linear scan-and-requeue costs
            // nothing that matters. A dead entry (root destroyed somehow) is dropped rather than
            // requeued, matching the original single-Dequeue fallback.
            int toScan = _pool.Count;
            for (int i = 0; i < toScan; i++)
            {
                var candidate = _pool.Dequeue();
                if (candidate.root == null)
                    continue;
                if (candidate.builtSpecies == species)
                {
                    view = candidate;
                    view.root.gameObject.SetActive(true);
                    break;
                }
                _pool.Enqueue(candidate); // right shape, wrong species right now — keep for later
            }

            if (view == null)
                view = CreateView(species);

            view.id = id;
            view.targetPosition = view.root != null ? view.root.position : Vector3.zero;
            view.samples.Clear(); // un proxy reciclado no arrastra el recorrido del anterior
            view.targetRotation = view.root != null ? view.root.eulerAngles.y : 0f;
            view.yawVelocity = 0f; // [C] no carry-over from a recycled view
            ResetCosmetics(view); // ADR-022..ADR-049: recycled proxy starts clean (root just re-activated above)
            view.lastSeenTime = Time.unscaledTime;

            if (view.root != null)
                view.root.name = $"RemotePlayer_{id}";

            if (view.root != null)
            {
                Debug.Log(
                    $"MPTRACE step=PVP event=remote_proxy_acquire remote_id={id} " +
                    $"root={view.root.name} active={view.root.gameObject.activeSelf}");
                RemotePvpHitbox.Install(view.root.gameObject, id);
            }

            // ADR-094: "los facelings no llevan nombre de disfraz (nametag vacío)".
            ConfigureNameText(view.nameTag, species == 0 ? FormatNameTag(id, playerName) : string.Empty);

            return view;
        }

        private void Release(RemotePlayerView view)
        {
            if (view == null)
                return;

            view.id = -1;
            ResetCosmetics(view); // ADR-022..ADR-049
            view.targetPosition = Vector3.zero;
            view.samples.Clear();
            view.targetRotation = 0f;
            view.yawVelocity = 0f; // [C]

            if (view.nameTag != null)
                view.nameTag.text = string.Empty;

            if (view.root != null)
                view.root.gameObject.SetActive(false);

            _pool.Enqueue(view);
        }

        /// <summary>
        /// Returns the 16 cosmetic relay fields of a pooled view to their defaults. Single shared
        /// point for the two reset sites required by the pose-relay convention (rule #5, reset on
        /// Acquire/Release): Acquire uses it so a proxy recycled out of the pool carries nothing
        /// over, Release uses it so a proxy handed back to the pool stores nothing stale.
        ///
        /// PLAIN FIELD-BY-FIELD ASSIGNMENTS ON PURPOSE — do not collapse this into an object or
        /// struct initializer: a dropped line relays 0 forever.
        ///
        /// Deliberately does NOT touch id, root, nameTag, targetPosition, targetRotation,
        /// yawVelocity or lastSeenTime: those differ between the two call sites and stay assigned
        /// there.
        /// </summary>
        private static void ResetCosmetics(RemotePlayerView view)
        {
            view.animationState = "idle";
            view.crouch = false;
            view.pitch = 0f;
            view.equipment = new int[4]; // ADR-022: no stale clothing on a recycled proxy
            view.heldItem = 0; // ADR-023: no stale held item on a recycled proxy
            view.hitSeq = 0; // ADR-024: no stale hit counter on a recycled proxy (hook re-arms its sentinel)
            view.dead = false; // ADR-028 post-E3: the pooled SetActive(false) is the pool's, not the flag's
            view.revealed = false; // ADR-038: no stale real form on a recycled proxy (hook restores its materials)
            view.vocalSeq = 0; // ADR-048: no stale scream counter (hook re-arms its sentinel)
            view.vocalKind = 0; // ADR-048
            view.lightOn = false; // ADR-042: no stale torch glow on a recycled proxy
            view.fireSeq = 0; // ADR-042: no stale shot counter (hook re-arms its sentinel)
            view.buttons = 0; // ADR-044: a recycled proxy is neither aiming nor reloading
            view.meleeSeq = 0; // ADR-044: no stale swing counter (hook re-arms its sentinel)
            view.carryDef = 0; // ADR-049: a recycled proxy must not inherit the last owner's planks
            view.carryCount = 0; // ADR-049
            view.species = 0; // ADR-094: no stale faceling species on a recycled proxy
        }

        private RemotePlayerView CreateView(int species)
        {
            GameObject go;
            // ADR-094: species==1/2 get their own prefab if one has been built; falls back to the
            // default player avatar otherwise (a faceling with no baked body just looks like a
            // player until the matching "Backrooms ▸ Facelings ▸ Build * Avatar Prefab" has been
            // run — never a missing-reference error).
            GameObject prefab = remotePlayerPrefab;
            // ADR-131: el VIGILANTE (species 3) lleva el cuerpo del adulto. Es un faceling adulto
            // sentado, no una criatura con su propio modelo: lo único que lo distingue en pantalla
            // es la postura, y esa la decide el bit `RemoteButtons.Seated`, no el prefab.
            if ((species == 1 || species == 3) && facelingAdultPrefab != null)
                prefab = facelingAdultPrefab;
            else if (species == 2 && facelingChildPrefab != null)
                prefab = facelingChildPrefab;

            if (prefab != null)
                go = Instantiate(prefab);
            else
                go = CreateDefaultAvatar();

            DisableLocalOnlyComponents(go);
            ConfigureProxyRendering(go);
            go.transform.SetParent(transform, false);

            var view = new RemotePlayerView
            {
                root = go.transform,
                nameTag = CreateNameTag(go.transform),
                targetPosition = go.transform.position,
                targetRotation = go.transform.eulerAngles.y,
                animationState = "idle",
                builtSpecies = species
            };

            return view;
        }

        /// <summary>
        /// A remote proxy must never depend on Unity's culling to be correct.
        ///
        /// The vendor rig ships every SkinnedMeshRenderer with <c>updateWhenOffscreen = false</c>
        /// (12 of them in MTP_PlayerViewer) and its Animator in <c>CullUpdateTransforms</c>. That
        /// combination is fine for a locally-driven character, but a proxy is posed by hooks that
        /// run in LateUpdate — and ProxyGroundingHook shifts the Pelvis ADDITIVELY, on the stated
        /// assumption that "the Animator re-writes Pelvis first, so nothing accumulates". The
        /// moment the Animator is culled it stops re-writing, that assumption breaks, and the
        /// offset accumulates on a bone the renderer bounds are anchored to. The bounds drift out
        /// of the frustum, which keeps the proxy culled, which keeps it drifting: the mesh
        /// disappears and does not come back while you look at it, yet the NameTag (a runtime
        /// MeshRenderer with its own bounds, no skinning) keeps rendering. That is exactly the
        /// reported symptom.
        ///
        /// Cost is a per-frame bounds recompute for a handful of proxies. The vendor itself applies
        /// this same pair as the standard remedy (WieldableAnimatorEditor.cs:90/99). Done in code
        /// rather than baked into the prefab so it also covers the capsule fallback and survives
        /// any future re-bake — and so STP's own prefab is never edited.
        /// </summary>
        private static void ConfigureProxyRendering(GameObject root)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;

            foreach (var anim in root.GetComponentsInChildren<Animator>(true))
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        private GameObject CreateDefaultAvatar()
        {
            var root = new GameObject("RemotePlayer");

            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Visual";
            capsule.transform.SetParent(root.transform, false);
            capsule.transform.localPosition = new Vector3(0f, 1f, 0f);

            var col = capsule.GetComponent<Collider>();
            if (col != null)
                SafeDestroy(col);

            var renderer = capsule.GetComponent<Renderer>();
            if (renderer != null)
            {
                // Evita Shader.Find directo. Usa el helper robusto contra magenta en build.
                renderer.sharedMaterial = MaterialHelper.MakeLit(defaultAvatarColor);
            }

            return root;
        }

        private GameObject CreateRemoteMarker(Transform parent)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "RemoteMarker";
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = new Vector3(0f, nameTagHeight + 0.35f, 0f);
            marker.transform.localScale = Vector3.one * 0.18f;

            var col = marker.GetComponent<Collider>();
            if (col != null)
                SafeDestroy(col);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = MaterialHelper.MakeLit(remoteMarkerColor);

            return marker;
        }

        private static void DisableLocalOnlyComponents(GameObject root)
        {
            if (root == null)
                return;

            foreach (var controller in root.GetComponentsInChildren<PlayerController>(true))
                controller.enabled = false;

            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                camera.enabled = false;

            foreach (var listener in root.GetComponentsInChildren<AudioListener>(true))
                listener.enabled = false;

            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null)
                    continue;

                string typeName = behaviour.GetType().Name;
                if (typeName == "PlayerInput")
                    behaviour.enabled = false;
            }
        }

        private TextMeshPro CreateNameTag(Transform parent)
        {
            var tagGo = new GameObject("NameTag");
            tagGo.transform.SetParent(parent, false);
            tagGo.transform.localPosition = new Vector3(0f, nameTagHeight, 0f);

            var tmp = tagGo.AddComponent<TextMeshPro>();
            ConfigureNameText(tmp, string.Empty);

            var rectTransform = tmp.GetComponent<RectTransform>();
            if (rectTransform != null)
                rectTransform.sizeDelta = new Vector2(4f, 1f);

            tagGo.AddComponent<BillboardNameTag>();
            CreateRemoteMarker(parent);

            return tmp;
        }

        private void ConfigureNameText(TMP_Text text, string displayName)
        {
            if (text == null)
                return;

            text.text = displayName;
            text.fontSize = nameTagFontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.color = new Color(0.85f, 1f, 1f, 1f);
            text.outlineColor = Color.black;
            text.outlineWidth = 0.18f;

            // Sustituye TMP_Text.enableWordWrapping obsoleto.
            text.textWrappingMode = TextWrappingModes.NoWrap;

            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = false;
            text.raycastTarget = false;

            if (text is TextMeshPro textMeshPro)
                textMeshPro.sortingOrder = 100;
        }

        private static string FormatNameTag(int id, string playerName)
        {
            string displayName = string.IsNullOrWhiteSpace(playerName) ? $"Player {id}" : playerName;
            return $"{displayName}\nID {id}";
        }

        private void OnDestroy()
        {
            OnDisable();

            foreach (var kvp in _active)
            {
                var view = kvp.Value;

                if (view != null && view.root != null)
                    SafeDestroy(view.root.gameObject);
            }

            _active.Clear();

            while (_pool.Count > 0)
            {
                var view = _pool.Dequeue();

                if (view != null && view.root != null)
                    SafeDestroy(view.root.gameObject);
            }

            _idsThisFrame.Clear();
            _toRemove.Clear();
        }

        private static void SafeDestroy(Object obj)
        {
            if (obj == null)
                return;

            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }
    }

    public class RemotePlayerView
    {
        /// Una pose recibida, con el instante en que llegó. El sello es LOCAL (hora de recepción)
        /// porque el wire no trae marca de tiempo del emisor; para absorber el jitter da igual, ya
        /// que lo que importa es la irregularidad entre llegadas, que es justo lo que esto mide.
        public struct PoseSample
        {
            public float time;
            public Vector3 position;
            public float yaw;
        }

        public int id = -1;
        public Transform root;
        public TextMeshPro nameTag;
        public Vector3 targetPosition;
        public float targetRotation;
        // [C] SmoothDampAngle state for the yaw smoothing (degrees/sec); reset on spawn/release.
        public float yawVelocity;

        /// Historial de poses recibidas, para reproducir con retardo (ver
        /// <see cref="RemotePlayerManager.InterpolationDelay"/>). Es una cola pequeña: se descarta
        /// todo lo que ya quedó por detrás del instante que se está dibujando, así que su tamaño lo
        /// acota el retardo y no crece con la duración de la partida.
        public readonly List<PoseSample> samples = new List<PoseSample>(8);
        // ADR-011: scalar transient-action channel, read by ProxyPickupHook, which edge-detects the
        // transition into "pickup" and fires the Pickup trigger. The domain the backend actually
        // emits is exactly "idle" | "walk_slow" | "pickup" (sync.rs::broadcast_player_update).
        // LOCOMOTION DOES NOT GO THROUGH HERE, and there is nothing to drive from it: ADR-013 derives
        // it from velocity into MovementSpeed, and the ADR-012 controller only has the states
        // Movement/Jump/Pickup. Do not re-add a CrossFade switch over this string: it would be dead
        // twice over — no Idle/Walk/Run/Attack state exists to fade to, and three of those four
        // strings are never sent in the first place. The manager that tried it logged
        // "Animator.GotoState: State could not be found" with a full stack trace, per proxy, per frame.
        public string animationState = "idle";
        // ADR-020: cosmetic crouch state for this proxy (read by ProxyCrouchHook).
        public bool crouch;
        // ADR-021: cosmetic camera pitch in degrees (read by ProxyPitchHook).
        public float pitch;
        // ADR-022: cosmetic worn clothing item IDs [Head, Torso, Legs, Feet] (read by ProxyClothingHook).
        public int[] equipment = new int[4];
        // ADR-023: cosmetic held item ID (read by ProxyHeldItemHook); 0 = empty hands.
        public int heldItem;
        // ADR-024: cosmetic hit-reaction counter (read by ProxyHitReactionHook); 0 = never hit.
        public int hitSeq;
        // ADR-028 post-E3: cosmetic dead flag (server-derived on the peer's backend). While true
        // the proxy root is hidden — the peer's corpse (CorpseSpawner) is the visible body.
        public bool dead;
        // ADR-038: cosmetic real-form flag (read by ProxyRevealHook). Always false for a real
        // player — only the robapieles (ADR-016) is ever sent with it true.
        public bool revealed;
        // ADR-048: cosmetic vocalisation counter (read by ProxyVocalHook); 0 = never vocalised.
        // Always 0 for a real player — only the robapieles (ADR-016) is ever sent with it set.
        public int vocalSeq;
        // ADR-048: which voice the last bump was. Only meaningful alongside vocalSeq.
        public int vocalKind;
        // ADR-042: cosmetic "held wieldable is lit" flag (read by ProxyLightHook).
        public bool lightOn;
        // ADR-042: cosmetic shot counter (read by ProxyFireAudioHook); 0 = never fired.
        public int fireSeq;
        // ADR-044: cosmetic sustained-state bits (read by ProxyStanceHook) — see RemoteButtons.
        public int buttons;
        // ADR-044: cosmetic melee-swing counter (read by ProxyMeleeHook); 0 = never swung.
        public int meleeSeq;
        // ADR-049: cosmetic carry state (read by ProxyCarryHook) — the CarryableDefinition id on the
        // shoulder, 0 = empty hands, and how many units. A LEVEL: the hook rebuilds when either
        // changes, so unlike the counters above there is no sentinel and no delta.
        public int carryDef;
        public int carryCount;
        // ADR-094: cosmetic species tag (0 human, 1 faceling adulto, 2 faceling niño). Always 0
        // for a real player — the client picks model/animator/audio banks by this value.
        public int species;
        // ADR-094: which species `CreateView` actually built `root` FOR — structural, not
        // cosmetic, so it is NEVER touched by `ResetCosmetics`. `Acquire`'s pool scan reads this
        // to decide whether a pooled view can be reused as-is (its prefab already matches) or a
        // fresh one has to be built; `species` above is the field the relay writes every tick and
        // can legitimately disagree with this while a peer is mid-reassignment.
        public int builtSpecies;
        public float lastSeenTime;
    }

    public sealed class BillboardNameTag : MonoBehaviour
    {
        private Camera _cam;
        private Transform _camTransform;

        private void LateUpdate()
        {
            // Camera.main is a tag lookup; it ran once per name tag PER FRAME. Cache it and
            // re-resolve only when the cached camera is gone or disabled (scene reload / camera
            // swap), which is the way STP hands over between cameras.
            if (_cam == null || !_cam.isActiveAndEnabled)
            {
                _cam = Camera.main;
                _camTransform = _cam != null ? _cam.transform : null;
            }

            if (_camTransform == null)
                return;

            Vector3 direction = transform.position - _camTransform.position;

            if (direction.sqrMagnitude < 0.0001f)
                return;

            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }
    }
}
