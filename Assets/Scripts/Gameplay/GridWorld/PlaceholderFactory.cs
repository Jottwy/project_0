using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Fase 5C — builds primitive PLACEHOLDER prop geometry at runtime (no new prefab
    /// assets). Each prop is a small hierarchy of Cube/Cylinder primitives sharing the
    /// layer's prop material, tinted per type via a MaterialPropertyBlock. The root is
    /// anchored at local origin:
    ///   · floor props          → base at y = 0, extending up;
    ///   · ceiling props (cable) → top at y = 0, extending down.
    /// The caller (<see cref="GridChunkBuilder"/>.PlaceProps) positions/rotates the root.
    /// Swap a placeholder for a real prefab later via <c>PropEntry.prefab</c>.
    /// Collider policy (enmienda a la nota de arquitectura de knee walls, docs/DECISIONS.md):
    /// solid floor props (desk/chair/cabinet/bin, más partition/filecab de ZONE_OFFICE) keep
    /// their primitive collider as a local physical blocker — Rust NPC collision stays
    /// authoritative and unaffected, this is player-only. Thin decals (paper/stain), the
    /// hanging cable and sub-5 cm details (the file cabinet's handles) stay collider-less: a
    /// 0.01 m decal would create a micro-step and the cable would be an invisible mid-air
    /// obstacle.
    /// </summary>
    public static class PlaceholderFactory
    {
        private static readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();

        // Per-type base colours (MPB over the shared prop material).
        private static readonly Color Wood     = new Color(0.30f, 0.20f, 0.12f);
        private static readonly Color SeatWood = new Color(0.26f, 0.19f, 0.13f);
        private static readonly Color Metal    = new Color(0.30f, 0.34f, 0.30f); // grey-green cabinet
        private static readonly Color DarkGrey = new Color(0.18f, 0.18f, 0.20f);
        private static readonly Color PaperCol = new Color(0.80f, 0.78f, 0.72f);
        private static readonly Color Black    = new Color(0.05f, 0.05f, 0.05f);
        private static readonly Color StainCol = new Color(0.12f, 0.09f, 0.06f);
        private static readonly Color PartitionCol = new Color(0.55f, 0.53f, 0.45f); // tela beige de mampara
        private static readonly Color ScreenCol    = new Color(0.16f, 0.19f, 0.17f); // CRT apagado
        private static readonly Color CardboardCol = new Color(0.62f, 0.48f, 0.32f);
        private static readonly Color PotCol       = new Color(0.36f, 0.24f, 0.18f);
        private static readonly Color LeafCol      = new Color(0.24f, 0.34f, 0.20f);
        private static readonly Color BoardCol     = new Color(0.88f, 0.88f, 0.85f);

        /// <summary>Create a placeholder for <paramref name="type"/>. <paramref name="hA"/>
        /// is a per-prop hash value in [0,1) for type variation (e.g. cable length).</summary>
        public static GameObject Create(string type, Material mat, float hA)
        {
            switch (type)
            {
                case "desk":    return Desk(mat);
                case "chair":   return Chair(mat);
                case "cabinet": return Cabinet(mat);
                case "bin":     return Bin(mat);
                case "partition": return Partition(mat);
                case "filecab":   return FileCabinet(mat);
                case "monitor":   return Monitor(mat);
                case "boxes":     return Boxes(mat, hA);
                case "plant":     return Plant(mat);
                case "whiteboard": return Whiteboard(mat);
                case "paper":   return Paper(mat);
                case "cable":   return Cable(mat, hA);
                case "stain":   return Stain(mat);
                default:        return Cabinet(mat); // unknown type → neutral box
            }
        }

        private static GameObject Desk(Material mat)
        {
            var root = new GameObject("Prop_desk");
            Box(root.transform, mat, Wood, new Vector3(0f, 0.72f, 0f), new Vector3(1.2f, 0.06f, 0.6f), keepCollider: true);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Box(root.transform, mat, Wood,
                        new Vector3(sx * 0.55f, 0.36f, sz * 0.25f), new Vector3(0.05f, 0.72f, 0.05f), keepCollider: true);
            return root;
        }

        private static GameObject Chair(Material mat)
        {
            var root = new GameObject("Prop_chair");
            Box(root.transform, mat, SeatWood, new Vector3(0f, 0.45f, 0f), new Vector3(0.45f, 0.08f, 0.45f), keepCollider: true);
            Box(root.transform, mat, SeatWood, new Vector3(0f, 0.70f, -0.20f), new Vector3(0.45f, 0.5f, 0.05f), keepCollider: true);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Box(root.transform, mat, SeatWood,
                        new Vector3(sx * 0.18f, 0.225f, sz * 0.18f), new Vector3(0.04f, 0.45f, 0.04f), keepCollider: true);
            return root;
        }

        private static GameObject Cabinet(Material mat)
        {
            var root = new GameObject("Prop_cabinet");
            Box(root.transform, mat, Metal, new Vector3(0f, 0.55f, 0f), new Vector3(0.5f, 1.1f, 0.4f), keepCollider: true);
            return root;
        }

        private static GameObject Bin(Material mat)
        {
            var root = new GameObject("Prop_bin");
            Cyl(root.transform, mat, DarkGrey, new Vector3(0f, 0.25f, 0f), 0.35f, 0.5f, keepCollider: true);
            return root;
        }

        /// <summary>
        /// ZONE_OFFICE — mampara de cubículo: panel de 2.2 m × 1.45 m sobre dos pies.
        ///
        /// La ALTURA es una cantidad de jugabilidad, no estética, por el mismo motivo que
        /// <see cref="LayerVisualConfig.MinKneeWallHeight"/>: lleva collider, así que un panel
        /// más bajo que ~1.15 m lo saltaría el jugador (FPS_Player.prefab _jumpHeight 1.05,
        /// m_StepOffset 0.275). 1.45 m deja margen y sigue por debajo de la altura de ojos
        /// (~1.65 m) — se ve por encima de la planta, no se pasa por encima.
        /// </summary>
        private static GameObject Partition(Material mat)
        {
            var root = new GameObject("Prop_partition");
            Box(root.transform, mat, PartitionCol, new Vector3(0f, 0.775f, 0f), new Vector3(2.2f, 1.35f, 0.07f), keepCollider: true);
            for (int sx = -1; sx <= 1; sx += 2)
                Box(root.transform, mat, Metal, new Vector3(sx * 1.0f, 0.05f, 0f), new Vector3(0.12f, 0.10f, 0.45f), keepCollider: true);
            return root;
        }

        /// <summary>ZONE_OFFICE — archivador de 4 cajones, más alto y estrecho que
        /// <see cref="Cabinet"/> para que las dos siluetas no se confundan de lejos.</summary>
        private static GameObject FileCabinet(Material mat)
        {
            var root = new GameObject("Prop_filecab");
            Box(root.transform, mat, Metal, new Vector3(0f, 0.66f, 0f), new Vector3(0.42f, 1.32f, 0.62f), keepCollider: true);
            // Tiradores: sin collider, son 3 cm de relieve — un collider ahí solo añadiría
            // micro-escalones contra los que engancharse.
            for (int i = 0; i < 4; i++)
                Box(root.transform, mat, DarkGrey, new Vector3(0f, 0.20f + i * 0.31f, 0.32f), new Vector3(0.20f, 0.03f, 0.03f), keepCollider: false);
            return root;
        }

        /// <summary>ZONE_OFFICE — monitor CRT sobre su peana. Bajo (0.75 m) a propósito: se
        /// lee como "hay un escritorio ahí" sin necesitar que el escritorio caiga en la misma
        /// sub-celda, que el sorteo no garantiza.</summary>
        private static GameObject Monitor(Material mat)
        {
            var root = new GameObject("Prop_monitor");
            Box(root.transform, mat, Wood, new Vector3(0f, 0.36f, 0f), new Vector3(0.9f, 0.72f, 0.5f), keepCollider: true);
            Box(root.transform, mat, DarkGrey, new Vector3(0f, 0.75f, 0f), new Vector3(0.06f, 0.06f, 0.2f), keepCollider: false);
            Box(root.transform, mat, DarkGrey, new Vector3(0f, 0.98f, 0f), new Vector3(0.44f, 0.40f, 0.38f), keepCollider: true);
            Box(root.transform, mat, ScreenCol, new Vector3(0f, 0.98f, 0.20f), new Vector3(0.36f, 0.30f, 0.02f), keepCollider: false);
            return root;
        }

        /// <summary>ZONE_OFFICE — pila de cajas de archivo, altura variable por
        /// <paramref name="hA"/>.</summary>
        private static GameObject Boxes(Material mat, float hA)
        {
            var root = new GameObject("Prop_boxes");
            int n = 2 + (int)(hA * 2.99f); // 2..4
            for (int i = 0; i < n; i++)
                Box(root.transform, mat, CardboardCol,
                    new Vector3((i % 2) * 0.06f, 0.16f + i * 0.32f, (i % 3) * 0.05f),
                    new Vector3(0.5f, 0.32f, 0.4f), keepCollider: true);
            return root;
        }

        /// <summary>ZONE_OFFICE — planta de oficina en su maceta. El follaje va sin collider:
        /// es lo único que asoma por encima de la maceta y no debe frenar al jugador por
        /// rozarlo.</summary>
        private static GameObject Plant(Material mat)
        {
            var root = new GameObject("Prop_plant");
            Cyl(root.transform, mat, PotCol, new Vector3(0f, 0.18f, 0f), 0.36f, 0.36f, keepCollider: true);
            Box(root.transform, mat, LeafCol, new Vector3(0f, 0.75f, 0f), new Vector3(0.5f, 0.8f, 0.5f), keepCollider: false);
            return root;
        }

        /// <summary>ZONE_OFFICE — pizarra. Va SIN collider y a altura de pared a propósito:
        /// es decoración vertical, y un bloque sólido a 1.5 m en mitad de una sub-celda sería
        /// un obstáculo invisible de leer.</summary>
        private static GameObject Whiteboard(Material mat)
        {
            var root = new GameObject("Prop_whiteboard");
            Box(root.transform, mat, BoardCol, new Vector3(0f, 1.55f, 0f), new Vector3(1.6f, 0.95f, 0.05f), keepCollider: false);
            Box(root.transform, mat, Metal, new Vector3(0f, 1.06f, 0f), new Vector3(1.6f, 0.06f, 0.09f), keepCollider: false);
            return root;
        }

        private static GameObject Paper(Material mat)
        {
            var root = new GameObject("Prop_paper");
            Box(root.transform, mat, PaperCol, new Vector3(0f, 0.005f, 0f), new Vector3(0.3f, 0.01f, 0.4f), keepCollider: false);
            return root;
        }

        private static GameObject Cable(Material mat, float hA)
        {
            var root = new GameObject("Prop_cable");
            float len = 1.5f + hA * 1.0f; // 1.5–2.5 m; top at y=0, hangs down
            Box(root.transform, mat, Black, new Vector3(0f, -len * 0.5f, 0f), new Vector3(0.02f, len, 0.02f), keepCollider: false);
            return root;
        }

        private static GameObject Stain(Material mat)
        {
            var root = new GameObject("Prop_stain");
            Box(root.transform, mat, StainCol, new Vector3(0f, 0.0025f, 0f), new Vector3(0.6f, 0.005f, 0.8f), keepCollider: false);
            return root;
        }

        private static void Box(Transform parent, Material mat, Color color, Vector3 localPos, Vector3 scale, bool keepCollider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            Finish(go, mat, color, keepCollider);
        }

        private static void Cyl(Transform parent, Material mat, Color color, Vector3 localPos,
            float diameter, float height, bool keepCollider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(diameter, height * 0.5f, diameter); // Unity cyl = 2 tall
            Finish(go, mat, color, keepCollider);
        }

        private static void Finish(GameObject go, Material mat, Color color, bool keepCollider)
        {
            if (!keepCollider)
            {
                // decal/prop colgante — sin presencia física. `Object.Destroy` a secas lanza
                // "Destroy may not be called from edit mode!" fuera de Play Mode, y desde que
                // hay tests EditMode que construyen placeholders esa ruta SÍ se recorre.
                // Mismo patrón que `LayerVisualMaterials.SafeDestroy`, ya establecido en este
                // subsistema; en Play Mode el comportamiento es idéntico al de antes.
                var col = go.GetComponent<Collider>();
                if (col != null)
                {
                    if (Application.isPlaying) Object.Destroy(col);
                    else Object.DestroyImmediate(col);
                }
            }
            var r = go.GetComponent<MeshRenderer>();
            if (mat != null) r.sharedMaterial = mat;
            _mpb.Clear();
            _mpb.SetColor(LayerVisualMaterials.BaseColorId, color);
            r.SetPropertyBlock(_mpb);
        }
    }
}
