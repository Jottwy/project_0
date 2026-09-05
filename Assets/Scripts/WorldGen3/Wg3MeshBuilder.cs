using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Triangula los volúmenes de <see cref="Wg3Geometry"/>.
    ///
    /// No lee la pieza: lee los volúmenes. Es la mitad "lo que se ve" del contrato de fuente única,
    /// y su gemela —"lo que bloquea"— es simplemente filtrar esos mismos volúmenes por
    /// <see cref="Wg3Volume.IsSolid"/>. Por eso no hay un `Wg3ColliderBuilder`: no hace falta un
    /// segundo recorrido cuando el primero ya produjo la verdad.
    ///
    /// Cuatro submallas, una por material, en el orden de <see cref="SubMesh"/>. Se reparten por
    /// función y no por pieza porque es lo que permite que el suelo sea moqueta en todo el mundo y
    /// la pared sea pared: la continuidad de material en la junta (R31) sale de que dos piezas
    /// distintas mandan sus caras a la misma submalla.
    ///
    /// DEUDA CONOCIDA Y ASUMIDA PARA F0: se emiten las seis caras de cada caja, también las que
    /// quedan enterradas contra otra caja. Sobran triángulos. Se acepta porque F0 mide si la
    /// geometría es correcta, no si es barata; el fundido por chunk es problema de F4, donde
    /// además hay que fundir varias piezas en una malla o el coste no es de triángulos sino de
    /// draw calls.
    /// </summary>
    public static class Wg3MeshBuilder
    {
        /// <summary>Repeticiones de textura por metro de mundo. Era 1 (una repetición por metro,
        /// que en pared se leía como grano); a 0,5 cada baldosa del papel mide dos metros.</summary>
        public const float UvPerMetre = 0.5f;

        public static class SubMesh
        {
            public const int Floor = 0;
            public const int Structure = 1;
            public const int Ceiling = 2;
            public const int Decoration = 3;
            public const int Count = 4;
        }

        public static int SubMeshFor(Wg3VolumeKind kind)
        {
            switch (kind)
            {
                case Wg3VolumeKind.Floor: return SubMesh.Floor;
                case Wg3VolumeKind.Ceiling: return SubMesh.Ceiling;
                case Wg3VolumeKind.Decoration: return SubMesh.Decoration;
                case Wg3VolumeKind.Casing: return SubMesh.Decoration;
                default: return SubMesh.Structure;
            }
        }

        /// <summary>Construye la malla de una lista de volúmenes. Las posiciones se emiten
        /// RELATIVAS a <paramref name="origin"/> para que el <c>GameObject</c> se pueda situar en
        /// el mundo sin que los vértices acumulen coordenadas grandes — a 5 km del origen un float
        /// ya no distingue el milímetro y el rodapié empieza a coserse mal.</summary>
        public static Mesh Build(IReadOnlyList<Wg3Volume> volumes, Vector3 origin, Mesh into = null)
        {
            var verts = new List<Vector3>(volumes.Count * 24);
            var normals = new List<Vector3>(volumes.Count * 24);
            var uvs = new List<Vector2>(volumes.Count * 24);
            var tris = new List<int>[SubMesh.Count];
            for (int i = 0; i < SubMesh.Count; i++) tris[i] = new List<int>(volumes.Count * 12);

            for (int i = 0; i < volumes.Count; i++)
            {
                Wg3Volume v = volumes[i];
                if (v.shape == Wg3Shape.Box && v.kind == Wg3VolumeKind.Casing)
                    AddCasingBox(verts, normals, uvs, tris[SubMeshFor(v.kind)],
                        v.center - origin, v.size, v.yawDegrees);
                else if (v.shape == Wg3Shape.Box)
                    AddBox(verts, normals, uvs, tris[SubMeshFor(v.kind)],
                        v.center - origin, v.size, v.yawDegrees);
                else if (v.shape == Wg3Shape.Arch && v.kind == Wg3VolumeKind.Casing)
                    AddArchCasing(verts, normals, uvs, tris[SubMeshFor(v.kind)],
                        v.center - origin, v.size);
                else if (v.shape == Wg3Shape.Arch)
                    // En tono de SALA, como la mocheta: se probó en la submalla de decoración para
                    // que el intradós fuese del tono del marco, y salieron las enjutas naranjas
                    // (el material de decoración del estilo de ESTE lado, no el del marco, que
                    // lleva el estilo del lado `a`).
                    AddArch(verts, normals, uvs, tris[SubMeshFor(v.kind)],
                        v.center - origin, v.size);
                else
                    AddPrism(verts, normals, uvs, tris[SubMeshFor(v.kind)],
                        v.center - origin, v.size, v.yawDegrees, v.shape);
            }

            Mesh mesh = into != null ? into : new Mesh();
            mesh.Clear();
            mesh.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            // Joel, 2026-09-06: «las texturas se ven muy pequeñas, reduce la repetición a la
            // mitad». Las UV se emiten en METROS en todos los emisores; el factor va aquí, una vez,
            // y no en cada material, para que gotelé, moqueta, techo y marco repitan igual.
            for (int i = 0; i < uvs.Count; i++) uvs[i] *= UvPerMetre;
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = SubMesh.Count;
            for (int i = 0; i < SubMesh.Count; i++) mesh.SetTriangles(tris[i], i);
            // **Y TANGENTES, sin las cuales un mapa de normales no existe.**
            //
            // La malla salía con posición, normal y UV, y los cuatro `Wg3_*.mat` tenían `_BumpMap`
            // vacío, así que nadie las echaba en falta. En cuanto el material lleva normal, el
            // sombreado necesita la base tangente: sin ella URP usa una arbitraria y el relieve sale
            // girado por cara, que se ve como suciedad y no como textura. Se calculan aquí y no en el
            // llamador porque aquí es donde ya están las UV.
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Un cubo unitario centrado en el origen, con UNA sola submalla.
        ///
        /// Es para la decoración que se dibuja con un material único y no necesita el reparto por
        /// función —hoy, la luminaria—: se escala con el transform, así que una sola instancia sirve
        /// para todas las de la sesión. Va aparte de <see cref="Build"/> a propósito: aquélla emite
        /// las cuatro submallas y con un solo material asignado las tres restantes se quedarían sin
        /// con qué dibujarse.
        /// </summary>
        public static Mesh BuildUnitCube()
        {
            var verts = new List<Vector3>(24);
            var normals = new List<Vector3>(24);
            var uvs = new List<Vector2>(24);
            var tris = new List<int>(36);
            AddBox(verts, normals, uvs, tris, Vector3.zero, Vector3.one, 0f);

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Una caja con 24 vértices: cuatro por cara, para que cada cara tenga su normal
        /// dura. Con 8 compartidos las normales se promedian y una esquina de pared se ve como un
        /// bisel redondeado bajo cualquier luz rasante — que es toda la luz de este juego.</summary>
        private static void AddBox(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
            List<int> tris, Vector3 centre, Vector3 size, float yawDegrees)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDegrees, 0f);
            Vector3 h = size * 0.5f;

            // (normal local, tangente U local, tangente V local) por cara.
            AddFace(verts, normals, uvs, tris, centre, rot, h, Vector3.right, Vector3.forward, Vector3.up, size.z, size.y);
            AddFace(verts, normals, uvs, tris, centre, rot, h, Vector3.left, Vector3.back, Vector3.up, size.z, size.y);
            AddFace(verts, normals, uvs, tris, centre, rot, h, Vector3.forward, Vector3.left, Vector3.up, size.x, size.y);
            AddFace(verts, normals, uvs, tris, centre, rot, h, Vector3.back, Vector3.right, Vector3.up, size.x, size.y);
            AddFace(verts, normals, uvs, tris, centre, rot, h, Vector3.up, Vector3.right, Vector3.forward, size.x, size.z);
            AddFace(verts, normals, uvs, tris, centre, rot, h, Vector3.down, Vector3.right, Vector3.back, size.x, size.z);
        }

        /// <summary>Ancho de las dos bandas de borde del perfil del marco, a cada lado de la banda
        /// central.</summary>
        public const float CasingEdgeM = 0.02f;
        /// <summary>Cuánto se hunden las bandas de borde respecto a la central, por cada cara. Con
        /// `CASING_PROUD_CM` = 4 en el servidor, el borde queda a 2 cm de la pared y el centro a 4.</summary>
        public const float CasingStepM = 0.02f;
        /// <summary>Alto del zócalo de la jamba: un bloque liso en el arranque, como en toda
        /// carpintería de puerta que no sea de obra.</summary>
        public const float CasingPlinthHM = 0.22f;
        /// <summary>Cuánto sobresale el zócalo del resto de la jamba, en ancho y en fondo.</summary>
        public const float CasingPlinthExtraM = 0.01f;

        private static Vector3 Axis(int i, float v)
        {
            var r = Vector3.zero;
            r[i] = v;
            return r;
        }

        /// <summary>
        /// ADR-125 enm. 2 (nota del perfil) — una pieza recta del MARCO de la puerta: jamba o
        /// cabeza plana. Una caja lisa de 9 cm se leía como cinta pegada a la pared; una moldura
        /// se lee por sus LÍNEAS DE SOMBRA. Tres cajas en vez de una: banda central a fondo
        /// completo y dos bandas de borde de <see cref="CasingEdgeM"/> hundidas
        /// <see cref="CasingStepM"/> por cada cara. Simétrico a propósito: el cliente no sabe de
        /// qué lado queda la luz de la puerta, y un perfil de dos escalones a cada lado es una
        /// moldura corriente. La jamba (la que sube) lleva además un zócalo liso de
        /// <see cref="CasingPlinthHM"/> un centímetro mayor en todo.
        ///
        /// Ejes por tamaño: el largo `L` es el mayor; de los otros dos, el fondo `A` (a través de
        /// la pared) es el horizontal, o el mayor si los dos lo son; el perfil `P` es el que queda.
        /// Jamba (9 × 240 × 38): L = y, A = z, P = x. Cabeza (138 × 10 × 38): L = x, A = z, P = y.
        /// </summary>
        private static void AddCasingBox(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
            List<int> tris, Vector3 centre, Vector3 size, float yawDegrees)
        {
            int L = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
            int a = (L + 1) % 3, b = (L + 2) % 3;
            int A = a == 1 ? b : (b == 1 ? a : (size[a] >= size[b] ? a : b));
            int P = 3 - L - A;
            float wP = size[P];
            if (wP <= 2f * CasingEdgeM + 0.005f || size[A] <= 2f * CasingStepM + 0.005f)
            {
                AddBox(verts, normals, uvs, tris, centre, size, yawDegrees);
                return;
            }
            Quaternion rot = Quaternion.Euler(0f, yawDegrees, 0f);
            float len = size[L];
            float lenOff = 0f;
            if (L == 1 && len > 2f * CasingPlinthHM)
            {
                Vector3 plinth = size;
                plinth[1] = CasingPlinthHM;
                plinth[P] += 2f * CasingPlinthExtraM;
                plinth[A] += 2f * CasingPlinthExtraM;
                AddBox(verts, normals, uvs, tris,
                    centre + rot * Axis(1, (CasingPlinthHM - size.y) * 0.5f), plinth, yawDegrees);
                len -= CasingPlinthHM;
                lenOff = CasingPlinthHM * 0.5f;
            }
            Vector3 c = centre + rot * Axis(L, lenOff);
            Vector3 mid = size;
            mid[L] = len;
            mid[P] = wP - 2f * CasingEdgeM;
            AddBox(verts, normals, uvs, tris, c, mid, yawDegrees);
            Vector3 edge = size;
            edge[L] = len;
            edge[P] = CasingEdgeM;
            edge[A] = size[A] - 2f * CasingStepM;
            float off = (wP - CasingEdgeM) * 0.5f;
            AddBox(verts, normals, uvs, tris, c + rot * Axis(P, -off), edge, yawDegrees);
            AddBox(verts, normals, uvs, tris, c + rot * Axis(P, off), edge, yawDegrees);
        }

        /// <summary>
        /// ADR-125 — un PRISMA inscrito en la caja del volumen: cilindro, media luna u octógono.
        ///
        /// Es el primer volumen no-caja de esta clase, y la razón de que exista el byte `shape`:
        /// la unión de cajas nunca da un círculo (dos cuadrados girados 45° dan una estrella de
        /// ocho puntas, no un octógono), y lo que hace que un pilar se lea redondo no es el número
        /// de lados sino la NORMAL SUAVE en la cara curva, que una caja no puede tener.
        ///
        /// Cara curva con normal radial por vértice (cilindro y media luna) o dura por cara
        /// (octógono: sus aristas son intención). Tapas planas arriba y abajo. UV en metros como en
        /// <see cref="AddFace"/>: `u` es la longitud de arco recorrida, `v` la altura.
        /// </summary>
        private static void AddPrism(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
            List<int> tris, Vector3 centre, Vector3 size, float yawDegrees, byte shape)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDegrees, 0f);
            float r = size.x * 0.5f;
            float h = size.y;
            float hy = h * 0.5f;
            bool half = shape == Wg3Shape.HalfCylinder;
            bool smooth = shape != Wg3Shape.Octagon;
            int sides = shape == Wg3Shape.Octagon ? 8 : (half ? 12 : 24);
            float sweep = half ? Mathf.PI : 2f * Mathf.PI;
            // El octógono gira medio lado para que sus caras planas queden sobre los ejes, que es
            // como lo estampa el servidor y como se lee un octógono en planta.
            float phase = shape == Wg3Shape.Octagon ? Mathf.PI / 8f : 0f;
            // El disco de la media luna está en el medio de la cara plana (z mínima de la caja).
            Vector3 disc = half ? new Vector3(0f, 0f, -size.z * 0.5f) : Vector3.zero;

            // Anillo, en orden de ángulo creciente. Tantos puntos como lados más uno para que la
            // media luna cierre en la cara plana; el cilindro repite el primero al final.
            int count = sides + 1;
            var rim = new Vector3[count];
            var rad = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                float t = phase + sweep * i / sides;
                rad[i] = new Vector3(Mathf.Cos(t), 0f, Mathf.Sin(t));
                rim[i] = disc + rad[i] * r;
            }

            // Cara curva: un quad por lado, (p0 abajo, p1 abajo, p1 arriba, p0 arriba) con `p1`
            // hacia ángulo creciente — el mismo orden y el mismo sentido que <see cref="AddFace"/>.
            float arc = 0f;
            float step = sweep * r / sides;
            for (int i = 0; i < sides; i++)
            {
                Vector3 p0 = rim[i], p1 = rim[i + 1];
                Vector3 n0, n1;
                if (smooth)
                {
                    n0 = rot * rad[i];
                    n1 = rot * rad[i + 1];
                }
                else
                {
                    n0 = n1 = rot * Vector3.Normalize(rad[i] + rad[i + 1]);
                }
                int b = verts.Count;
                verts.Add(centre + rot * (p0 + Vector3.down * hy));
                verts.Add(centre + rot * (p1 + Vector3.down * hy));
                verts.Add(centre + rot * (p1 + Vector3.up * hy));
                verts.Add(centre + rot * (p0 + Vector3.up * hy));
                normals.Add(n0); normals.Add(n1); normals.Add(n1); normals.Add(n0);
                uvs.Add(new Vector2(arc, 0f));
                uvs.Add(new Vector2(arc + step, 0f));
                uvs.Add(new Vector2(arc + step, h));
                uvs.Add(new Vector2(arc, h));
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
                arc += step;
            }

            // Tapas: abanico desde el centro del disco. Arriba (c, i+1, i) como el quad de
            // <see cref="AddFace"/> con normal `up`; abajo al revés.
            AddCap(verts, normals, uvs, tris, centre, rot, disc, rim, count, hy, true);
            AddCap(verts, normals, uvs, tris, centre, rot, disc, rim, count, -hy, false);

            // La media luna cierra por la cara plana, que es la cara `back` de su caja.
            if (half)
            {
                AddFace(verts, normals, uvs, tris, centre, rot, new Vector3(r, hy, size.z * 0.5f),
                    Vector3.back, Vector3.right, Vector3.up, size.x, h);
            }
        }

        /// <summary>Espejo de `ARCH_KEY_CM` del servidor: lo que queda de macizo sobre la clave.</summary>
        public const float ArchKeyM = 0.10f;

        /// <summary>
        /// ADR-125 enm. 1 — ARCO DE PUERTA: la banda sobre una boca con el intradós en media
        /// elipse, del arranque (cara inferior de la caja, en los dos extremos de la cuerda) a la
        /// clave (<see cref="ArchKeyM"/> bajo la cara superior). El eje largo de la caja es la
        /// cuerda; el corto, el grosor de pared. Intradós con normal suave; caras, remate y testeros
        /// planos. Es cóncavo: su collider es de malla NO convexa.
        /// </summary>
        private static void AddArch(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
            List<int> tris, Vector3 centre, Vector3 size)
        {
            bool alongX = size.x >= size.z;
            float r = (alongX ? size.x : size.z) * 0.5f;
            float t = (alongX ? size.z : size.x) * 0.5f;
            float hy = size.y * 0.5f;
            float rise = Mathf.Max(size.y - ArchKeyM, 0f);
            const int N = 16;

            // (u, y, w) locales → vector: `u` a lo largo de la cuerda, `w` a través de la pared.
            Vector3 L(float u, float y, float w) => alongX ? new Vector3(u, y, w) : new Vector3(w, y, u);

            var cu = new float[N + 1];
            var cy = new float[N + 1];
            var cn = new Vector3[N + 1];
            for (int i = 0; i <= N; i++)
            {
                // POR ÁNGULO, no por `u` uniforme. Con `u` uniforme el primer tramo junto a la
                // jamba era una cuerda de 15 cm de base y 19 de alto que se metía 5 cm en la luz
                // por dentro de la elipse: asomaba beige por dentro del marco (captura 15:25) y
                // el ráster, que estampa la elipse de verdad, dejaba pasar donde el dibujo no. Por
                // ángulo la flecha máxima de cada cuerda es de 2 mm, y la arquivolta —que muestrea
                // igual— queda a 1 cm por dentro en todo el recorrido.
                float a = Mathf.PI * i / N;
                float u = -r * Mathf.Cos(a);
                float y = -hy + rise * Mathf.Sin(a);
                cu[i] = u;
                cy[i] = y;
                // Normal del macizo en el intradós: hacia el centro de la elipse (abajo y adentro).
                Vector2 g = new Vector2(u / (r * r), rise > 0f ? (y + hy) / (rise * rise) : 0f);
                if (g.sqrMagnitude < 1e-8f) g = Vector2.up;
                g.Normalize();
                cn[i] = L(-g.x, -g.y, 0f).normalized;
            }

            // Intradós: un quad por tramo, normal suave por vértice.
            float arc = 0f;
            for (int i = 0; i < N; i++)
            {
                float seg = Vector2.Distance(new Vector2(cu[i], cy[i]), new Vector2(cu[i + 1], cy[i + 1]));
                Quad(verts, normals, uvs, tris,
                    centre + L(cu[i], cy[i], -t), centre + L(cu[i + 1], cy[i + 1], -t),
                    centre + L(cu[i + 1], cy[i + 1], t), centre + L(cu[i], cy[i], t),
                    cn[i], cn[i + 1], cn[i + 1], cn[i],
                    new Vector2(arc, 0f), new Vector2(arc + seg, 0f), new Vector2(arc + seg, 2f * t), new Vector2(arc, 2f * t));
                arc += seg;
            }
            // Las dos caras de pared: entre la curva y el remate.
            for (int side = -1; side <= 1; side += 2)
            {
                float w = side * t;
                Vector3 n = L(0f, 0f, side);
                for (int i = 0; i < N; i++)
                {
                    Quad(verts, normals, uvs, tris,
                        centre + L(cu[i], cy[i], w), centre + L(cu[i + 1], cy[i + 1], w),
                        centre + L(cu[i + 1], hy, w), centre + L(cu[i], hy, w),
                        n, n, n, n,
                        new Vector2(cu[i], cy[i]), new Vector2(cu[i + 1], cy[i + 1]),
                        new Vector2(cu[i + 1], hy), new Vector2(cu[i], hy));
                }
            }
            // Remate y testeros.
            Quad(verts, normals, uvs, tris,
                centre + L(-r, hy, -t), centre + L(r, hy, -t), centre + L(r, hy, t), centre + L(-r, hy, t),
                Vector3.up, Vector3.up, Vector3.up, Vector3.up,
                new Vector2(-r, -t), new Vector2(r, -t), new Vector2(r, t), new Vector2(-r, t));
            for (int side = -1; side <= 1; side += 2)
            {
                float u = side * r;
                Vector3 n = L(side, 0f, 0f);
                Quad(verts, normals, uvs, tris,
                    centre + L(u, -hy, -t), centre + L(u, -hy, t), centre + L(u, hy, t), centre + L(u, hy, -t),
                    n, n, n, n,
                    new Vector2(-t, -hy), new Vector2(t, -hy), new Vector2(t, hy), new Vector2(-t, hy));
            }
        }

        /// <summary>Espejo de `CASING_W_CM` del servidor: lo que la caja de la arquivolta excede al
        /// arco en cuerda y en flecha. Restándolo se recupera el intradós del arco.</summary>
        public const float ArchCasingWM = 0.09f;
        /// <summary>Espejo de `CASING_IN_CM`: cuánto queda la curva interior de la arquivolta por
        /// DENTRO del intradós del arco, medido por la normal. No es una elipse reducida: una elipse
        /// con la cuerda 1 cm más corta cae a cero antes que el arco y cerca del arranque el arco
        /// asomaba por dentro del anillo (captura 15:25).</summary>
        public const float ArchCasingInM = 0.01f;

        /// <summary>
        /// ADR-125 enm. 2 — la ARQUIVOLTA: el marco de una puerta en arco, con el mismo perfil de
        /// dos escalones que <see cref="AddCasingBox"/>. Cuatro curvas al mismo ángulo paramétrico
        /// (para que los quads no se crucen): la exterior es la caja del volumen; las dos siguientes
        /// son ésa empujada <see cref="CasingEdgeM"/> y <c>ArchCasingWM − CasingEdgeM</c> hacia
        /// dentro por su normal; la interior es el intradós del arco empujado
        /// <see cref="ArchCasingInM"/> hacia el hueco por la suya. Tres bandas: la central a fondo
        /// completo, las de borde hundidas <see cref="CasingStepM"/> por cara, con los escalones
        /// entre ellas. Extradós e intradós con normal suave, testeros en el arranque. Decoración:
        /// no frena.
        /// </summary>
        private static void AddArchCasing(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
            List<int> tris, Vector3 centre, Vector3 size)
        {
            bool alongX = size.x >= size.z;
            float ro = (alongX ? size.x : size.z) * 0.5f;
            float t = (alongX ? size.z : size.x) * 0.5f;
            float hy = size.y * 0.5f;
            float riseO = size.y;
            float ra = ro - ArchCasingWM;
            float riseA = Mathf.Max(riseO - ArchCasingWM, 0f);
            if (ra <= 0f) return;
            float tIn = Mathf.Max(t - CasingStepM, 0.001f);
            const int N = 16;
            Vector3 L(float u, float y, float w) => alongX ? new Vector3(u, y, w) : new Vector3(w, y, u);

            var c = new Vector2[4][];
            for (int k = 0; k < 4; k++) c[k] = new Vector2[N + 1];
            var no = new Vector3[N + 1];
            var ni = new Vector3[N + 1];
            for (int i = 0; i <= N; i++)
            {
                float a = Mathf.PI * i / N; // de −ro (a = π) a +ro (a = 0), pasando por la clave
                float cs = -Mathf.Cos(a), sn = Mathf.Sin(a);
                Vector2 po = new Vector2(ro * cs, -hy + riseO * sn);
                Vector2 go = new Vector2(cs / ro, riseO > 0f ? sn / riseO : 0f).normalized;
                Vector2 ga = new Vector2(cs / ra, riseA > 0f ? sn / riseA : 0f).normalized;
                Vector2 onArch = new Vector2(ra * cs, -hy + riseA * sn);
                c[0][i] = po;
                c[1][i] = po - go * CasingEdgeM;
                c[2][i] = po - go * (ArchCasingWM - CasingEdgeM);
                c[3][i] = onArch - ga * ArchCasingInM;
                no[i] = L(go.x, go.y, 0f).normalized;
                ni[i] = L(-ga.x, -ga.y, 0f).normalized;
            }
            float[] depth = { tIn, t, tIn };

            float arcI = 0f, arcO = 0f;
            for (int i = 0; i < N; i++)
            {
                float segI = Vector2.Distance(c[3][i], c[3][i + 1]);
                float segO = Vector2.Distance(c[0][i], c[0][i + 1]);
                // Intradós (curva interior), mirando al hueco, al fondo de la banda de borde.
                Quad(verts, normals, uvs, tris,
                    centre + L(c[3][i].x, c[3][i].y, -tIn), centre + L(c[3][i + 1].x, c[3][i + 1].y, -tIn),
                    centre + L(c[3][i + 1].x, c[3][i + 1].y, tIn), centre + L(c[3][i].x, c[3][i].y, tIn),
                    ni[i], ni[i + 1], ni[i + 1], ni[i],
                    new Vector2(arcI, 0f), new Vector2(arcI + segI, 0f), new Vector2(arcI + segI, 2f * tIn), new Vector2(arcI, 2f * tIn));
                // Extradós (curva exterior), mirando afuera.
                Quad(verts, normals, uvs, tris,
                    centre + L(c[0][i].x, c[0][i].y, -tIn), centre + L(c[0][i + 1].x, c[0][i + 1].y, -tIn),
                    centre + L(c[0][i + 1].x, c[0][i + 1].y, tIn), centre + L(c[0][i].x, c[0][i].y, tIn),
                    no[i], no[i + 1], no[i + 1], no[i],
                    new Vector2(arcO, 0f), new Vector2(arcO + segO, 0f), new Vector2(arcO + segO, 2f * tIn), new Vector2(arcO, 2f * tIn));
                for (int side = -1; side <= 1; side += 2)
                {
                    // Las tres bandas de cada cara de pared, cada una a su fondo.
                    for (int k = 0; k < 3; k++)
                    {
                        float w = side * depth[k];
                        Vector3 n = L(0f, 0f, side);
                        Quad(verts, normals, uvs, tris,
                            centre + L(c[k + 1][i].x, c[k + 1][i].y, w), centre + L(c[k + 1][i + 1].x, c[k + 1][i + 1].y, w),
                            centre + L(c[k][i + 1].x, c[k][i + 1].y, w), centre + L(c[k][i].x, c[k][i].y, w),
                            n, n, n, n,
                            c[k + 1][i], c[k + 1][i + 1], c[k][i + 1], c[k][i]);
                    }
                    // Los dos escalones: el de fuera mira al extradós, el de dentro al hueco.
                    for (int k = 1; k <= 2; k++)
                    {
                        float sgn = k == 1 ? 1f : -1f;
                        Quad(verts, normals, uvs, tris,
                            centre + L(c[k][i].x, c[k][i].y, side * tIn), centre + L(c[k][i + 1].x, c[k][i + 1].y, side * tIn),
                            centre + L(c[k][i + 1].x, c[k][i + 1].y, side * t), centre + L(c[k][i].x, c[k][i].y, side * t),
                            sgn * no[i], sgn * no[i + 1], sgn * no[i + 1], sgn * no[i],
                            new Vector2(arcO, tIn), new Vector2(arcO + segO, tIn), new Vector2(arcO + segO, t), new Vector2(arcO, t));
                    }
                }
                arcI += segI;
                arcO += segO;
            }
            // Testeros en el arranque: una tira por banda, a −hy, cada una a su fondo.
            for (int end = 0; end <= N; end += N)
            {
                for (int k = 0; k < 3; k++)
                {
                    float uo = c[k][end].x, ui = c[k + 1][end].x, d = depth[k];
                    Vector3 n = Vector3.down;
                    Quad(verts, normals, uvs, tris,
                        centre + L(ui, -hy, -d), centre + L(uo, -hy, -d),
                        centre + L(uo, -hy, d), centre + L(ui, -hy, d),
                        n, n, n, n,
                        new Vector2(ui, -d), new Vector2(uo, -d), new Vector2(uo, d), new Vector2(ui, d));
                }
            }
        }

        /// <summary>Un quad con la normal que se le pide: el orden de los triángulos se elige para
        /// que la cara mire hacia `n0` (Unity: la normal geométrica de (a, b, c) es
        /// <c>cross(b − a, c − a)</c>), así ningún llamador tiene que acertar el sentido a mano.</summary>
        private static void Quad(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris,
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
            Vector3 n0, Vector3 n1, Vector3 n2, Vector3 n3,
            Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3)
        {
            int b = verts.Count;
            verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
            normals.Add(n0); normals.Add(n1); normals.Add(n2); normals.Add(n3);
            uvs.Add(t0); uvs.Add(t1); uvs.Add(t2); uvs.Add(t3);
            Vector3 geo = Vector3.Cross(p2 - p0, p1 - p0);
            if (Vector3.Dot(geo, n0 + n1 + n2 + n3) >= 0f)
            {
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }
            else
            {
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
        }

        private static void AddCap(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
            List<int> tris, Vector3 centre, Quaternion rot, Vector3 disc, Vector3[] rim, int count,
            float y, bool top)
        {
            Vector3 n = rot * (top ? Vector3.up : Vector3.down);
            Vector3 lift = Vector3.up * y;
            int b = verts.Count;
            verts.Add(centre + rot * (disc + lift));
            normals.Add(n);
            uvs.Add(new Vector2(disc.x, disc.z));
            for (int i = 0; i < count; i++)
            {
                verts.Add(centre + rot * (rim[i] + lift));
                normals.Add(n);
                uvs.Add(new Vector2(rim[i].x, rim[i].z));
            }
            for (int i = 0; i < count - 1; i++)
            {
                int a = b + 1 + i, c = b + 2 + i;
                tris.Add(b);
                tris.Add(top ? c : a);
                tris.Add(top ? a : c);
            }
        }

        private static void AddFace(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
            List<int> tris, Vector3 centre, Quaternion rot, Vector3 half,
            Vector3 n, Vector3 u, Vector3 v, float uLen, float vLen)
        {
            Vector3 faceCentre = Vector3.Scale(n, half);
            Vector3 du = Vector3.Scale(u, half);
            Vector3 dv = Vector3.Scale(v, half);

            // Sobre la cara, `u` y `v` son ejes perpendiculares a la normal, así que Scale con el
            // medio-tamaño da exactamente el medio-lado en cada dirección.
            int b = verts.Count;
            verts.Add(centre + rot * (faceCentre - du - dv));
            verts.Add(centre + rot * (faceCentre + du - dv));
            verts.Add(centre + rot * (faceCentre + du + dv));
            verts.Add(centre + rot * (faceCentre - du + dv));

            Vector3 worldNormal = rot * n;
            for (int i = 0; i < 4; i++) normals.Add(worldNormal);

            // UV en METROS: la textura repite cada metro sin importar el tamaño de la cara, así que
            // una pared de 26 m y una de 2 m tienen el mismo grano. Escalarla al 0..1 de la cara
            // haría que el gotelé de un pasillo largo se viera estirado junto al de una sala.
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(uLen, 0f));
            uvs.Add(new Vector2(uLen, vLen));
            uvs.Add(new Vector2(0f, vLen));

            tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
        }
    }
}
