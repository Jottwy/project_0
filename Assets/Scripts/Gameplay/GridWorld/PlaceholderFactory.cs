using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Fase 5C — builds primitive PLACEHOLDER prop geometry at runtime (no new prefab
    /// assets). Each prop is a small hierarchy of Cube/Cylinder primitives sharing the
    /// layer's prop material, tinted per type via a MaterialPropertyBlock, with colliders
    /// stripped (Rust owns collision). The root is anchored at local origin:
    ///   · floor props          → base at y = 0, extending up;
    ///   · ceiling props (cable) → top at y = 0, extending down.
    /// The caller (<see cref="GridChunkBuilder"/>.PlaceProps) positions/rotates the root.
    /// Swap a placeholder for a real prefab later via <c>PropEntry.prefab</c>.
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
                case "paper":   return Paper(mat);
                case "cable":   return Cable(mat, hA);
                case "stain":   return Stain(mat);
                default:        return Cabinet(mat); // unknown type → neutral box
            }
        }

        private static GameObject Desk(Material mat)
        {
            var root = new GameObject("Prop_desk");
            Box(root.transform, mat, Wood, new Vector3(0f, 0.72f, 0f), new Vector3(1.2f, 0.06f, 0.6f));
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Box(root.transform, mat, Wood,
                        new Vector3(sx * 0.55f, 0.36f, sz * 0.25f), new Vector3(0.05f, 0.72f, 0.05f));
            return root;
        }

        private static GameObject Chair(Material mat)
        {
            var root = new GameObject("Prop_chair");
            Box(root.transform, mat, SeatWood, new Vector3(0f, 0.45f, 0f), new Vector3(0.45f, 0.08f, 0.45f));
            Box(root.transform, mat, SeatWood, new Vector3(0f, 0.70f, -0.20f), new Vector3(0.45f, 0.5f, 0.05f));
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Box(root.transform, mat, SeatWood,
                        new Vector3(sx * 0.18f, 0.225f, sz * 0.18f), new Vector3(0.04f, 0.45f, 0.04f));
            return root;
        }

        private static GameObject Cabinet(Material mat)
        {
            var root = new GameObject("Prop_cabinet");
            Box(root.transform, mat, Metal, new Vector3(0f, 0.55f, 0f), new Vector3(0.5f, 1.1f, 0.4f));
            return root;
        }

        private static GameObject Bin(Material mat)
        {
            var root = new GameObject("Prop_bin");
            Cyl(root.transform, mat, DarkGrey, new Vector3(0f, 0.25f, 0f), 0.35f, 0.5f);
            return root;
        }

        private static GameObject Paper(Material mat)
        {
            var root = new GameObject("Prop_paper");
            Box(root.transform, mat, PaperCol, new Vector3(0f, 0.005f, 0f), new Vector3(0.3f, 0.01f, 0.4f));
            return root;
        }

        private static GameObject Cable(Material mat, float hA)
        {
            var root = new GameObject("Prop_cable");
            float len = 1.5f + hA * 1.0f; // 1.5–2.5 m; top at y=0, hangs down
            Box(root.transform, mat, Black, new Vector3(0f, -len * 0.5f, 0f), new Vector3(0.02f, len, 0.02f));
            return root;
        }

        private static GameObject Stain(Material mat)
        {
            var root = new GameObject("Prop_stain");
            Box(root.transform, mat, StainCol, new Vector3(0f, 0.0025f, 0f), new Vector3(0.6f, 0.005f, 0.8f));
            return root;
        }

        private static void Box(Transform parent, Material mat, Color color, Vector3 localPos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            Finish(go, mat, color);
        }

        private static void Cyl(Transform parent, Material mat, Color color, Vector3 localPos,
            float diameter, float height)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(diameter, height * 0.5f, diameter); // Unity cyl = 2 tall
            Finish(go, mat, color);
        }

        private static void Finish(GameObject go, Material mat, Color color)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col); // decorative — Rust owns collision
            var r = go.GetComponent<MeshRenderer>();
            if (mat != null) r.sharedMaterial = mat;
            _mpb.Clear();
            _mpb.SetColor(LayerVisualMaterials.BaseColorId, color);
            r.SetPropertyBlock(_mpb);
        }
    }
}
