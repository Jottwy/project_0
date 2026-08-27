using System.Collections.Generic;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La pieza autorada REAL, no una sintética de test.
    ///
    /// Los demás ficheros comprueban el horno con modelos escritos dentro del propio test, que
    /// siempre son más simples de lo que alguien dibuja: rectángulos limpios, una boca, sin
    /// salientes. Esta clase mira <c>cor_alcove</c> tal y como salió del horno —contorno con
    /// entrante, dos bocas en lados opuestos, una columna girada 12°— porque es donde aparecen los
    /// fallos que un rectángulo no puede producir.
    ///
    /// Si alguien borra o rehornea la pieza, estos tests fallan. Es lo que se quiere: son la única
    /// atadura entre el contenido y el código que lo interpreta.
    /// </summary>
    public sealed class Wg3BakedPieceTests
    {
        private const string PiecePath = "Assets/WorldGen3/Pieces/cor_alcove.asset";

        private static Wg3PieceAsset Load()
        {
            var asset = AssetDatabase.LoadAssetAtPath<Wg3PieceAsset>(PiecePath);
            Assert.IsNotNull(asset, $"no está {PiecePath}. Se regenera con " +
                                    "Backrooms/WorldGen3/Crear la pieza autorada de prueba");
            return asset;
        }

        private static Vector2 FootprintExtent(in Wg3Volume v)
        {
            float rad = v.yawDegrees * Mathf.Deg2Rad;
            float c = Mathf.Abs(Mathf.Cos(rad)), s = Mathf.Abs(Mathf.Sin(rad));
            return new Vector2(v.size.x * c + v.size.z * s, v.size.x * s + v.size.z * c);
        }

        [Test]
        public void TheBakedPiecePassesTheSameValidatorAsTheCodeCatalogue()
        {
            List<string> issues = Wg3Validator.ValidatePiece(Load().ToPiece());
            Assert.IsEmpty(issues, string.Join("\n", issues));
        }

        [Test]
        public void EveryBoxStaysInsideTheDeclaredFootprint()
        {
            // Si la pieza se sale de su huella, el compositor decide sin solape y el mundo se solapa
            // igual: la ocupación se comprueba contra la huella declarada, no contra la real.
            Wg3PieceAsset asset = Load();
            foreach (Wg3Volume v in asset.volumes)
            {
                Vector2 ext = FootprintExtent(v);
                Assert.GreaterOrEqual(v.center.x - ext.x * 0.5f, -1e-3f, "se sale por −X");
                Assert.GreaterOrEqual(v.center.z - ext.y * 0.5f, -1e-3f, "se sale por −Z");
                Assert.LessOrEqual(v.center.x + ext.x * 0.5f, asset.sizeX + 1e-3f, "se sale por +X");
                Assert.LessOrEqual(v.center.z + ext.y * 0.5f, asset.sizeZ + 1e-3f, "se sale por +Z");
            }
        }

        [Test]
        public void BothDoorwaysAreOpenInTheChuleta()
        {
            // El fallo que esto caza es el peor de todos y el más fácil de no ver: una boca correcta
            // en los números y tapiada en las cajas. El compositor engancha ahí un pasillo, el
            // cliente dibuja una puerta, y el jugador se choca contra ella.
            Wg3PieceAsset asset = Load();
            Assert.AreEqual(2, asset.sockets.Length);

            foreach (Wg3Socket s in asset.sockets)
            {
                Vector2 mouth = Wg3Piece.LocalPoint(s.side, s.offset, asset.sizeX, asset.sizeZ);

                // Dentro del grosor de la pared, no en el plano de la huella: en el borde la sonda
                // queda fuera de la caja por medio grosor y pasaría con el vano tapiado.
                Vector2 inward = -Wg3Piece.OutwardNormal(s.side) * 0.08f;
                var probe = new Vector3(mouth.x + inward.x, 1.6f, mouth.y + inward.y);

                foreach (Wg3Volume v in asset.volumes)
                {
                    if (!v.IsSolid) continue;

                    float rad = -v.yawDegrees * Mathf.Deg2Rad;
                    float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                    var d = new Vector2(probe.x - v.center.x, probe.z - v.center.z);
                    var local = new Vector2(d.x * cos - d.y * sin, d.x * sin + d.y * cos);

                    bool inside = Mathf.Abs(local.x) < v.size.x * 0.5f - 0.01f
                               && Mathf.Abs(probe.y - v.center.y) < v.size.y * 0.5f - 0.01f
                               && Mathf.Abs(local.y) < v.size.z * 0.5f - 0.01f;

                    Assert.IsFalse(inside, $"la boca del lado {s.side} está tapiada por una caja " +
                                           $"{v.kind} en {v.center} de {v.size}");
                }
            }
        }

        [Test]
        public void TheAuthoredMeshCoversTheSameFootprintAsTheChuleta()
        {
            // La comprobación de la rebanada 2 sobre datos REALES: la malla del prefab, desplazada
            // por el pivote que escribió el horno, tiene que caer dentro de la huella que el
            // servidor va a colisionar. Una malla más grande que su chuleta se ve como una pared
            // sólida que se atraviesa; una más pequeña, como chocar contra el aire.
            Wg3PieceAsset asset = Load();
            Assert.IsNotNull(asset.visualPrefab, "la pieza se horneó sin malla");

            var filter = asset.visualPrefab.GetComponent<MeshFilter>();
            Assert.IsNotNull(filter, "el prefab no lleva MeshFilter");
            Assert.IsNotNull(filter.sharedMesh, "el MeshFilter no apunta a ninguna malla");

            Bounds b = filter.sharedMesh.bounds;
            Vector3 min = b.min + new Vector3(asset.visualPivot.x, 0f, asset.visualPivot.y);
            Vector3 max = b.max + new Vector3(asset.visualPivot.x, 0f, asset.visualPivot.y);

            // Tolerancia de 2 cm: el rodapié y las molduras sobresalen unos milímetros de la cara de
            // la pared a propósito, y eso NO debe costar colisión (R25).
            const float Slack = 0.02f;
            Assert.GreaterOrEqual(min.x, -Slack, "la malla sobresale de la huella por −X");
            Assert.GreaterOrEqual(min.z, -Slack, "la malla sobresale de la huella por −Z");
            Assert.LessOrEqual(max.x, asset.sizeX + Slack, "la malla sobresale de la huella por +X");
            Assert.LessOrEqual(max.z, asset.sizeZ + Slack, "la malla sobresale de la huella por +Z");
        }
    }
}
