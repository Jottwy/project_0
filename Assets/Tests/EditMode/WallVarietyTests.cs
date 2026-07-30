using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Medias paredes (variedad de panel de pared). Variante puramente CLIENTE, elegida
    /// por hash determinista de tile — el wire no cambia: los bits 0x0F de
    /// <c>walls[tx,tz]</c> siguen diciendo "aquí hay pared", solo cambia con qué forma la
    /// dibuja el cliente.
    ///
    /// El panel de pared es la ÚNICA superficie de grid_gen que lleva collider Unity en
    /// runtime (<c>GridChunkBuilder.AddColliderIfMissing</c>; pilares y techo NO), así que
    /// estos tests no miran solo el visual: comprueban que el collider acompaña a la
    /// escala (ni barrera invisible a 4 m, ni hueco nuevo) y que la altura nunca cae por
    /// debajo del umbral que la haría atravesable (jump 1.05 / step 0.275 de
    /// FPS_Player.prefab).
    /// </summary>
    [TestFixture]
    public class WallVarietyTests
    {
        private const int Tiles = GridChunkDataMsg.Tiles;

        // Convención de bits del backend (grid_gen tile_walls.rs) que BuildFromWalls
        // consume: 2 = S (+Z) → panel +z del tile, 4 = E (+X) → panel +x. Son los dos
        // únicos que el render emite (regla de no-duplicación).
        private const byte BitS = 2;
        private const byte BitE = 4;

        private GameObject _root;
        private LayerVisualConfig _cfg;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_cfg != null) Object.DestroyImmediate(_cfg);
            _root = null;
            _cfg = null;
        }

        private static byte[,] EmptyWalls() => new byte[Tiles, Tiles];

        /// <summary>
        /// Config mínima para la ruta "styled". Materiales a null a propósito: <c>Paint</c>
        /// los ignora si son null, así que el test no depende de Shader.Find ni deja
        /// materiales huérfanos.
        /// </summary>
        private LayerVisualConfig StyledConfig(float variety, float kneeHeight)
        {
            _cfg = ScriptableObject.CreateInstance<LayerVisualConfig>();
            _cfg.showCeiling = false;      // menos hijos que filtrar; no afecta a las paredes
            _cfg.ceilingPipes = false;
            _cfg.props = null;
            _cfg.wallPanelVariety = variety;
            _cfg.kneeWallHeight = kneeHeight;
            return _cfg;
        }

        private static LayerVisualMaterials NullMaterials() => new LayerVisualMaterials();

        /// <summary>layerIndex 0 de 2 ⇒ no es la capa superior ⇒ sin losa de techo extra.</summary>
        private GameObject Build(byte[,] walls, LayerVisualConfig cfg, string name) =>
            GridChunkBuilder.BuildFromWalls(walls, GridPrefabSet.LoadFromResources(),
                Vector3.zero, name, 0, 2, cfg, NullMaterials());

        private static List<Transform> WallInstances(GameObject root)
        {
            var list = new List<Transform>();
            foreach (Transform c in root.transform)
                if (c.name.StartsWith("Wall")) list.Add(c);
            return list;
        }

        /// <summary>AABB en mundo del collider del panel (el BoxCollider vive en el hijo
        /// con MeshRenderer, así que hereda la escala del root del panel).</summary>
        private static Bounds ColliderBounds(Transform wall)
        {
            var col = wall.GetComponentInChildren<Collider>();
            Assert.IsNotNull(col, "todo panel de pared debe llevar collider en runtime");
            return col.bounds;
        }

        // ── 1. Regresión cero ───────────────────────────────────────────────────

        /// <summary>
        /// <c>wallPanelVariety = 0</c> ⇒ el mundo se dibuja exactamente como antes de que
        /// el campo existiera: un panel por bit, todos a escala 1 y apoyados en el suelo.
        /// Es la garantía de que layers 1-3 (que dejan el campo a 0) quedan intactos.
        /// </summary>
        [Test]
        public void ZeroVarietyKeepsEveryPanelFullHeight()
        {
            var walls = EmptyWalls();
            walls[3, 3] = BitS;
            walls[4, 4] = BitE;
            walls[5, 5] = (byte)(BitS | BitE);

            _root = Build(walls, StyledConfig(0f, 1.35f), "ChunkZeroVariety");

            var panels = WallInstances(_root);
            Assert.AreEqual(4, panels.Count,
                "4 bits puestos ⇒ 4 paneles (2 en el tile con ambos bits)");
            foreach (var p in panels)
            {
                Assert.AreEqual(Vector3.one, p.localScale,
                    $"con variety 0 ningún panel debe reescalarse ({p.name})");
                Assert.AreEqual(0f, p.localPosition.y, 1e-4f,
                    "con variety 0 ningún panel debe desplazarse en Y");
            }
        }

        // ── 2. Knee wall: escala y collider coherentes ──────────────────────────

        /// <summary>
        /// <c>variety = 1</c> ⇒ todos los paneles son knee wall, así que el resultado no
        /// depende del hash. Comprueba las dos mitades del contrato: el visual mide
        /// <c>kneeWallHeight</c> Y el collider mide lo mismo — si el collider se quedara a
        /// 4 m tendríamos una barrera invisible, que es justo el fallo que este test existe
        /// para descartar.
        /// </summary>
        [Test]
        public void KneeWallScalesVisualAndColliderTogether()
        {
            const float h = 1.35f;
            var walls = EmptyWalls();
            walls[3, 3] = BitS;
            walls[6, 2] = BitE; // panel rotado 90°: el AABB del collider no debe cambiar de alto

            _root = Build(walls, StyledConfig(1f, h), "ChunkAllKnee");

            var panels = WallInstances(_root);
            Assert.AreEqual(2, panels.Count);
            foreach (var p in panels)
            {
                Assert.AreEqual(h / 4f, p.localScale.y, 1e-4f,
                    "el prefab de pared está autorado a 4 m ⇒ escala = altura/4");
                Assert.AreEqual(1f, p.localScale.x, 1e-4f, "solo se escala en Y");
                Assert.AreEqual(1f, p.localScale.z, 1e-4f, "solo se escala en Y");

                var b = ColliderBounds(p);
                Assert.AreEqual(0f, b.min.y, 1e-3f, "la knee wall nace en el suelo");
                Assert.AreEqual(h, b.max.y, 1e-3f,
                    "el collider debe medir lo mismo que el visual — sin barrera invisible a 4 m");
            }
        }

        // ── 3. Clamp de altura mínima ───────────────────────────────────────────

        /// <summary>
        /// Una altura autorada por debajo del umbral se sube a
        /// <see cref="LayerVisualConfig.MinKneeWallHeight"/>. No es cosmética: con
        /// jumpHeight 1.05 y stepOffset 0.275, un panel más bajo sería atravesable y el
        /// jugador cruzaría una arista que el backend marca sólida.
        /// </summary>
        [Test]
        public void ExtremeLowHeightIsClampedToUntraversableFloor()
        {
            var cfg = StyledConfig(1f, 0.5f);
            Assert.AreEqual(LayerVisualConfig.MinKneeWallHeight, cfg.KneeWallHeightClamped, 1e-4f,
                "0.5 m debe subirse al mínimo no atravesable");

            var walls = EmptyWalls();
            walls[3, 3] = BitS;

            _root = Build(walls, cfg, "ChunkClampedKnee");

            var panels = WallInstances(_root);
            Assert.AreEqual(1, panels.Count);
            var b = ColliderBounds(panels[0]);
            Assert.GreaterOrEqual(b.max.y, LayerVisualConfig.MinKneeWallHeight - 1e-3f,
                "la geometría construida tampoco puede quedar por debajo del mínimo");
        }
    }
}
