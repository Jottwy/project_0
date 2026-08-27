#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// LA PRIMERA PIEZA AUTORADA DE VERDAD, y el recorrido completo que la produce: modelo → malla,
    /// modelo → chuleta, las dos del MISMO modelo (R2).
    ///
    /// El modelo se escribe aquí en código y no se dibuja con el ratón, y eso NO lo hace procedural:
    /// es un modelo hecho a mano, con sus medidas puestas a mano, que se puede abrir en
    /// <c>Backrooms/Room Authoring Tool</c> y seguir tocando — para eso el asset guarda
    /// <c>sourceDefinition</c>. Escribirlo en código es lo que permite reproducirlo, revisarlo en un
    /// diff y volver a generarlo si el horno cambia.
    ///
    /// LA PIEZA NO ES UNA CAJA A PROPÓSITO: pasillo con una alcoba lateral y una columna dentro. Una
    /// caja lisa pasaría el recorrido entero sin ejercitar nada de lo que hace falta —contorno con
    /// entrantes, vano que deja jambas, columna con colisión propia— y daría un verde que no
    /// significa nada.
    ///
    /// Se guarda FUERA de <c>Resources/</c>: la biblioteca es lo único que activa el catálogo
    /// autorado, y una pieza suelta no debe cambiar el mundo que se está jugando por el hecho de
    /// existir.
    /// </summary>
    public static class Wg3AuthoredPieceMaker
    {
        private const string PieceFolder = "Assets/WorldGen3/Pieces";
        private const string PieceId = "cor_alcove";
        private const float Thickness = 0.15f;
        private const float Height = 3.2f;

        /// <summary>
        /// El modelo. Medidas escogidas contra el horno, no al azar:
        ///  · pasillo de 3,0 m de paso para que un vano de 2,4 m deje 30 cm de jamba a cada lado —
        ///    un vano tan ancho como su pared se recorta contra sí mismo y deja la esquina rara;
        ///  · alcoba de 2 × 2 m colgando del lado +X, que es lo que obliga al contorno a tener
        ///    entrantes y no ser un rectángulo;
        ///  · los dos vanos en los extremos, en paredes rectas y a ras de la huella, que es lo único
        ///    que el horno acepta como boca.
        /// </summary>
        private static RoomDefinition BuildDefinition()
        {
            var def = new RoomDefinition
            {
                tilesX = 2,
                tilesZ = 3,
                heightMeters = Height,
                wallThickness = Thickness,
                planMode = RoomDefinition.PlanMode.Manual,
                manualContour = new[]
                {
                    new Vector2(-1.5f, -6f),
                    new Vector2(1.5f, -6f),
                    new Vector2(1.5f, 1f),
                    new Vector2(3.5f, 1f),
                    new Vector2(3.5f, 3f),
                    new Vector2(1.5f, 3f),
                    new Vector2(1.5f, 6f),
                    new Vector2(-1.5f, 6f)
                }
            };

            def.holes = new[]
            {
                // Lado 0 del contorno: el tramo sur. Lado 6: el tramo norte.
                new RoomDefinition.WallHole
                {
                    side = 0, along = 0.5f, baseY = 0f, width = 2.4f, height = 2.2f
                },
                new RoomDefinition.WallHole
                {
                    side = 6, along = 0.5f, baseY = 0f, width = 2.4f, height = 2.2f
                }
            };

            // L14 — la columna es ESTRUCTURA: parte el pasillo, tapa la vista y hay que rodearla.
            // Descentrada para que rodearla no sea simétrico.
            def.pillars = new[]
            {
                new RoomDefinition.Pillar
                {
                    position = new Vector2(0.45f, -1.8f), size = 0.5f, sides = 4, yawDegrees = 12f
                }
            };

            return def;
        }

        [MenuItem("Backrooms/WorldGen3/Crear la pieza autorada de prueba")]
        public static void Create()
        {
            RoomDefinition def = BuildDefinition();

            EnsureFolder("Assets/WorldGen3");
            EnsureFolder(PieceFolder);

            // ── malla ───────────────────────────────────────────────────────────────────────
            Mesh mesh = RoomMeshBuilder.Build(def);

            // Si la triangulación tuvo que recurrir al respaldo, la malla y los colliders YA no
            // describen lo mismo. El propio constructor lo avisa con esta bandera y dice que esa
            // sala no se debe hornear: seguir aquí produciría exactamente el fallo que el resto de
            // este trabajo intenta hacer imposible.
            if (RoomMeshBuilder.TriangulationFailed)
            {
                Debug.LogError("[WG3] la triangulación del modelo falló y cayó al respaldo: malla y " +
                               "colisión dejarían de describir lo mismo. NO se hornea.");
                return;
            }

            string meshPath = $"{PieceFolder}/{PieceId}_mesh.asset";
            // Se recrea el asset entero en vez de reescribir el existente: una malla reescrita en
            // sitio se puede quedar dibujando con búferes viejos aunque el fichero esté bien, y ese
            // fallo ya se pagó una vez en este proyecto.
            AssetDatabase.DeleteAsset(meshPath);
            mesh.name = $"{PieceId}_mesh";
            AssetDatabase.CreateAsset(mesh, meshPath);

            // ── prefab visual ───────────────────────────────────────────────────────────────
            var go = new GameObject(PieceId);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                LoadMat("Wg3_Floor"), LoadMat("Wg3_Structure"), LoadMat("Wg3_Ceiling")
            };

            string prefabPath = $"{PieceFolder}/{PieceId}.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);

            // ── asset de pieza + horneado ───────────────────────────────────────────────────
            string assetPath = $"{PieceFolder}/{PieceId}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<Wg3PieceAsset>(assetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<Wg3PieceAsset>();
                AssetDatabase.CreateAsset(asset, assetPath);
            }

            asset.pieceId = PieceId;
            asset.scale = Wg3Scale.Narrow;
            asset.weight = 1f;
            asset.minDepth = 0;
            asset.isDeadEnd = false;
            asset.sourceDefinition = def;
            asset.visualPrefab = prefab;

            List<string> issues = Wg3PieceBaker.Bake(asset);
            if (issues.Count > 0)
            {
                Debug.LogError($"[WG3] la pieza de prueba NO se horneó, {issues.Count} motivo(s):\n" +
                               string.Join("\n", issues), asset);
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var report = new System.Text.StringBuilder();
            report.AppendLine($"[WG3] pieza autorada «{PieceId}» lista en {PieceFolder}");
            report.AppendLine($"  huella {asset.sizeX:0.00} × {asset.sizeZ:0.00} × {asset.heightMeters:0.00} m");
            report.AppendLine($"  pivote del modelo en ({asset.visualPivot.x:0.00}, {asset.visualPivot.y:0.00})");
            report.AppendLine($"  malla: {mesh.vertexCount} vértices, {mesh.subMeshCount} submallas");
            report.AppendLine($"  chuleta: {asset.volumes.Length} cajas");
            foreach (Wg3Socket s in asset.sockets)
                report.AppendLine($"  boca lado {s.side} offset {s.offset:0.00} m ancho {s.width:0.00} " +
                                  $"({s.type}) suelo {s.floorY:0.00} techo {s.ceilingY:0.00}");
            Debug.Log(report.ToString(), asset);

            Selection.activeObject = asset;
        }

        private static Material LoadMat(string name)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(
                $"Assets/Materials/WorldGen3/{name}.mat");
            if (mat == null)
                Debug.LogWarning($"[WG3] falta el material {name}; el prefab saldrá con hueco. " +
                                 "Se crean con Backrooms/WorldGen3/Crear escena de pruebas.");
            return mat;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int cut = path.LastIndexOf('/');
            AssetDatabase.CreateFolder(path.Substring(0, cut), path.Substring(cut + 1));
        }
    }
}
#endif
