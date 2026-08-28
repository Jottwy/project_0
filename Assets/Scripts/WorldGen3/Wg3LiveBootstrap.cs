using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// ADR-095, verificación (e) — arranca una sesión REAL sirviendo mundo de WorldGen3 y deja al
    /// jugador dentro de una pieza.
    ///
    /// Es lo mínimo para cerrar la verificación que ni los tests ni la escena aislada pueden dar:
    /// que el backend lea el manifiesto, coloque, mande la lista por el wire, y que lo que el
    /// cliente dibuja sea lo mismo contra lo que el servidor colisiona.
    ///
    /// **EL TELEPORT NO ES COMODIDAD.** El andamio de F2 deja un chunk de cada tres vacío, así que
    /// el origen del mundo cae en el aire con bastante probabilidad. Aparecer ahí y caerse se lee
    /// como "el mundo no ha cargado", y se depuraría el streaming en vez de mirar que ahí,
    /// simplemente, no hay nada. Se espera a la primera pieza y se aterriza dentro de ella.
    /// </summary>
    public sealed class Wg3LiveBootstrap : MonoBehaviour
    {
        [Tooltip("Semilla del mundo. La misma que use el backend decide qué piezas caen dónde.")]
        public int worldSeed = 42;

        public Wg3ChunkStreamer streamer;
        public Wg3TestPlayer player;

        [Tooltip("Segundos antes de rendirse esperando la primera pieza.")]
        public float spawnTimeout = 30f;

        private bool _placed;
        private float _deadline;
        private Vector3 _landedAt;
        private float _verdictAt = -1f;
        private bool _verdictGiven;

        /// <summary>
        /// EL CRITERIO DE CIERRE, dicho por el propio juego unos segundos después de aterrizar.
        ///
        /// Que las piezas lleguen por el wire y se dibujen no demuestra nada por sí solo: lo que
        /// hay que probar es que **lo que se dibuja es contra lo que se choca**. Si el suelo que se
        /// ve no tuviera collider, el jugador seguiría cayendo y la escena se vería idéntica en una
        /// captura. Por eso el veredicto mira la Y y el contacto con el suelo, no los píxeles.
        /// </summary>
        private void GiveVerdict()
        {
            _verdictGiven = true;
            var controller = player.GetComponent<CharacterController>();
            float drop = _landedAt.y - player.transform.position.y;
            bool grounded = controller != null && controller.isGrounded;

            // El umbral NO es "no ha caído": se le suelta a propósito un metro por encima del suelo
            // de la pieza, así que caer ~1 m es lo correcto. Lo que se comprueba es que la caída
            // TERMINÓ — que hay algo debajo. Un jugador atravesando geometría sin colisión seguiría
            // bajando, y a los tres segundos llevaría decenas de metros.
            //
            // La primera versión de esto exigía `drop < 0.5` y dio un rojo que no era del sistema
            // sino del criterio. Un test que mide otra cosa que la que dice medir es peor que no
            // tenerlo, porque manda a depurar donde no hay nada roto.
            const float MaxLandingDrop = 2.0f;

            if (grounded && drop < MaxLandingDrop)
                Debug.Log($"[WG3] VERIFICACIÓN (e) OK — el jugador se sostiene en la geometría " +
                          $"servida por el backend. pos={player.transform.position} caída={drop:0.00} m, " +
                          $"grounded={grounded}");
            else
                Debug.LogError($"[WG3] VERIFICACIÓN (e) FALLA — el jugador NO se apoya en lo que se " +
                               $"dibuja. pos={player.transform.position} caída={drop:0.00} m, " +
                               $"grounded={grounded}. La geometría llegó pero su colisión no.");

            // Captura de la vista de juego para poder MIRARLO, no solo leerlo. Va a la carpeta
            // temporal del sistema y no al proyecto: es una prueba de una ejecución, no un asset.
            string shot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wg3_live.png");
            ScreenCapture.CaptureScreenshot(shot);
            Debug.Log($"[WG3] captura de la vista de juego en {shot}");

            // EL ARNÉS EXIGE ADEMÁS UNA VARIABLE DE ENTORNO, y no es cinturón y tirantes.
            //
            // Apagarlo en el fichero de escena no sirvió: con «Enter Play Mode Options / Reload
            // Scene disabled», Unity NO recarga la escena del disco al entrar en Play, y la copia en
            // memoria traía el campo serializado a `true` de antes del cambio. Poner el defecto de
            // la clase en `false` tampoco sirvió, por lo mismo: un valor serializado gana al
            // defecto. Dos intentos y dos veces el jugador acabó teletransportado al borde de la
            // región a los tres segundos de aparecer — que desde dentro se lee como «esto no genera
            // nada» y costó dos sesiones de juego.
            //
            // Una variable de entorno no la puede serializar ninguna escena, así que este camino
            // solo se enciende queriendo. La verificación (e) de ADR-096 ya está cumplida y
            // registrada; esto es solo para volver a lanzarla si hiciera falta.
            bool harnessRequested =
                System.Environment.GetEnvironmentVariable("BACKROOMS_WG3_AUTOCROSS") == "1";

            if (autoCrossJunction && harnessRequested && grounded)
            {
                _cross = Cross.Searching;
                // Un radio más para que la región de enfrente esté montada ANTES de pisar la junta:
                // cruzar hacia geometría que aún no ha llegado mediría el streaming, no la junta.
                if (streamer != null) streamer.radius = Mathf.Max(streamer.radius, 2);
            }
        }

        /// <summary>
        /// Busca por dónde se cruza la junta más cercana, y lo hace MIRANDO LA GEOMETRÍA en vez de
        /// preguntando dónde están las puertas.
        ///
        /// El cliente no conoce el contrato de junta —ni tiene por qué: solo recibe piezas—, así que
        /// barre la línea del borde buscando un punto con suelo debajo y hueco a la altura de la
        /// cabeza. Si lo encuentra, ahí hay una puerta; y si el contrato estuviera roto, no habría
        /// ninguno y el barrido lo diría. Es una comprobación más honesta que teletransportar a una
        /// coordenada que el servidor haya chivado.
        /// </summary>
        private void BeginCrossing()
        {
            Vector3 at = player.transform.position;
            _crossBorderX = Mathf.Round(at.x / RegionMeters) * RegionMeters;

            const float Span = 70f;
            const float Step = 0.5f;
            float bestZ = float.NaN;
            float bestDistance = float.MaxValue;

            for (float z = at.z - Span; z <= at.z + Span; z += Step)
            {
                var probe = new Vector3(_crossBorderX, at.y + 2.5f, z);
                if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 6f)) continue;

                // Hueco a la altura de la cabeza sobre ese suelo. Sin esto, el barrido se casaría con
                // el techo de una pieza o con la cara superior de un bloque macizo.
                Vector3 stand = hit.point + Vector3.up * 0.9f;
                if (Physics.CheckSphere(stand, 0.35f)) continue;

                float d = Mathf.Abs(z - at.z);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestZ = hit.point.z;
                }
            }

            if (float.IsNaN(bestZ))
            {
                Debug.LogError(
                    $"[WG3] CRUCE: no hay ni un punto caminable en la junta x={_crossBorderX:0} " +
                    $"a lo largo de {Span * 2:0} m. O el contrato de junta no está poniendo puertas, " +
                    $"o la región de enfrente no ha llegado.", this);
                _cross = Cross.Done;
                return;
            }

            // Se arranca ANTES de la junta y se cruza de lado a lado, para que el veredicto cubra el
            // paso y no solo el llegar.
            _crossStartX = _crossBorderX - 7f;
            player.Respawn(new Vector3(_crossStartX, player.transform.position.y, bestZ));
            _crossLowestY = player.transform.position.y;
            _crossDeadline = Time.time + 20f;
            _cross = Cross.Walking;
            Debug.Log($"[WG3] CRUCE: junta en x={_crossBorderX:0}, entrando por z={bestZ:0.00}");
        }

        private void StepCrossing()
        {
            var controller = player.GetComponent<CharacterController>();
            controller.Move((Vector3.right * 2.5f + Vector3.down * 4f) * Time.deltaTime);
            _crossLowestY = Mathf.Min(_crossLowestY, player.transform.position.y);

            float travelled = player.transform.position.x - _crossStartX;
            bool crossed = player.transform.position.x > _crossBorderX + 5f;

            if (crossed)
            {
                _cross = Cross.Done;
                float fell = _crossStartX == 0f ? 0f : player.transform.position.y - _crossLowestY;
                Debug.Log(
                    $"[WG3] CRUCE OK — la junta x={_crossBorderX:0} se cruza andando. " +
                    $"recorrido {travelled:0.0} m, y final {player.transform.position.y:0.00}, " +
                    $"y mínima {_crossLowestY:0.00} (caída {fell:0.00} m)");
                string shot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wg3_cross.png");
                ScreenCapture.CaptureScreenshot(shot);
                return;
            }

            if (Time.time > _crossDeadline)
            {
                _cross = Cross.Done;
                Debug.LogError(
                    $"[WG3] CRUCE FALLA — 20 s empujando y no se pasa de x={player.transform.position.x:0.0} " +
                    $"(junta en {_crossBorderX:0}). Hay algo tapiando la puerta.", this);
                string shot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wg3_cross.png");
                ScreenCapture.CaptureScreenshot(shot);
            }
        }

        private void Start()
        {
            var init = NetworkInitializer.Instance;
            if (init == null)
                init = new GameObject("NetworkInitializer").AddComponent<NetworkInitializer>();

            init.enableWorldGen3 = true;
            init.StartAsAutoSolo("Wg3Live", worldSeed);

            _deadline = Time.time + spawnTimeout;
            Debug.Log($"[WG3] arrancando sesión en vivo, semilla {worldSeed}, WorldGen3 ACTIVO");
        }

        [Header("Cruce de junta (ADR-096 verificación (e))")]
        /// <summary>
        /// APAGADO POR DEFECTO desde 2026-08-28. Era el arnés de la verificación (e) de ADR-096
        /// —buscar una junta y cruzarla solo—, ya cumplida y registrada.
        ///
        /// El defecto importa más de lo que parece: con «Enter Play Mode Options / Reload Scene
        /// disabled», Unity NO recarga la escena del disco al entrar en Play, así que una escena que
        /// siga en memoria de antes de que este campo existiera usa el DEFECTO DE LA CLASE. Con el
        /// defecto en `true`, apagarlo en el fichero de escena no servía de nada y el arnés seguía
        /// secuestrando al jugador a los tres segundos de aparecer: se lo llevaba al borde de la
        /// región y lo empujaba 12 m, que desde dentro se lee como «esto no genera nada».
        /// </summary>
        [Tooltip("Tras aterrizar, busca una junta de región y la cruza andando, solo. Es un arnés " +
                 "de verificación: déjalo apagado para jugar.")]
        public bool autoCrossJunction;

        /// <summary>
        /// Lado de región en metros. **Espejo de `REGION_M` en Rust**, y duplicado a sabiendas: el
        /// cliente no necesita saber de regiones para jugar —solo recibe piezas— y este número
        /// existe únicamente para que el arnés sepa DÓNDE mirar. Si algún día el cliente necesitara
        /// la región para algo real, iría por el wire y no por una constante repetida.
        /// </summary>
        public const float RegionMeters = 150f;

        private enum Cross { Idle, Searching, Walking, Done }
        private Cross _cross = Cross.Idle;
        private float _crossStartX;
        private float _crossBorderX;
        private float _crossLowestY;
        private float _crossDeadline;

        private void Update()
        {
            if (_placed && !_verdictGiven && _verdictAt > 0f && Time.time >= _verdictAt)
                GiveVerdict();

            if (_cross == Cross.Searching) BeginCrossing();
            else if (_cross == Cross.Walking) StepCrossing();

            if (_placed) return;

            if (streamer != null && player != null && streamer.TryGetSpawnPoint(out Vector3 point))
            {
                player.transform.position = point;
                player.Respawn(point);
                _placed = true;
                _landedAt = point;
                _verdictAt = Time.time + 3f;
                Debug.Log($"[WG3] jugador colocado dentro de la primera pieza en {point}");
                return;
            }

            if (Time.time > _deadline)
            {
                _placed = true; // no se reintenta: repetir el aviso cada frame lo vuelve ruido
                var client = IPCClient.Instance;
                string estado = client == null
                    ? "no hay IPCClient"
                    : (client.Wg3Enabled ? "el backend dice WG3 activo pero no llegó ninguna pieza"
                                         : "el backend NO anunció WG3 en el saludo");
                Debug.LogError($"[WG3] {spawnTimeout:0} s sin recibir una sola pieza — {estado}", this);
            }
        }
    }
}
