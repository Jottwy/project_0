using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-068 S3 — el lienzo: la conversión entre "dónde apunta el jugador en la pared" y las
    /// coordenadas de 0..255 que viajan por el wire.
    ///
    /// Todo aquí es función PURA, sin escena y sin estado, porque es donde vive el riesgo real
    /// del slice: un eje invertido no revienta nada, dibuja en espejo, y eso no se detecta
    /// compilando ni mirando un log — solo pintando una letra y viéndola del revés.
    ///
    /// El convenio, que tiene que casar clavado con <see cref="SprayRenderer"/>:
    /// <list type="bullet">
    /// <item><c>yaw</c> es la dirección hacia la que MIRA la pintada, o sea la normal de la
    /// pared apuntando a la sala, no hacia el muro.</item>
    /// <item>La U del lienzo crece hacia la DERECHA de quien la lee, que en mundo es
    /// <see cref="RightOf"/> — la misma dirección que la UV invertida de la malla del
    /// renderizador.</item>
    /// <item>La V crece hacia ARRIBA (Y de mundo), que es como la textura indexa sus filas.</item>
    /// </list>
    /// </summary>
    public static class SprayCanvas
    {
        /// <summary>Lado de la retícula del wire. Espejo de la cuantización del backend.</summary>
        public const int Grid = 256;

        /// <summary>Topes del backend (`world::spray`), espejados para no mandar lo que se va a rechazar.</summary>
        public const int MaxStrokes = 32;
        public const int MaxPoints = 512;
        public const float MinCanvasMeters = 0.1f;
        public const float MaxCanvasMeters = 2.0f;
        public const float MaxPlaceDistance = 5.0f;

        /// <summary>
        /// Yaw en grados de una pared cuya normal (apuntando a la sala) es
        /// <paramref name="normal"/>. Se aplana en Y: una pintada nunca se inclina, aunque la
        /// normal traiga algo de componente vertical por el redondeo del collider.
        /// </summary>
        public static float YawFromNormal(Vector3 normal)
        {
            var flat = new Vector3(normal.x, 0f, normal.z);
            // Normal casi vertical (suelo o techo): no hay pared que mirar, así que se cae a 0
            // en vez de devolver NaN. El llamante ya debería haberlo descartado.
            if (flat.sqrMagnitude < 1e-6f) return 0f;
            return Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Dirección de mundo en la que CRECE la U del lienzo: la derecha de quien lo lee.
        ///
        /// Se deriva de la rotación del quad y no de una fórmula suelta a propósito — es la
        /// misma <c>Quaternion.Euler(0, yaw, 0)</c> con la que <see cref="SprayRenderer"/> coloca
        /// la malla, y va NEGADA porque esa malla lleva la U invertida. Cambiar una de las dos
        /// sin la otra deja las pintadas en espejo.
        /// </summary>
        public static Vector3 RightOf(float yaw) => -(Quaternion.Euler(0f, yaw, 0f) * Vector3.right);

        /// <summary>Dirección de mundo en la que crece la V. Siempre arriba.</summary>
        public static Vector3 UpOf(float yaw) => Vector3.up;

        /// <summary>
        /// Proyecta un punto de mundo al lienzo y lo devuelve ya cuantizado a 0..255.
        ///
        /// Fuera del lienzo se CLAMPEA en vez de descartarse: el jugador que se pasa del borde
        /// ve el trazo pegarse al canto, que es lo que hace un spray real contra el límite de
        /// una plantilla, y además evita que un movimiento brusco parta el trazo en dos.
        /// </summary>
        public static void WorldToCanvas(Vector3 world, Vector3 center, float yaw,
            float sizeX, float sizeY, out byte u, out byte v)
        {
            Vector3 d = world - center;
            float su = Mathf.Max(sizeX, 1e-4f);
            float sv = Mathf.Max(sizeY, 1e-4f);

            // 0..1 con el centro del lienzo en 0.5.
            float fu = Vector3.Dot(d, RightOf(yaw)) / su + 0.5f;
            float fv = Vector3.Dot(d, UpOf(yaw)) / sv + 0.5f;

            u = Quantize(fu);
            v = Quantize(fv);
        }

        /// <summary>0..1 → 0..255, con el borde superior alcanzable (no 255,999 truncado a 255).</summary>
        public static byte Quantize(float unit)
        {
            if (float.IsNaN(unit)) return 0;
            int q = Mathf.RoundToInt(Mathf.Clamp01(unit) * (Grid - 1));
            return (byte)Mathf.Clamp(q, 0, Grid - 1);
        }

        /// <summary>
        /// ¿Este impacto sirve para empezar una pintada? Se exige pared (normal casi horizontal)
        /// y alcance de brazo.
        ///
        /// El alcance se comprueba TAMBIÉN aquí aunque el host lo revalide: rechazar en el
        /// cliente ahorra el viaje y, sobre todo, le dice al jugador que no puede pintar ESO en
        /// el momento en que apunta, en vez de dejarle pintar un trazo entero que luego
        /// desaparece sin explicación.
        /// </summary>
        /// <param name="maxVerticalDot">
        /// Cuánto puede desviarse la superficie de la vertical y seguir contando como pared.
        /// 0,60 ⇒ hasta ~37°, y sigue descartando suelo y techo, que están en 1,0. Se subió desde
        /// 0,35 (~20°) porque era demasiado estricto con la parte baja de los muros: ahí la
        /// normal que devuelve el collider no siempre sale limpia, y el bote dejaba de responder
        /// sin motivo aparente.
        /// </param>
        public static bool IsPaintableWall(Vector3 from, Vector3 hitPoint, Vector3 normal,
            float maxVerticalDot = 0.60f)
        {
            if (Vector3.Distance(from, hitPoint) > MaxPlaceDistance) return false;
            return Mathf.Abs(normal.y) <= maxVerticalDot;
        }

        /// <summary>
        /// Metros recorridos entre dos puntos de LIENZO, para cobrar la pintura gastada. La
        /// diagonal del lienzo son <c>hypot(sizeX, sizeY)</c> metros, así que un paso de retícula
        /// vale su fracción — cobrar por puntos y no por distancia haría que pintar despacio
        /// costase más que pintar rápido, que es al revés de lo que se espera.
        /// </summary>
        public static float CanvasStepMeters(byte u0, byte v0, byte u1, byte v1,
            float sizeX, float sizeY)
        {
            float du = (u1 - u0) / (float)(Grid - 1) * sizeX;
            float dv = (v1 - v0) / (float)(Grid - 1) * sizeY;
            return Mathf.Sqrt(du * du + dv * dv);
        }
    }
}
