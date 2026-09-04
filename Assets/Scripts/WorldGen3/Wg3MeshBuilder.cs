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
                if (v.shape == Wg3Shape.Box)
                    AddBox(verts, normals, uvs, tris[SubMeshFor(v.kind)],
                        v.center - origin, v.size, v.yawDegrees);
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
