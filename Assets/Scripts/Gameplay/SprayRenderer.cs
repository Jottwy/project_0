using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-068 S2 — dibuja las pintadas de spray sobre las paredes del mundo.
    ///
    /// Rasteriza los trazos UNA vez, al llegar la pintada, a una <see cref="Texture2D"/>
    /// pequeña, y la cuelga de un quad pegado a la pared. Coste por frame: cero — ni un
    /// <c>Update</c> que recorra pintadas, ni una <c>DecalRendererFeature</c> en el renderer
    /// (ADR-068 decisión 7 la prohíbe expresamente para esto). Es el mismo truco que
    /// <c>ChunkRenderer.CreateFloorStains</c> ya usa para las manchas procedurales.
    ///
    /// Dos fuentes, un solo camino: las pintadas que vienen CON el chunk (hidratación) y la que
    /// llega suelta cuando alguien acaba de pintar (<c>spray_placed</c>) pasan las dos por
    /// <see cref="Show"/>, deduplicadas por id. Sin esa dedup, recargar un chunk duplicaría cada
    /// pintada que ya estuviera en pantalla.
    ///
    /// Auto-arranque en un objeto propio con <c>DontDestroyOnLoad</c>, mismo patrón que
    /// <c>TorchShadowCaster</c> (ADR-065) y <c>PlayerPoseTransmitter</c>: nada que colocar a mano
    /// en una escena.
    /// </summary>
    [DisallowMultipleComponent]
    public class SprayRenderer : MonoBehaviour
    {
        /// <summary>
        /// Lado de la textura rasterizada. Es la MISMA retícula en la que el backend cuantiza los
        /// puntos (256×256), así que un punto del wire cae en un téxel exacto y no hay
        /// re-muestreo que emborrone el trazo.
        /// </summary>
        public const int CanvasPixels = 256;

        /// <summary>
        /// Entradas de la paleta. Espejo de <c>world::spray::PALETTE_LEN</c>: el backend rechaza
        /// cualquier índice por encima, así que uno recibido siempre cae dentro de la tabla.
        /// </summary>
        public const int PaletteLen = 16;

        /// <summary>
        /// Separación de la pared, en metros. Suficiente para que no haya z-fighting con el muro
        /// y demasiado poco para leerse como despegada. Mismo orden que el offset con el que
        /// <c>ChunkRenderer</c> pega sus manchas.
        /// </summary>
        private const float WallOffset = 0.003f;

        /// <summary>
        /// La paleta vive AQUÍ y no en el backend a propósito: por el wire viaja un índice
        /// (<c>SprayStrokeMsg.color</c>), que acota el dato por construcción y deja el color real
        /// como decisión de arte. Cambiar un color de esta tabla repinta el mundo entero sin
        /// tocar un byte de save ni de protocolo.
        ///
        /// Tiene que tener exactamente <c>PALETTE_LEN</c> (16) entradas — el backend rechaza
        /// cualquier índice por encima, así que un índice recibido siempre cae dentro.
        /// </summary>
        private static readonly Color32[] Palette =
        {
            new Color32(20, 20, 20, 255),      // 0  negro hollín
            new Color32(240, 240, 235, 255),   // 1  blanco tiza
            new Color32(200, 30, 30, 255),     // 2  rojo
            new Color32(230, 120, 20, 255),    // 3  naranja
            new Color32(235, 210, 40, 255),    // 4  amarillo
            new Color32(60, 170, 60, 255),     // 5  verde
            new Color32(40, 110, 200, 255),    // 6  azul
            new Color32(120, 60, 180, 255),    // 7  morado
            new Color32(230, 100, 170, 255),   // 8  rosa
            new Color32(110, 70, 40, 255),     // 9  marrón
            new Color32(130, 130, 130, 255),   // 10 gris
            new Color32(40, 200, 190, 255),    // 11 turquesa
            new Color32(150, 200, 40, 255),    // 12 lima
            new Color32(200, 170, 120, 255),   // 13 arena
            new Color32(90, 20, 20, 255),      // 14 óxido
            new Color32(20, 40, 80, 255),      // 15 azul noche
        };

        /// <summary>El color real de un índice del wire. Público para que un test pueda
        /// comprobar que lo rasterizado usa el color que la paleta promete.</summary>
        public static Color32 ColorOf(byte paletteIndex) => Palette[paletteIndex % Palette.Length];

        /// <summary>Tamaño REAL de la tabla, para casarlo con <see cref="PaletteLen"/>.</summary>
        public static int PaletteCount => Palette.Length;

        private static SprayRenderer _instance;

        /// <summary>El renderizador vivo, para que el pintor pueda pedirle la vista previa.</summary>
        public static SprayRenderer Instance => _instance;

        /// <summary>
        /// Una pintada materializada. Guarda la textura y el material APARTE del GameObject
        /// porque destruir el objeto NO los libera: son recursos creados en runtime, y el que
        /// sea Unity quien los referencie no los hace suyos. Sin esto, cada pintada vista deja
        /// 256 KB de textura (256×256 RGBA) colgados para siempre — cien pintadas en una sesión
        /// larga son 25 MB que no vuelven.
        /// </summary>
        private readonly struct Live
        {
            public readonly GameObject Go;
            public readonly Texture2D Tex;
            public readonly Material Mat;
            public readonly int Cx, Cz;
            public readonly byte Layer;

            public Live(GameObject go, Texture2D tex, Material mat, int cx, int cz, byte layer)
            {
                Go = go; Tex = tex; Mat = mat; Cx = cx; Cz = cz; Layer = layer;
            }

            /// <summary>La pared a la que pertenece, en la misma forma que usa el contador de
            /// apilado. Se guarda para poder retirar ESA entrada al soltarla, sin tocar las
            /// de las paredes que siguen vivas.</summary>
            public (int, int, byte) Wall => (Cx, Cz, Layer);
        }

        /// <summary>Lo pintado, por id de pintada. El id lo acuña el host y es único de mundo.</summary>
        private readonly Dictionary<uint, Live> _spawned = new Dictionary<uint, Live>();

        /// <summary>
        /// Cuántas pintadas se han materializado ya en cada pared (chunk + capa). Es lo que
        /// separa en profundidad a las que se solapan: sin ello todas se pegan a la MISMA
        /// distancia del muro, hacen z-fighting donde coinciden, y no hay ninguna garantía de que
        /// la nueva tape a la vieja — que es exactamente la decisión 4 de ADR-068.
        /// </summary>
        private readonly Dictionary<(int, int, byte), int> _stacked =
            new Dictionary<(int, int, byte), int>();

        /// <summary>
        /// Radio de conservación en chunks alrededor del jugador. Más allá se sueltan con su
        /// textura: vuelven solas cuando el chunk se vuelve a pedir, porque las pintadas viajan
        /// CON el chunk. Generoso a propósito — soltar y rehidratar cuesta rasterizar otra vez.
        /// </summary>
        private const int KeepRadiusChunks = 3;
        private const float SweepInterval = 5f;
        private float _nextSweep;

        private Mesh _quad;
        private IPCClient _client;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("SprayRenderer");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SprayRenderer>();
        }

        private void OnEnable()
        {
            _quad = BuildQuad();
            TryHook();
        }

        private void Update()
        {
            // El IPCClient puede no existir todavía cuando este objeto arranca (el bootstrap de
            // sesión lo crea más tarde). Reintento barato por frame hasta engancharse — un
            // enganche perdido significaría un mundo sin una sola pintada, en silencio.
            if (_client == null) TryHook();

            if (Time.unscaledTime >= _nextSweep)
            {
                _nextSweep = Time.unscaledTime + SweepInterval;
                var cam = Camera.main;
                if (cam != null) SweepFarAwayFrom(cam.transform.position);
            }
        }

        /// <summary>
        /// Suelta las pintadas lejanas CON su textura y su material. Sin esta pasada el
        /// diccionario solo crece: cada pintada vista en la sesión se queda con sus 256 KB, y
        /// recorrer el mundo un rato basta para acumular decenas de MB que nunca se liberan.
        ///
        /// Soltar es seguro porque las pintadas viajan con el chunk: cuando el jugador vuelva,
        /// `chunk_data` las trae otra vez y se rasterizan de nuevo.
        ///
        /// Recibe la posición en vez de leer <c>Camera.main</c> para poder probarla sin escena.
        /// </summary>
        public void SweepFarAwayFrom(Vector3 viewer)
        {
            if (_spawned.Count == 0) return;

            int pcx = Mathf.FloorToInt(viewer.x / SprayMsg.ChunkSize);
            int pcz = Mathf.FloorToInt(viewer.z / SprayMsg.ChunkSize);

            List<uint> drop = null;
            foreach (var kv in _spawned)
            {
                if (Mathf.Abs(kv.Value.Cx - pcx) <= KeepRadiusChunks &&
                    Mathf.Abs(kv.Value.Cz - pcz) <= KeepRadiusChunks)
                    continue;
                (drop ??= new List<uint>()).Add(kv.Key);
            }
            if (drop == null) return;

            foreach (var id in drop)
            {
                var live = _spawned[id];
                // El contador de apilado se va CON su pared, y SOLO con ella. Vaciarlo entero
                // aquí devolvía a cero paredes cuyas pintadas siguen materializadas y a su
                // profundidad: la siguiente pintada sobre una de ellas volvía a `depth = 0`,
                // coplanar con la más antigua, y el "pintar encima" de la decisión 4 de ADR-068
                // dejaba de estar garantizado — z-fighting y gana el azar del depth buffer.
                //
                // Retirar por clave es exacto porque la purga es por chunk y la clave lleva el
                // chunk dentro: todas las pintadas de una misma pared caen juntas o se quedan
                // juntas, nunca a medias. Las que caen vuelven en el mismo orden al rehidratar
                // el chunk y se vuelven a escalonar igual.
                _stacked.Remove(live.Wall);
                Release(live);
                _spawned.Remove(id);
            }
        }

        /// <summary>Destruye objeto, textura y material. Los tres, o la textura sobrevive.</summary>
        private void Release(Live live)
        {
            Discard(live.Go);
            Discard(live.Mat);
            Discard(live.Tex);
        }

        /// <summary>
        /// Destruye eligiendo la llamada que toca según dónde se esté.
        ///
        /// No es cosmético: en modo edición <c>Destroy</c> NO destruye, escribe un error
        /// ("Destroy may not be called from edit mode!") y sigue. Los tres tests de apilado de
        /// esta clase se caían por ese error — no por sus aserciones — así que la purga y sus
        /// texturas de 256 KB no se estaban comprobando en ninguna parte. En juego el
        /// comportamiento no cambia.
        /// </summary>
        private static void Discard(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        private void TryHook()
        {
            var client = IPCClient.Instance;
            if (client == null || client == _client) return;
            _client = client;
            _client.AddChunkDataListener(OnChunkData);
            _client.AddSprayListener(Show);
        }

        private void OnDisable()
        {
            if (_client != null)
            {
                _client.RemoveChunkDataListener(OnChunkData);
                _client.RemoveSprayListener(Show);
                _client = null;
            }
            Clear();
            Discard(_quad);
        }

        private void OnChunkData(GridChunkDataMsg data)
        {
            if (data == null || data.sprays == null) return;
            // En el orden en que vienen: el backend las manda de la más antigua a la más nueva,
            // que es el orden de render. No se reordena aquí.
            for (int i = 0; i < data.sprays.Length; i++)
                Show(data.sprays[i]);
        }

        /// <summary>
        /// Materializa una pintada. Idempotente por id: llamarla dos veces con la misma pintada
        /// (hidratación del chunk + eco en vivo, o un chunk que vuelve a streamear) no duplica
        /// nada.
        /// </summary>
        public void Show(SprayMsg spray)
        {
            if (spray == null || spray.strokes == null || spray.strokes.Length == 0) return;
            if (_spawned.ContainsKey(spray.id)) return;

            var tex = Rasterize(spray);
            if (tex == null) return;

            // Llegó la autoritativa: fuera la previa, o se verían las dos superpuestas.
            ClearPreview();

            // Cada pintada nueva sobre la MISMA pared se despega una pizca más que la anterior.
            // Es lo que hace real el "pintar encima" de ADR-068: a la misma distancia del muro
            // dos quads solapados hacen z-fighting y quién tapa a quién lo decide el azar del
            // depth buffer, no el orden en que se pintaron.
            var key = (spray.cx, spray.cz, spray.layer);
            _stacked.TryGetValue(key, out int depth);
            _stacked[key] = depth + 1;

            var go = BuildQuadObject(spray, tex, $"Spray_{spray.id}", depth, out var mat);
            _spawned[spray.id] = new Live(go, tex, mat, spray.cx, spray.cz, spray.layer);
        }

        /// <summary>El quad con su textura, colocado contra la pared. Compartido por la pintada
        /// definitiva y por la previa, para que las dos se vean EXACTAMENTE igual.</summary>
        private GameObject BuildQuadObject(SprayMsg spray, Texture2D tex, string name,
            int stackDepth, out Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            // Sin collider, y esto NO es un descuido: una pintada no puede frenar al jugador ni
            // aparecer en los raycasts de construcción o interacción. Es pintura, no geometría.
            go.transform.SetPositionAndRotation(spray.WorldPos, Quaternion.Euler(0f, spray.yaw, 0f));
            // 0,15 mm por capa: suficiente para que el depth buffer las ordene, y con el cap de
            // 64 por chunk el peor caso son ~10 mm de separación, invisible contra el muro.
            go.transform.position += go.transform.forward * (WallOffset + stackDepth * StackStep);
            go.transform.localScale = new Vector3(spray.sizeX, spray.sizeY, 1f);

            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;

            // Blanco: el color va en los téxeles, no en el tinte, para que una sola pintada pueda
            // llevar trazos de varios colores.
            mat = MaterialHelper.MakeTransparent(Color.white);
            if (mat != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                mat.mainTexture = tex;
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.05f);
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
                mr.sharedMaterial = mat;
            }
            return go;
        }

        /// <summary>Separación extra por cada pintada ya presente en la misma pared.</summary>
        private const float StackStep = 0.00015f;

        /// <summary>Cuántas pintadas hay materializadas ahora mismo. Para diagnóstico y tests.</summary>
        public int LiveCount => _spawned.Count;

        /// <summary>
        /// A qué profundidad saldría la SIGUIENTE pintada de esa pared. Para diagnóstico y tests:
        /// es el estado que una purga parcial corrompía, y no se puede leer de la escena porque
        /// vive en el contador, no en los quads ya colocados.
        /// </summary>
        public int NextStackDepthOn(int cx, int cz, byte layer)
        {
            _stacked.TryGetValue((cx, cz, layer), out int depth);
            return depth;
        }

        // ── Vista previa ──────────────────────────────────────────────────────────────────
        //
        // El trazo EN CURSO, dibujado en local mientras el jugador arrastra. No es un adorno:
        // sin esto se pinta a ciegas y el dibujo no aparece hasta que el host lo devuelve, más
        // de un segundo después de soltar. La previa es lo único que hace que la mecánica se
        // sienta como pintar y no como rellenar un formulario.
        //
        // Es puramente local: no viaja, no se guarda, y desaparece en cuanto llega la pintada
        // autoritativa (que puede diferir — el host manda).
        private Live _preview;
        private bool _hasPreview;

        /// <summary>
        /// Refresca el trazo en curso. Llamar mientras se pinta — se rehace 30 veces por segundo,
        /// así que soltar bien la textura anterior aquí importa MÁS que en ninguna otra parte:
        /// una pintada larga fabrica cientos de texturas de 256 KB, una por refresco.
        /// </summary>
        public void ShowPreview(SprayMsg spray)
        {
            ClearPreview();
            if (spray == null || spray.strokes == null || spray.strokes.Length == 0) return;

            var tex = Rasterize(spray);
            if (tex == null) return;

            // La previa va SIEMPRE encima de lo que ya hubiera en esa pared: se está pintando
            // ahora mismo, así que es lo más nuevo por definición.
            var key = (spray.cx, spray.cz, spray.layer);
            _stacked.TryGetValue(key, out int depth);

            var go = BuildQuadObject(spray, tex, "Spray_Preview", depth, out var mat);
            _preview = new Live(go, tex, mat, spray.cx, spray.cz, spray.layer);
            _hasPreview = true;
        }

        /// <summary>
        /// Retira la previa. Se llama al mandar la pintada: a partir de ahí manda la copia
        /// autoritativa, y tener las dos a la vez daría doble trazo mientras llega el eco.
        /// </summary>
        public void ClearPreview()
        {
            if (!_hasPreview) return;
            Release(_preview);
            _preview = default;
            _hasPreview = false;
        }

        public void Clear()
        {
            foreach (var kv in _spawned)
                Release(kv.Value);
            _spawned.Clear();
            _stacked.Clear();
            ClearPreview();
        }

        /// <summary>
        /// Trazos → téxeles. Fondo transparente; solo la pintura tiene alfa.
        ///
        /// Los puntos vienen intercalados (X, Y) sobre la retícula de 256, así que un par de
        /// bytes es un punto y la longitud impar no puede ocurrir (el host la rechaza). Entre dos
        /// puntos consecutivos se traza una línea: el bote se mueve continuo, y unir con líneas
        /// es lo que separa un trazo de una fila de lunares cuando la mano va rápida.
        /// </summary>
        public static Texture2D Rasterize(SprayMsg spray)
        {
            var pixels = new Color32[CanvasPixels * CanvasPixels];
            // Color32 por defecto ya es (0,0,0,0) — transparente. No hace falta rellenar.

            bool painted = false;
            for (int s = 0; s < spray.strokes.Length; s++)
            {
                var stroke = spray.strokes[s];
                var pts = stroke.points;
                if (pts == null || pts.Length < 2) continue;

                Color32 color = Palette[stroke.color % Palette.Length];
                // El grosor llega en la misma retícula de 256 que los puntos. Mínimo 1 téxel:
                // un trazo de grosor 0 sería una pintada invisible, no una pintada fina.
                int radius = Mathf.Max(1, stroke.width / 2);

                int prevX = -1, prevY = -1;
                for (int i = 0; i + 1 < pts.Length; i += 2)
                {
                    int x = pts[i];
                    int y = pts[i + 1];
                    if (prevX >= 0) DrawLine(pixels, prevX, prevY, x, y, radius, color);
                    else Dab(pixels, x, y, radius, color);
                    prevX = x;
                    prevY = y;
                    painted = true;
                }
            }

            if (!painted) return null;

            var tex = new Texture2D(CanvasPixels, CanvasPixels, TextureFormat.RGBA32, false)
            {
                // Clamp y no Repeat: con Repeat, un trazo que roce el borde reaparecería por el
                // lado contrario del lienzo.
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        /// <summary>Bresenham entero, con un disco en cada paso para dar grosor.</summary>
        private static void DrawLine(Color32[] px, int x0, int y0, int x1, int y1, int radius, Color32 c)
        {
            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                Dab(px, x0, y0, radius, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>Un disco lleno de radio <paramref name="radius"/> centrado en (cx, cy).</summary>
        private static void Dab(Color32[] px, int cx, int cy, int radius, Color32 c)
        {
            int r2 = radius * radius;
            for (int y = cy - radius; y <= cy + radius; y++)
            {
                if (y < 0 || y >= CanvasPixels) continue;
                int dy = y - cy;
                for (int x = cx - radius; x <= cx + radius; x++)
                {
                    if (x < 0 || x >= CanvasPixels) continue;
                    int dx = x - cx;
                    if (dx * dx + dy * dy > r2) continue;
                    px[y * CanvasPixels + x] = c;
                }
            }
        }

        /// <summary>
        /// Quad de 1×1 centrado en el origen, mirando a +Z. Mesh propia y no
        /// <c>CreatePrimitive</c> porque el primitivo trae un <c>MeshCollider</c> — y una pintada
        /// con collider bloquearía al jugador y ensuciaría los raycasts de construcción.
        /// </summary>
        private static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "SprayQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            };
            // U INVERTIDA respecto a la X local, y no es un despiste. Quien mira una cara que
            // apunta a +Z está en +Z mirando hacia −Z, y desde ahí su derecha es −X. Con las UV
            // "naturales" (U creciendo con +X) el dibujo saldría EN ESPEJO — invisible con una
            // flecha, obvio en cuanto alguien pinte una letra o un número, que es justo para lo
            // que existe esta mecánica. Así la X del lienzo crece hacia la derecha del que mira.
            mesh.uv = new[]
            {
                new Vector2(1f, 0f), new Vector2(0f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            // Cara visible hacia +Z local, o sea la MISMA dirección que `yaw` y que el
            // desplazamiento de separación. El Quad de Unity mira a −Z; copiar su devanado tal
            // cual habría metido la pintada DENTRO de la pared.
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
