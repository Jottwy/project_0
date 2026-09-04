using BackroomsSurvival.Net;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// ADR-105 D4 — el macizo que llega por el cable se monta DONDE dice el cable.
    ///
    /// Existe porque no existía: desde wire 50 el centro del volumen se daba en coordenadas locales
    /// y <c>Wg3MeshBuilder</c> le restaba el origen otra vez, así que todos los macizos del mundo se
    /// dibujaban y colisionaban apilados en (0, 0). Ningún test lo miraba: los de tramos y piezas
    /// prueban álgebra de volúmenes, no dónde acaba la malla. Este mide la malla y el collider en
    /// coordenadas de MUNDO, que es lo único que el jugador ve y contra lo que choca.
    /// </summary>
    public class Wg3SolidAssemblyTests
    {
        private static Wg3SolidMsg Solid() => new Wg3SolidMsg
        {
            xCm = 900,
            zCm = -60,
            sizeXCm = 150,
            sizeZCm = 20,
            bottomYCm = 332,
            topYCm = 442,
            style = 3,
        };

        [Test]
        public void TheMeshAndTheColliderLandWhereTheWireSays()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new System.Collections.Generic.List<Mesh>();
                go = Wg3SceneAssembler.AssembleSolid(Solid(), parent.transform, null, created, "solid");
                Assert.IsNotNull(go, "el macizo válido tiene que montarse");

                // Centro esperado en metros: esquina mínima + medio tamaño.
                var expected = new Vector3(9f + 0.75f, 3.32f + 0.55f, -0.6f + 0.1f);

                Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                Vector3 meshCentre = go.transform.TransformPoint(mesh.bounds.center);
                Assert.Less((meshCentre - expected).magnitude, 0.001f,
                    $"la malla está centrada en {meshCentre}, no en {expected}: el macizo se dibuja en otro sitio");
                Assert.Less((mesh.bounds.size - new Vector3(1.5f, 1.1f, 0.2f)).magnitude, 0.001f,
                    "la malla no mide lo que el cable dice");

                var box = go.GetComponent<BoxCollider>();
                Assert.IsNotNull(box, "un macizo frena: lleva collider");
                Vector3 boxCentre = go.transform.TransformPoint(box.center);
                Assert.Less((boxCentre - expected).magnitude, 0.001f,
                    $"el collider está centrado en {boxCentre}, no en {expected}: colisión y dibujo separados");

                foreach (Mesh m in created) Object.DestroyImmediate(m);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>ADR-125 — un cilindro se monta con malla convexa, del tamaño de su huella, y
        /// la esquina de la caja queda FUERA de la malla (que es lo que el ráster del servidor
        /// también deja libre: `round_solids_stamp_as_discs_not_boxes`).</summary>
        [Test]
        public void ACylinderIsAConvexMeshInscribedInItsFootprint()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new System.Collections.Generic.List<Mesh>();
                var msg = new Wg3SolidMsg
                {
                    xCm = 800, zCm = 800, sizeXCm = 400, sizeZCm = 400,
                    bottomYCm = 0, topYCm = 300, style = 3, yawDeg = 0, shape = Wg3Shape.Cylinder,
                };
                go = Wg3SceneAssembler.AssembleSolid(msg, parent.transform, null, created, "cyl");
                Assert.IsNotNull(go);

                Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                Vector3 centre = go.transform.TransformPoint(mesh.bounds.center);
                Assert.Less((centre - new Vector3(10f, 1.5f, 10f)).magnitude, 0.001f, $"centro en {centre}");
                Assert.Less((mesh.bounds.size - new Vector3(4f, 3f, 4f)).magnitude, 0.01f,
                    $"la envolvente del cilindro mide {mesh.bounds.size}, no 4×3×4");

                // Ningún vértice a más del radio del eje: la esquina de la caja no existe.
                Vector3[] verts = mesh.vertices;
                float maxR = 0f;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = go.transform.TransformPoint(verts[i]);
                    maxR = Mathf.Max(maxR, new Vector2(w.x - 10f, w.z - 10f).magnitude);
                }
                Assert.Less(maxR, 2.001f, $"un vértice a {maxR} m del eje: eso es una caja, no un cilindro");

                Assert.IsNull(go.GetComponent<BoxCollider>(), "un prisma no lleva BoxCollider");
                var mc = go.GetComponent<MeshCollider>();
                Assert.IsNotNull(mc, "un prisma frena con su malla");
                Assert.IsTrue(mc.convex, "y la malla tiene que ser convexa para que un CharacterController choque");

                foreach (Mesh m in created) Object.DestroyImmediate(m);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>ADR-125 — la media luna girada 90° apoya la cara plana en x mínima y la panza
        /// mira a +x: es el giro que `wall_pilasters` usa contra la pared de x mínima, y si la
        /// convención de giro de los dos lados no casa, la panza queda dentro del muro.</summary>
        [Test]
        public void AHalfMoonTurnedNinetyBulgesTowardsPlusX()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new System.Collections.Generic.List<Mesh>();
                var msg = new Wg3SolidMsg
                {
                    xCm = 900, zCm = 1000, sizeXCm = 200, sizeZCm = 100,
                    bottomYCm = 0, topYCm = 300, style = 3, yawDeg = 90, shape = Wg3Shape.HalfCylinder,
                };
                go = Wg3SceneAssembler.AssembleSolid(msg, parent.transform, null, created, "half");
                Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                Bounds b = mesh.bounds;
                Vector3 min = go.transform.TransformPoint(b.min);
                Vector3 max = go.transform.TransformPoint(b.max);
                // Envolvente girada: x 9,5..10,5 (cara plana en 9,5), z 9,5..11,5.
                Assert.AreEqual(9.5f, min.x, 0.01f, "la cara plana no está en x = 9,5");
                Assert.AreEqual(10.5f, max.x, 0.01f, "la panza no llega a x = 10,5");
                Assert.AreEqual(9.5f, min.z, 0.01f);
                Assert.AreEqual(11.5f, max.z, 0.01f);
                foreach (Mesh m in created) Object.DestroyImmediate(m);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>ADR-125 enm. 1 — el arco de puerta ocupa su banda entera en envolvente, deja la
        /// luz libre bajo la clave (ningún vértice del intradós por debajo del arranque, y el punto
        /// medio de la cuerda sube hasta la clave) y frena con malla NO convexa.</summary>
        [Test]
        public void ADoorArchLeavesTheOpeningFreeUnderItsKey()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new System.Collections.Generic.List<Mesh>();
                var msg = new Wg3SolidMsg
                {
                    xCm = 940, zCm = 1000, sizeXCm = 120, sizeZCm = 15,
                    bottomYCm = 190, topYCm = 240, style = 3, yawDeg = 0, shape = Wg3Shape.Arch,
                };
                go = Wg3SceneAssembler.AssembleSolid(msg, parent.transform, null, created, "arch");
                Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                Assert.Less((mesh.bounds.size - new Vector3(1.2f, 0.5f, 0.15f)).magnitude, 0.01f,
                    $"la envolvente del arco mide {mesh.bounds.size}, no 1,2×0,5×0,15");

                // El intradós en el centro de la cuerda está a la altura de la clave (2,30), no del
                // arranque: la puerta queda abierta bajo él.
                Vector3[] verts = mesh.vertices;
                float lowestAtCentre = float.MaxValue, lowestAnywhere = float.MaxValue;
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 w = go.transform.TransformPoint(verts[i]);
                    lowestAnywhere = Mathf.Min(lowestAnywhere, w.y);
                    if (Mathf.Abs(w.x - 10f) < 0.01f) lowestAtCentre = Mathf.Min(lowestAtCentre, w.y);
                }
                Assert.AreEqual(1.9f, lowestAnywhere, 0.01f, "el arco baja por debajo del arranque");
                Assert.AreEqual(2.3f, lowestAtCentre, 0.01f, "la clave no está a 2,30 en el centro de la cuerda");

                var mc = go.GetComponent<MeshCollider>();
                Assert.IsNotNull(mc);
                Assert.IsFalse(mc.convex, "un arco es cóncavo: con casco convexo taparía la puerta");
                foreach (Mesh m in created) Object.DestroyImmediate(m);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>Wire 56 — las dos claves nuevas se leen por nombre; si el parser las saltara,
        /// todo pilar redondo llegaría como caja y sin giro, sin un solo error.</summary>
        [Test]
        public void YawAndShapeAreParsedFromTheWire()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(9);
            w.WriteString("x_cm"); w.WriteInt(900);
            w.WriteString("z_cm"); w.WriteInt(-60);
            w.WriteString("size_x_cm"); w.WriteInt(150);
            w.WriteString("size_z_cm"); w.WriteInt(20);
            w.WriteString("bottom_y_cm"); w.WriteInt(332);
            w.WriteString("top_y_cm"); w.WriteInt(442);
            w.WriteString("style"); w.WriteInt(3);
            w.WriteString("yaw_deg"); w.WriteInt(270);
            w.WriteString("shape"); w.WriteInt(2);
            var r = new MsgPackReader(w.ToArray());
            Wg3SolidMsg s = Wg3SolidMsg.Parse(r);
            Assert.AreEqual(900, s.xCm);
            Assert.AreEqual(270, s.yawDeg);
            Assert.AreEqual(2, s.shape);
        }
    }
}
