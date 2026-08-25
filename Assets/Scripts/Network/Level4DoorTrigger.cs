using BackroomsSurvival.Gameplay;
using PolymindGames.MovementSystem;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-093 E3 — la puerta física del Level 4: marco exento, hoja que se abre y se cierra, y
    /// un cruce que sólo cuenta si ATRAVIESAS el hueco con la puerta abierta.
    ///
    /// POR QUÉ EXENTA Y NO EMPOTRADA EN UNA PARED. Un marco suelto en mitad de una sala es la
    /// imagen Backrooms por excelencia, pero además resuelve dos cosas de ingeniería: no depende
    /// de que el generador haya dejado una pared utilizable justo ahí (la reserva se re-sortea
    /// cada epoch, y en Level 0 el tile del ancla es un fondo de saco), y deja el hueco visible
    /// por los dos lados — que es lo que hará falta cuando el hueco enseñe el otro lado (P3).
    ///
    /// CRUCE POR PLANO, NO POR RADIO. Antes esto disparaba al entrar en una esfera de 2 m: daba
    /// igual hacia dónde miraras o anduvieras, y como aterrizabas encima de la puerta de destino
    /// había que silenciar 3 segundos para que no rebotaras. Ahora se mira el SIGNO de la
    /// distancia al plano de la puerta y se dispara en el cambio de signo, con dos condiciones
    /// más: la hoja tiene que estar abierta y el punto de cruce caer dentro del ancho del vano.
    /// El enfriamiento sobra por construcción — apareces al otro lado del plano de la puerta de
    /// destino, y quedarse quieto ahí no vuelve a cruzar nada.
    ///
    /// Sigue siendo poll contra el motor LOCAL y no un trigger de física, por el mismo motivo de
    /// siempre: los avatares remotos son proxies cosméticos bajo <see cref="RemotePlayerManager"/>
    /// y pueden o no llevar collider, así que un `OnTriggerEnter` puede dispararse por el jugador
    /// equivocado.
    ///
    /// Instanciada (nunca en escena ni prefab) por <see cref="GameBootstrap"/>. La hoja y el marco
    /// son primitivas PLACEHOLDER: se sustituyen por arte sin tocar la lógica de cruce.
    /// </summary>
    public sealed class Level4DoorTrigger : MonoBehaviour
    {
        // Vano: 1,6 m de ancho por 2,4 m de alto. Cabe de sobra un jugador y encaja con el tile
        // de 5 m sin comerse el paso.
        private const float DoorWidth = 1.6f;
        private const float DoorHeight = 2.4f;
        private const float FrameThickness = 0.18f;

        /// Cuánto gira la hoja al abrir, y en cuánto tiempo.
        private const float OpenAngle = 95f;
        private const float SwingSeconds = 0.55f;
        /// A qué distancia se puede alcanzar la puerta con la tecla de uso. Igual que
        /// <see cref="WorldInteractor.interactDistance"/>, para que se sienta como el resto.
        private const float ReachM = 5f;
        /// Semiángulo del cono de puntería. Generoso a propósito: el vano mide 1,6 m y a 5 m eso
        /// son ~9°, así que un cono estrecho obligaría a centrar el retículo en una hoja que
        /// además puede estar abierta y fuera del hueco.
        private const float AimConeDegrees = 35f;
        /// RED DE SEGURIDAD, no el mecanismo. La geometría ya impide el rebote —sales a
        /// `PORTAL_EXIT_M` (1,5 m), fuera de la banda de 1 m, por la cara transitable— así que
        /// esto no debería dispararse nunca en juego normal. Existe para lo que la geometría no
        /// cubre: un veredicto que llega tarde por lag, un snap que se solapa con otro, o un
        /// empujón de física en el frame del salto. Corto a propósito: si fuera largo taparía un
        /// bug de verdad en vez de acotarlo, que es como se llegó a los 3 s de la primera versión.
        private const float ChainGuardSeconds = 0.4f;
        private static float _lastCrossTime = float.NegativeInfinity;

        private Level4Door _door;
        private long _nextRequestId = 1;

        private CharacterControllerMotor _motor;
        private Transform _leaf;
        private Collider _leafCollider;
        private float _swing;          // 0 = cerrada, 1 = abierta
        private bool _open;
        /// Posición del jugador en el espacio de la puerta el frame anterior. Es el otro extremo
        /// del segmento que se prueba contra el plano.
        private Vector3 _prevLocal;
        private bool _hasPrevLocal;
        /// Longitud de segmento a partir de la cual el movimiento no puede ser andar, sino una
        /// recolocación. Generoso (6 m ≈ 20 m/s a 30 fps) para no descartar un frame malo de
        /// verdad, y muy por debajo de cualquier teleport real.
        private const float TeleportSegmentM = 6f;
        /// El motor con el que se tomó `_prevLocal`. Si el rig se recrea (respawn), la posición
        /// anterior es de OTRO objeto y el segmento entre ambas no significa nada.
        private CharacterControllerMotor _prevMotor;

        public bool IsOpen => _open;

        /// <summary>
        /// ADR-093 — empareja esta puerta con su gemela y le pone al hueco la vista del otro
        /// lado (<see cref="Level4Portal"/>). Se llama DESPUÉS de <see cref="Configure"/> y desde
        /// fuera, porque hasta que existen las dos puertas no hay par que formar.
        ///
        /// Las medidas del vano salen de aquí y no del portal: el que sabe cómo de grande es el
        /// hueco es quien lo construyó, y duplicar las constantes sería la clase de desfase que
        /// se ve como un marco con la vista descuadrada dentro.
        /// </summary>
        public void PairWith(Level4DoorTrigger other)
        {
            if (other == null || other == this)
                return;
            var mine = GetComponent<Level4Portal>() ?? gameObject.AddComponent<Level4Portal>();
            var theirs = other.GetComponent<Level4Portal>()
                         ?? other.gameObject.AddComponent<Level4Portal>();
            mine.Bind(theirs, DoorWidth, DoorHeight);
        }

        // Con la recarga de dominio desactivada los estáticos sobreviven entre sesiones de Play:
        // sin esto, el primer cruce de la siguiente partida podría comerse la guarda de la
        // anterior.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _lastCrossTime = float.NegativeInfinity;

        public void Configure(Level4Door door, Vector3 facing)
        {
            _door = door;
            transform.rotation = Quaternion.LookRotation(
                new Vector3(facing.x, 0f, facing.z).normalized, Vector3.up);
            BuildFrame(door);
            // Una línea por puerta y sesión. Sin ella, "no pasa nada" es indistinguible de puerta
            // que no nació, puerta lejos de donde se anduvo, y backend que ignoró la petición —
            // la ambigüedad que quemó tres play-tests (2026-08-25).
            Debug.Log($"[Level4Door] MPTRACE step=L4 event=door_spawned door={door} " +
                      $"pos={transform.position} facing={transform.forward}");
        }

        // ── Construcción del placeholder ────────────────────────────────────────────────────
        //
        // Cinco cajas: dos jambas, dintel, umbral y hoja. El marco NO lleva collider (se cruza
        // por el hueco, y una jamba con collider en mitad de una sala es un obstáculo invisible
        // en el minimapa); la hoja SÍ, porque cerrada tiene que parar al jugador.
        private void BuildFrame(Level4Door door)
        {
            Color tint = door == Level4Door.Entry
                ? new Color(0.2f, 0.9f, 0.95f)
                : new Color(0.95f, 0.2f, 0.85f);
            Material frameMat = MaterialHelper.MakeEmissive(tint, 1.2f);

            float halfW = DoorWidth * 0.5f;
            AddBox("Jamb_L", new Vector3(-halfW - FrameThickness * 0.5f, DoorHeight * 0.5f, 0f),
                new Vector3(FrameThickness, DoorHeight + FrameThickness, FrameThickness), frameMat, false);
            AddBox("Jamb_R", new Vector3(halfW + FrameThickness * 0.5f, DoorHeight * 0.5f, 0f),
                new Vector3(FrameThickness, DoorHeight + FrameThickness, FrameThickness), frameMat, false);
            AddBox("Lintel", new Vector3(0f, DoorHeight + FrameThickness * 0.5f, 0f),
                new Vector3(DoorWidth + FrameThickness * 2f, FrameThickness, FrameThickness), frameMat, false);
            AddBox("Threshold", new Vector3(0f, FrameThickness * 0.25f, 0f),
                new Vector3(DoorWidth + FrameThickness * 2f, FrameThickness * 0.5f, FrameThickness), frameMat, false);

            // La hoja cuelga de un pivote en la jamba izquierda, que es lo que hace que gire como
            // una puerta y no sobre su propio centro.
            var hinge = new GameObject("Hinge");
            hinge.transform.SetParent(transform, false);
            hinge.transform.localPosition = new Vector3(-halfW, 0f, 0f);
            _leaf = hinge.transform;

            // Alpha explícito a 1: `tint * 0.35f` multiplica TAMBIÉN el alfa, y una hoja de puerta
            // medio transparente por accidente es justo lo contrario de lo que se quiere ver.
            var leafTint = new Color(tint.r * 0.35f, tint.g * 0.35f, tint.b * 0.35f, 1f);
            var leafMat = MaterialHelper.MakeEmissive(leafTint, 0.25f);
            var leaf = AddBox("Leaf", new Vector3(halfW, DoorHeight * 0.5f, 0f),
                new Vector3(DoorWidth, DoorHeight, FrameThickness * 0.6f), leafMat, true);
            leaf.transform.SetParent(hinge.transform, false);
            leaf.transform.localPosition = new Vector3(halfW, DoorHeight * 0.5f, 0f);
            _leafCollider = leaf.GetComponent<Collider>();
        }

        private GameObject AddBox(string name, Vector3 localPos, Vector3 size, Material mat, bool keepCollider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;

            var col = go.GetComponent<Collider>();
            if (col != null && !keepCollider)
                Destroy(col);

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null && mat != null)
                renderer.sharedMaterial = mat;
            return go;
        }

        // ── Ciclo ───────────────────────────────────────────────────────────────────────────

        private void Update()
        {
            AnimateLeaf();

            ResolveMotor();
            if (_motor == null)
                return;
            if (!ReferenceEquals(_motor, _prevMotor))
            {
                _prevMotor = _motor;
                _hasPrevLocal = false; // rig nuevo: la posición anterior era de otro cuerpo
            }

            TryToggleFromInput();
            TrackCrossing(_motor.transform.position);
        }

        private void AnimateLeaf()
        {
            if (_leaf == null)
                return;
            float target = _open ? 1f : 0f;
            if (!Mathf.Approximately(_swing, target))
            {
                _swing = Mathf.MoveTowards(_swing, target, Time.deltaTime / SwingSeconds);
                _leaf.localRotation = Quaternion.Euler(0f, -OpenAngle * _swing, 0f);
                // La hoja abierta deja de estorbar; cerrada vuelve a parar al jugador.
                if (_leafCollider != null)
                    _leafCollider.enabled = _swing < 0.5f;
            }
        }

        /// Tecla de uso, mismo gesto que <see cref="WorldInteractor"/> (E) y mismo alcance.
        ///
        /// La puntería se resuelve con geometría y no con un raycast, a propósito: el marco no
        /// lleva collider (ver <see cref="BuildFrame"/>) y probar contra los renderers acertaría
        /// sólo la primera jamba —una caja de 18 cm— en vez de la puerta. Distancia al centro del
        /// vano más un cono de mirada es lo que un jugador entiende por "estoy apuntando a esto",
        /// y no depende de capas de física ni de si los triggers responden a los rayos.
        private void TryToggleFromInput()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.eKey.wasPressedThisFrame)
                return;
            var cam = Camera.main;
            if (cam == null)
                return;

            Vector3 aimPoint = transform.position + Vector3.up * (DoorHeight * 0.5f);
            Vector3 toDoor = aimPoint - cam.transform.position;
            if (toDoor.sqrMagnitude > ReachM * ReachM)
                return;
            if (Vector3.Angle(cam.transform.forward, toDoor) > AimConeDegrees)
                return;

            _open = !_open;
            Debug.Log($"[Level4Door] MPTRACE step=L4 event=door_toggled door={_door} open={_open}");
        }

        /// El cruce, por BARRIDO DEL SEGMENTO recorrido este frame — no por muestreo de una banda.
        ///
        /// La versión de banda necesitaba que la banda fuese ancha (para que un frame lento no se
        /// saltara el plano entero) y eso obligaba a salir LEJOS del plano al otro lado, o el
        /// aterrizaje caía dentro de la banda y el primer paso volvía a contar como cruce. Esa
        /// distancia de salida es justo lo que hace que el salto se note.
        ///
        /// Comprobando el segmento (posición anterior → actual) contra el plano no hay banda que
        /// dimensionar: un frame de 200 ms a velocidad de sprint se detecta igual que uno de 5 ms,
        /// y el punto de cruce se interpola, así que "¿pasó por el hueco?" se responde donde
        /// realmente cruzó y no donde estaba al final del frame. Con eso la salida puede quedar a
        /// centímetros del plano y el cruce deja de sentirse como un salto.
        private void TrackCrossing(Vector3 playerPos)
        {
            Vector3 local = transform.InverseTransformPoint(playerPos);
            if (!_hasPrevLocal)
            {
                _prevLocal = local;
                _hasPrevLocal = true;
                return;
            }
            Vector3 previous = _prevLocal;
            _prevLocal = local;

            // UN TELEPORT AJENO NO ES UN CRUCE. Comparar contra el frame anterior significa que
            // CUALQUIER recolocación —respawn, `session_restored` al cargar partida, un rig
            // recreado, el propio snap de la otra puerta— produce un segmento que atraviesa este
            // plano y dispararía un cruce que nadie hizo: morir junto a la puerta te mandaría al
            // Level 4. Y no vale con enumerar las causas conocidas, porque la siguiente no estará
            // en la lista.
            //
            // El discriminante es la LONGITUD: andando, incluso esprintando y con un frame malo,
            // no se recorren metros entre dos frames; teleportarse sí. Un segmento así se descarta
            // y se re-arma el muestreo desde la posición nueva.
            if ((local - previous).sqrMagnitude > TeleportSegmentM * TeleportSegmentM)
                return;

            // Mismo lado ⇒ no cruzó. `>= 0` en los dos para que quedarse EXACTAMENTE en el plano
            // no cuente como cruce cada frame.
            bool wasFront = previous.z >= 0f;
            bool isFront = local.z >= 0f;
            if (wasFront == isFront)
                return;

            // Punto de cruce interpolado sobre el plano: es dónde pasó de verdad, no dónde acabó.
            float span = previous.z - local.z;
            float tCross = Mathf.Approximately(span, 0f) ? 0f : previous.z / span;
            float crossX = Mathf.Lerp(previous.x, local.x, tCross);
            float crossY = Mathf.Lerp(previous.y, local.y, tCross);

            // ¿Por el hueco? Ancho y ALTO: pasar por encima del dintel saltando no es cruzar una
            // puerta, y antes contaba.
            if (Mathf.Abs(crossX) > DoorWidth * 0.5f)
                return;
            if (crossY < -0.5f || crossY > DoorHeight)
                return;
            if (!_open)
            {
                Debug.Log($"[Level4Door] MPTRACE step=L4 event=cross_ignored door={_door} reason=closed");
                return;
            }

            if (Time.time - _lastCrossTime < ChainGuardSeconds)
            {
                Debug.Log($"[Level4Door] MPTRACE step=L4 event=cross_ignored door={_door} " +
                          "reason=chain_guard");
                return;
            }
            _lastCrossTime = Time.time;

            long requestId = _nextRequestId++;
            var ipc = IPCClient.Instance;
            // El caso nulo es el interesante: significa que el cruce se TIRÓ, y el jugador ve
            // exactamente lo mismo que si el backend estuviera roto. Que se diga.
            if (ipc == null)
                Debug.LogWarning($"[Level4Door] MPTRACE step=L4 event=cross_dropped door={_door} " +
                                 "reason=no_ipc_client");
            else
                Debug.Log($"[Level4Door] MPTRACE step=L4 event=cross_sent door={_door} " +
                          $"request_id={requestId} from={playerPos}");
            ipc?.SendLevel4Door(_door, requestId);

            // Un teleport NO es andar. El snap que llega en respuesta mueve al jugador varios
            // metros —y le sube la Y de los pies a la de los ojos— y esa recolocación volvía a
            // leerse como un cruce: en el log del playtest se ven `request_id` 1, 2 y 3 en menos
            // de medio segundo, cada uno disparado por el reposicionamiento del anterior.
            // Desarmar el muestreo en TODAS las puertas hace que el primer frame tras el salto
            // sea "primera lectura" y no un flanco, aquí y en la de destino.
            foreach (var d in FindObjectsByType<Level4DoorTrigger>(FindObjectsSortMode.None))
                d._hasPrevLocal = false;
        }

        /// <summary>Mirror of AuthoritativePoseApplier.ResolveMotor — Unity's overloaded ==
        /// reports a destroyed motor as null, so a rig rebuild forces a re-find.</summary>
        private void ResolveMotor()
        {
            if (_motor != null)
                return;
            _motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
        }
    }
}
