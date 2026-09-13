using System;
using System.Collections.Generic;
using System.IO;
using BackroomsSurvival.Net;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-122 — la rampa del cliente frente a la del servidor.
    ///
    /// Lo único que puede cazar una deriva entre los dos idiomas es un oráculo común: lo escribe Rust
    /// (<c>the_ramp_oracle_is_current</c>, desde <c>ramp::ramp_step_boxes</c>) y aquí se comprueba caja
    /// a caja, al centímetro. Si difieren, el jugador flota o tropieza donde la criatura no, y nada
    /// revienta. Además, la cuña se dibuja hacia fuera y en su sitio.
    /// </summary>
    [TestFixture]
    public class Wg3RampGeometryTests
    {
        [Serializable]
        private sealed class OracleRamp
        {
            public int x_cm, z_cm, size_x_cm, size_z_cm, bottom_y_cm, top_y_cm, dir, style;
        }

        [Serializable]
        private sealed class OracleBox
        {
            public int x_cm, z_cm, size_x_cm, size_z_cm, bottom_y_cm, top_y_cm;
        }

        [Serializable]
        private sealed class OracleCase
        {
            public OracleRamp ramp;
            public OracleBox[] boxes;
        }

        [Serializable]
        private sealed class Oracle
        {
            public OracleCase[] cases;
        }

        private static string OraclePath => Path.GetFullPath(Path.Combine(Application.dataPath, "..",
            "backend", "tests", "fixtures", "wg3_ramp_oracle.json"));

        private static Wg3RampMsg ToMsg(OracleRamp r) => new Wg3RampMsg
        {
            xCm = r.x_cm, zCm = r.z_cm, sizeXCm = r.size_x_cm, sizeZCm = r.size_z_cm,
            bottomYCm = r.bottom_y_cm, topYCm = r.top_y_cm, dir = (byte)r.dir, style = (byte)r.style,
        };

        [Test]
        public void StepBoxesMatchTheRustOracle()
        {
            Assert.IsTrue(File.Exists(OraclePath),
                $"sin oráculo en {OraclePath}: lo escribe `cargo test the_ramp_oracle_is_current` con WG3_WRITE_RAMP_ORACLE=1");
            var oracle = JsonUtility.FromJson<Oracle>(File.ReadAllText(OraclePath));
            Assert.IsNotNull(oracle?.cases);
            Assert.Greater(oracle.cases.Length, 0);

            int compared = 0;
            foreach (OracleCase c in oracle.cases)
            {
                Wg3RampMsg msg = ToMsg(c.ramp);
                List<Wg3RampGeometry.StepBox> ours = Wg3RampGeometry.StepBoxes(msg);
                Assert.AreEqual(c.boxes.Length, ours.Count, $"dir {c.ramp.dir}: número de cajas");
                for (int i = 0; i < ours.Count; i++)
                {
                    OracleBox o = c.boxes[i];
                    Wg3RampGeometry.StepBox b = ours[i];
                    string at = $"dir {c.ramp.dir} caja {i}";
                    Assert.AreEqual(o.x_cm, b.xCm, at + " x");
                    Assert.AreEqual(o.z_cm, b.zCm, at + " z");
                    Assert.AreEqual(o.size_x_cm, b.sizeXCm, at + " size_x");
                    Assert.AreEqual(o.size_z_cm, b.sizeZCm, at + " size_z");
                    Assert.AreEqual(o.bottom_y_cm, b.bottomYCm, at + " bottom");
                    Assert.AreEqual(o.top_y_cm, b.topYCm, at + " top");
                    compared++;
                }
            }
            Assert.Greater(compared, 0);
        }

        private static Wg3RampMsg Sample(byte dir) => new Wg3RampMsg
        {
            xCm = 1000, zCm = -450,
            sizeXCm = dir % 2 == 0 ? 300 : 320, sizeZCm = dir % 2 == 0 ? 320 : 300,
            bottomYCm = -60, topYCm = 0, dir = dir, style = 2,
        };

        /// <summary>La cuña ocupa exactamente la huella (más el realce de 1 cm), sube hacia
        /// <c>dir</c> y todas sus caras miran hacia fuera.</summary>
        [Test]
        public void TheWedgeFillsTheFootprintAndFacesOutward([Values(0, 1, 2, 3)] int dir)
        {
            Wg3RampMsg r = Sample((byte)dir);
            var origin = new Vector3(r.xCm / 100f, r.bottomYCm / 100f, r.zCm / 100f);
            Mesh mesh = Wg3MeshBuilder.Build(new List<Wg3Volume> { Wg3RampGeometry.WedgeVolume(r) }, origin);
            try
            {
                Vector3[] v = mesh.vertices;
                Vector3[] n = mesh.normals;
                Assert.AreEqual(14, v.Length, "rampa 4 + testa 4 + dos costados 3");

                Bounds b = mesh.bounds;
                Assert.That(b.size.x, Is.EqualTo(r.sizeXCm / 100f).Within(1e-3f), "ancho en X");
                Assert.That(b.size.z, Is.EqualTo(r.sizeZCm / 100f).Within(1e-3f), "largo en Z");
                Assert.That(b.min.y, Is.EqualTo(Wg3RampGeometry.WedgeLiftM).Within(1e-3f), "base 1 cm sobre el fondo");
                Assert.That(b.max.y, Is.EqualTo(0.60f + Wg3RampGeometry.WedgeLiftM).Within(1e-3f), "arriba");

                // Normal de la rampa: hacia arriba y hacia el lado BAJO (contraria a dir).
                Vector3 up = n[0];
                Assert.Greater(up.y, 0.9f, "la rampa mira hacia arriba");
                Vector3 toward = dir switch
                {
                    0 => Vector3.forward,
                    1 => Vector3.right,
                    2 => Vector3.back,
                    _ => Vector3.left,
                };
                Assert.Less(Vector3.Dot(up, toward), 0f, "la rampa se inclina hacia su lado bajo");

                // Cada triángulo, con el orden de Unity, tiene su normal geométrica del lado de la
                // normal de sus vértices: si no, se descarta por la cara de atrás y no se ve.
                int[] tris = mesh.GetTriangles(Wg3MeshBuilder.SubMesh.Floor);
                Assert.Greater(tris.Length, 0, "la cuña va en la submalla de suelo");
                Vector3 centre = b.center;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 a = v[tris[t]], c1 = v[tris[t + 1]], c2 = v[tris[t + 2]];
                    Vector3 geo = Vector3.Cross(c1 - a, c2 - a);
                    Assert.Greater(Vector3.Dot(geo, n[tris[t]]), 0f, $"triángulo {t / 3} vuelto hacia dentro");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>Las cajas de colisión son peldaños sólidos y no se dibujan: el dibujo es la cuña.</summary>
        [Test]
        public void StepVolumesAreSolidSteps()
        {
            List<Wg3Volume> steps = Wg3RampGeometry.StepVolumes(Sample(0));
            Assert.AreEqual(7, steps.Count);
            foreach (Wg3Volume s in steps)
            {
                Assert.AreEqual(Wg3VolumeKind.Step, s.kind);
                Assert.AreEqual(Wg3Shape.Box, s.shape);
                Assert.IsTrue(s.IsSolid);
            }
            Assert.AreNotEqual(Wg3Shape.Box, Wg3RampGeometry.WedgeVolume(Sample(0)).shape,
                "la cuña no puede ser caja: AddColliders la convertiría en un bloque");
        }
    }
}
