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

        private void Update()
        {
            if (_placed && !_verdictGiven && _verdictAt > 0f && Time.time >= _verdictAt)
                GiveVerdict();
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
