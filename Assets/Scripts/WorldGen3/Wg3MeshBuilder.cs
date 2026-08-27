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
                AddBox(verts, normals, uvs, tris[SubMeshFor(v.kind)],
                    v.center - origin, v.size, v.yawDegrees);
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
