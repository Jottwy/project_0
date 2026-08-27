using System.Collections.Generic;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// La traducción de un dibujo del editor de salas al idioma de WG3. PURA: no toca assets, no
    /// llama a <c>UnityEditor</c> y no guarda nada — el menú y el guardado viven en
    /// <c>Wg3PieceBaker</c>, en la carpeta Editor.
    ///
    /// ESTÁ EN RUNTIME PARA PODER TESTEARLA, y no por gusto: <c>EditModeTests.asmdef</c> referencia
    /// <c>BackroomsSurvival</c> y NO puede referenciar <c>Assembly-CSharp-Editor</c>, así que una
    /// conversión que viviera entera en la carpeta Editor sería una conversión sin un solo test. Es
    /// justo la clase de detalle de ensamblados que en este proyecto ya se ha cobrado un falso verde.
    ///
    /// LA TRADUCCIÓN, que es todo lo que hace:
    ///  · la huella sale de los límites REALES del contorno exterior, no de <c>tilesX * 5</c>;
    ///  · el origen pasa del centro de la sala a la ESQUINA MÍNIMA, contrato de WG3;
    ///  · los agujeros de pared a ras de suelo se vuelven bocas tipadas por su ancho;
    ///  · las cajas de <see cref="RoomColliderBuilder"/> se trasladan y se clasifican.
    ///
    /// Lo que NO cruza: tiles, pivote centrado y cualquier idea de rejilla. Por eso F7 podrá borrar
    /// WG2 sin tocar una pieza ya horneada.
    /// </summary>
    public static class Wg3PieceBake
    {
        /// <summary>Tolerancia de "a ras". 2 cm: por debajo está el ruido de dibujar un contorno a
        /// mano en la vista de escena.</summary>
        public const float Eps = 0.02f;

        /// <summary>Cuánto puede desviarse el ancho de una boca de su ancho nominal. 5 cm deja
        /// dibujar sin pelear con el ratón y no llega a lo que el compositor notaría.</summary>
        public const float WidthTolerance = 0.05f;

        /// <summary>
        /// El resultado del horno. Trae los MOTIVOS y no un simple booleano porque el autor tiene
        /// que poder arreglar el dibujo: "no se pudo hornear" manda a mirar el código, y "la boca 2
        /// mide 1,60 m y los anchos válidos son 2,40 y 5,00" manda a mirar la boca 2.
        /// </summary>
        public sealed class Result
        {
            public float sizeX;
            public float sizeZ;
            public float heightMeters;
            public Wg3Socket[] sockets = System.Array.Empty<Wg3Socket>();
            public Wg3Volume[] volumes = System.Array.Empty<Wg3Volume>();

            /// <summary>Agujeros por encima del suelo, tratados como ventanas. Se cuentan y se
            /// dicen: un autor que esperaba tres bocas y ve dos merece saber por qué.</summary>
            public int windows;

            /// <summary>Dónde queda el origen del MODELO en coordenadas de la pieza. El editor de
            /// salas dibuja centrado en (0,0) y WG3 mide desde la esquina mínima, así que este
            /// desplazamiento es lo que hace que la malla autorada caiga sobre su propia colisión y
            /// no media pieza más allá.</summary>
            public Vector2 pivot;

            public List<string> issues = new List<string>();
            public bool Ok => issues.Count == 0;
        }

        /// <summary>
        /// Hornea el modelo. REGLA R6 — o sale entero o no sale: un horneado a medias deja la huella
        /// nueva con las bocas viejas, y eso no es una pieza con un fallo, es una pieza que el
        /// compositor coloca convencido y que el jugador se encuentra sellada.
        /// </summary>
        public static Result From(RoomDefinition def, string who)
        {
            var result = new Result();
            if (def == null)
            {
                result.issues.Add($"{who}: sin modelo de origen. La pieza se autora en el editor de " +
                                  "salas y el asset guarda el resultado, no al revés");
                return result;
            }

            float t = Mathf.Max(0.001f, def.wallThickness);
            Vector2[] inner = def.InnerContour();
            if (inner == null || inner.Length < 3)
            {
                result.issues.Add($"{who}: el contorno tiene {inner?.Length ?? 0} puntos");
                return result;
            }
            Vector2[] outer = RoomDefinition.OffsetOutward(inner, t);

            // La huella ES el rectángulo envolvente del contorno EXTERIOR. Del exterior y no del
            // interior porque en WG3 el grosor de pared va hacia dentro de la huella: dos piezas
            // encajadas dejan sus paredes espalda contra espalda en vez de solapándose.
            Vector2 min = outer[0], max = outer[0];
            foreach (Vector2 p in outer)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            result.sizeX = max.x - min.x;
            result.sizeZ = max.y - min.y;
            result.heightMeters = def.heightMeters;

            // El origen del modelo, visto desde la esquina mínima. Es la MISMA traslación que se
            // aplica a las cajas unas líneas más abajo, guardada para que la malla la reciba también.
            result.pivot = -min;
            result.sockets = BakeSockets(def, inner, t, min, max, result, who).ToArray();
            result.volumes = BakeVolumes(def, min).ToArray();

            if (result.volumes.Length == 0)
                result.issues.Add($"{who}: el modelo no dio ni una caja de colisión");
            if (result.sockets.Length == 0)
                result.issues.Add($"{who}: ninguna boca utilizable, la pieza no se conecta a nada");

            return result;
        }

        /// <summary>
        /// Las bocas, desde los agujeros de pared del modelo.
        ///
        /// UN AGUJERO NO ES UNA BOCA POR SER UN AGUJERO: una ventana también lo es, y convertirla en
        /// boca haría que el compositor enchufara un pasillo a dos metros del suelo. El criterio es
        /// arrancar del suelo — lo que pisa, conecta; lo que no, se mira.
        /// </summary>
        private static List<Wg3Socket> BakeSockets(RoomDefinition def, Vector2[] inner, float t,
            Vector2 min, Vector2 max, Result result, string who)
        {
            var sockets = new List<Wg3Socket>();
            int n = inner.Length;

            for (int i = 0; i < def.holes.Length; i++)
            {
                RoomDefinition.WallHole h = def.holes[i];
                string what = $"{who}: agujero {i}";

                if (h.baseY > Eps) { result.windows++; continue; }

                // Una reja se dibuja como hueco y para la colisión es pared entera. Aceptarla como
                // boca daría el peor fallo posible: el compositor conecta ahí y el jugador ve una
                // puerta contra la que se choca.
                if (h.grateBars > 0)
                {
                    result.issues.Add($"{what} lleva {h.grateBars} barrotes: para la colisión eso es " +
                                      "pared, no una boca. Quita los barrotes, o súbelo del suelo y " +
                                      "será una ventana");
                    continue;
                }
                if (h.spanCorners)
                {
                    result.issues.Add($"{what} dobla la esquina: una boca de WG3 vive en UN lado, y " +
                                      "el compositor no sabe casar media boca en cada uno");
                    continue;
                }
                if (h.level != 0)
                {
                    result.issues.Add($"{what} está en el piso {h.level}: conectar desde una " +
                                      "entreplanta es F5, y hasta entonces el validador exige que " +
                                      "las cotas casen");
                    continue;
                }

                int side = ((h.side % n) + n) % n;
                Vector2 a = inner[side], b = inner[(side + 1) % n];
                Vector2 dir = b - a;

                bool alongX = Mathf.Abs(dir.y) < Eps;
                bool alongZ = Mathf.Abs(dir.x) < Eps;
                if (alongX == alongZ)
                {
                    result.issues.Add($"{what} cae en una pared diagonal. Por dentro la pieza puede " +
                                      "ser todo lo irregular que quieras, pero la pared que lleva " +
                                      "una boca tiene que ser recta: las bocas viven en uno de 4 lados");
                    continue;
                }

                // El contorno se garantiza ANTIHORARIO (EnsureContourCCW), y para un contorno
                // antihorario la normal hacia afuera de a→b es (dy, −dx). Con el sentido al revés
                // saldrían todas las bocas mirando hacia dentro y ninguna llegaría al borde.
                Vector2 outward = new Vector2(dir.y, -dir.x).normalized;
                int wgSide = outward.y > 0.5f ? 0 : outward.x > 0.5f ? 1 : outward.y < -0.5f ? 2 : 3;

                Vector2 centre = a + dir * Mathf.Clamp01(h.along);
                Vector2 face = centre + outward * t;
                float toEdge = wgSide == 0 ? Mathf.Abs(face.y - max.y)
                             : wgSide == 1 ? Mathf.Abs(face.x - max.x)
                             : wgSide == 2 ? Mathf.Abs(face.y - min.y)
                                           : Mathf.Abs(face.x - min.x);
                if (toEdge > Eps)
                {
                    result.issues.Add($"{what} está a {toEdge:0.00} m del borde de la huella: da a " +
                                      "un entrante y no al exterior, así que la pieza vecina se " +
                                      "enchufaría contra la pared de al lado");
                    continue;
                }

                // Del punto del dibujo al offset de WG3, recorriendo el perímetro EN SENTIDO HORARIO
                // desde la esquina (0, D) — el contrato de Wg3Socket, que es lo que hace que girar
                // la pieza no toque un solo offset.
                Vector2 local = centre - min;
                float offset = wgSide == 0 ? local.x
                             : wgSide == 1 ? result.sizeZ - local.y
                             : wgSide == 2 ? result.sizeX - local.x
                                           : local.y;

                if (!TypeOfWidth(h.width, out Wg3SocketType type))
                {
                    result.issues.Add($"{what} mide {h.width:0.00} m de ancho. Los anchos que el " +
                                      $"compositor sabe casar son {Wg3Catalog.CorridorWidth:0.00} m " +
                                      $"(pasillo) y {Wg3Catalog.WideWidth:0.00} m (vano ancho): el " +
                                      "ancho ES el tipo de la boca");
                    continue;
                }

                sockets.Add(new Wg3Socket(wgSide, offset, h.width, type, 0f, h.height));
            }

            return sockets;
        }

        /// <summary>
        /// La chuleta. Traslada las cajas al origen en esquina mínima y las clasifica.
        ///
        /// Ninguna sale como <see cref="Wg3VolumeKind.Decoration"/>, y no es un olvido:
        /// <see cref="RoomColliderBuilder"/> solo emite lo que BLOQUEA, así que R25 —la decoración
        /// no cruza la frontera de autoridad— se cumple aquí por construcción. El rodapié y las
        /// molduras de una pieza autorada viven en su malla, que el servidor no llega a ver.
        /// </summary>
        private static List<Wg3Volume> BakeVolumes(RoomDefinition def, Vector2 min)
        {
            var volumes = new List<Wg3Volume>();
            foreach (RoomPool.CollisionBox box in RoomColliderBuilder.Build(def))
            {
                Vector3 c = box.center;
                c.x -= min.x;
                c.z -= min.y;

                float top = c.y + box.size.y * 0.5f;
                float bottom = c.y - box.size.y * 0.5f;
                Wg3VolumeKind kind = top <= 0.001f ? Wg3VolumeKind.Floor
                                   : bottom >= def.heightMeters - 0.001f ? Wg3VolumeKind.Ceiling
                                                                         : Wg3VolumeKind.Wall;

                volumes.Add(new Wg3Volume
                {
                    center = c,
                    size = box.size,
                    yawDegrees = box.yawDegrees,
                    kind = kind
                });
            }
            return volumes;
        }

        public static bool TypeOfWidth(float width, out Wg3SocketType type)
        {
            if (Mathf.Abs(width - Wg3Catalog.CorridorWidth) <= WidthTolerance)
            {
                type = Wg3SocketType.Corridor;
                return true;
            }
            if (Mathf.Abs(width - Wg3Catalog.WideWidth) <= WidthTolerance)
            {
                type = Wg3SocketType.Wide;
                return true;
            }
            type = Wg3SocketType.Corridor;
            return false;
        }
    }
}
