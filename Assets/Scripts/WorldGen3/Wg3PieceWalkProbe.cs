using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Recorre una pieza autorada de una boca a la otra y dice si se puede.
    ///
    /// POR QUÉ NO BASTA CON MIRARLA: una captura de la pieza montada se ve idéntica tenga colisión o
    /// no. Lo que hay que probar es que el suelo que se DIBUJA sostiene, que el vano que se DIBUJA
    /// se pasa, y que la columna que se DIBUJA para. Eso solo lo contesta el jugador moviéndose y
    /// chocando, así que el veredicto mira Y, avance y contacto, nunca píxeles.
    ///
    /// El recorrido va de boca a boca, no del centro a un lado: los dos sitios donde la geometría
    /// puede estar mal son justo los vanos —tapiados por una caja de más, o abiertos al vacío por
    /// una de menos— y el centro de la pieza no los toca.
    /// </summary>
    public sealed class Wg3PieceWalkProbe : MonoBehaviour
    {
        public Wg3TestPlayer player;

        [Tooltip("Los dos extremos del recorrido, en coordenadas de mundo. Los pone el creador de " +
                 "la escena a partir de las bocas horneadas de la pieza.")]
        public Vector3 from;
        public Vector3 to;

        [Tooltip("Segundos antes de dar el recorrido por imposible.")]
        public float timeout = 25f;

        /// <summary>Caída tolerada. NO es «no ha caído»: al jugador se le suelta medio metro por
        /// encima del suelo para que aterrice, así que caer un poco es lo correcto. Lo que se
        /// comprueba es que la caída TERMINA — que hay algo debajo.</summary>
        private const float MaxDrop = 2.0f;

        private float _deadline;
        private float _lowestY;
        private float _startedAt;
        private bool _done;
        private CharacterController _controller;
        private float _bestDistance = float.MaxValue;
        private float _progressAt;

        /// <summary>
        /// EL JUGADOR SE COLOCA EN EL PRIMER Update, NO EN Start, y no es manía.
        ///
        /// <see cref="Wg3TestPlayer.Start"/> llama a su propio <c>Respawn()</c>, que sin mundo
        /// asignado teletransporta a (0, 1, 0) — la esquina mínima de la huella, donde la cápsula
        /// queda medio fuera del suelo. El orden entre dos <c>Start</c> no está definido, así que
        /// colocar aquí en <c>Start</c> salía bien o mal según el día: cuando salía mal, el jugador
        /// se caía por el borde y el veredicto acusaba a la pieza de no tener colisión.
        ///
        /// Todos los <c>Start</c> corren antes del primer <c>Update</c>, así que aquí el sitio ya no
        /// se lo pisa nadie.
        /// </summary>
        private bool _placed;

        private void Place()
        {
            _placed = true;
            _controller = player.GetComponent<CharacterController>();
            player.Respawn(from + Vector3.up * 0.5f);
            _lowestY = player.transform.position.y;
            _deadline = Time.time + timeout;
            _startedAt = Time.time;
            _progressAt = Time.time;
            Debug.Log($"[WG3] recorrido: de {from} a {to}, {Vector3.Distance(from, to):0.0} m");
        }

        private void Update()
        {
            if (_done) return;
            if (!_placed) { Place(); return; }

            Vector3 pos = player.transform.position;
            _lowestY = Mathf.Min(_lowestY, pos.y);

            Vector3 flat = to - pos;
            flat.y = 0f;

            // CAERSE SE DETECTA ANTES DE LLEGAR, y no es un detalle: sin esto el sondeo cruzaba los
            // 10,7 m en caída libre y anunciaba que había «llegado», porque la distancia se mide en
            // planta. El veredicto quedaba en «llegó pero la y mínima es −191» — cierto, pero
            // acusando al suelo de no sostener cuando el problema era que no había suelo.
            if (pos.y < from.y - 3f)
            {
                _done = true;
                Debug.LogError($"[WG3] RECORRIDO FALLA — el jugador se cayó a través del suelo: " +
                               $"y={pos.y:0.00}, {from.y - pos.y:0.0} m por debajo de donde empezó. " +
                               "La pieza se dibuja pero no colisiona, o no se ha montado.", this);
                return;
            }

            if (flat.magnitude < 0.6f)
            {
                _done = true;
                float drop = from.y - _lowestY;
                bool grounded = _controller != null && _controller.isGrounded;
                float seconds = Time.time - _startedAt;

                if (grounded && drop < MaxDrop)
                    Debug.Log($"[WG3] RECORRIDO OK — la pieza autorada se cruza andando de boca a " +
                              $"boca en {seconds:0.0} s. y final {pos.y:0.00}, y mínima " +
                              $"{_lowestY:0.00} (caída {drop:0.00} m), grounded={grounded}");
                else
                    Debug.LogError($"[WG3] RECORRIDO DUDOSO — llegó, pero y mínima {_lowestY:0.00} " +
                                   $"(caída {drop:0.00} m) y grounded={grounded}. El suelo que se " +
                                   "dibuja no está sosteniendo.", this);

                ScreenCapture.CaptureScreenshot(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wg3_piece_walk.png"));
                return;
            }

            // RODEAR LO QUE ESTORBA, que aquí no es un extra: la columna interior está en medio de
            // la línea recta entre las dos bocas, y esa es su razón de ser (L14 — la columna es
            // estructura, parte el paso y obliga a rodearla). Un sondeo que empujara siempre de
            // frente se quedaría empotrado contra ella y yo leería «no se puede cruzar» de una pieza
            // que está perfectamente bien.
            //
            // Cuando el avance se estanca se añade deriva lateral, alternando el lado: no sabe por
            // dónde hay hueco, así que prueba uno y luego el otro.
            float distance = flat.magnitude;
            if (distance < _bestDistance - 0.05f)
            {
                _bestDistance = distance;
                _progressAt = Time.time;
            }

            Vector3 push = flat.normalized * 2.5f;
            if (Time.time - _progressAt > 1.2f)
            {
                var lateral = new Vector3(-flat.normalized.z, 0f, flat.normalized.x);
                float side = Mathf.Repeat(Time.time - _progressAt, 4f) < 2f ? 1f : -1f;
                push += lateral * (2.0f * side);
            }

            _controller.Move((push + Vector3.down * 4f) * Time.deltaTime);

            if (Time.time > _deadline)
            {
                _done = true;
                Debug.LogError($"[WG3] RECORRIDO FALLA — {timeout:0} s empujando y se queda a " +
                               $"{flat.magnitude:0.0} m de la otra boca, en {pos}. O el vano está " +
                               "tapiado, o hay algo bloqueando el paso.", this);
                ScreenCapture.CaptureScreenshot(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wg3_piece_walk.png"));
            }
        }
    }
}
