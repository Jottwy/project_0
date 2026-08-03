#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Read-only diagnostic: prints the world-space size of the imported Meshy meshes that are
    /// candidates for the buildable frame and its infill panels, plus the frame subdivision each
    /// panel implies.
    ///
    /// It exists because the FBXs are 80 MB binary and the editor cannot be driven headless while
    /// it is open, so the sizes cannot be read any other way. Writes nothing and touches no asset —
    /// delete it once the frame dimensions are fixed in code.
    /// </summary>
    public static class BackroomsMeshProbe
    {
        private const string FramePath =
            "Assets/MeshyImports/Steel Frame Grid_20260801_201107/Meshy_AI_Steel_Frame_Grid_0731205246_texture.fbx";
        private const string DrywallPath =
            "Assets/MeshyImports/Back of a Flight Case_20260803_121714/Meshy_AI_Back_of_a_Flight_Case_0803101651_texture.fbx";
        private const string BoardPath =
            "Assets/MeshyImports/Golden textured board_20260731_204957/Meshy_AI_Golden_textured_board_0731184937_texture.fbx";

        // The slot the frame has to fill: same 5 x 4 x 0.2 as the wall GridWallSnap already places.
        private const float SlotLength = 5f;
        private const float SlotHeight = 4f;

        [MenuItem("Backrooms/Diagnostics/Measure Building Meshes")]
        public static void Measure()
        {
            var report = new StringBuilder("[BackroomsMeshProbe]\n");
            Report(report, "FRAME   ", FramePath);
            Report(report, "DRYWALL ", DrywallPath);
            Report(report, "BOARD   ", BoardPath);
            Debug.Log(report.ToString());
        }

        private static void Report(StringBuilder report, string label, string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                report.AppendLine($"{label} MISSING at '{path}'");
                return;
            }

            // Combined over every filter: a Meshy import is often several submeshes under a root,
            // and one submesh's bounds would understate the object.
            var filters = asset.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length == 0)
            {
                report.AppendLine($"{label} no MeshFilter under '{path}'");
                return;
            }

            var bounds = new Bounds();
            bool started = false;
            int vertices = 0;
            foreach (var filter in filters)
            {
                var mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;

                vertices += mesh.vertexCount;

                // Local bounds through the prefab-root-relative transform: the FBX root may carry a
                // scale from the import settings, and the raw mesh bounds would ignore it.
                var local = mesh.bounds;
                var matrix = filter.transform.localToWorldMatrix;
                var centre = matrix.MultiplyPoint3x4(local.center);
                var extents = matrix.MultiplyVector(local.extents);
                var world = new Bounds(centre, new Vector3(
                    Mathf.Abs(extents.x) * 2f, Mathf.Abs(extents.y) * 2f, Mathf.Abs(extents.z) * 2f));

                if (started)
                    bounds.Encapsulate(world);
                else
                {
                    bounds = world;
                    started = true;
                }
            }

            if (!started)
            {
                report.AppendLine($"{label} every MeshFilter under '{path}' has a null sharedMesh");
                return;
            }

            var size = bounds.size;
            report.AppendLine($"{label} size=({size.x:F3}, {size.y:F3}, {size.z:F3}) " +
                              $"centre=({bounds.center.x:F3}, {bounds.center.y:F3}, {bounds.center.z:F3}) " +
                              $"meshes={filters.Length} verts={vertices}");

            // RAW bounds and the node scale that separates them from the size above. This distinction
            // is the whole point of the line: the size above is what you get by INSTANTIATING the
            // model asset (node scale included), while RAW is what you get by assigning
            // mesh.sharedMesh onto a bare GameObject. Reading one and building with the other put a
            // 4 mm speck in the player's hands.
            var first = System.Array.Find(filters, f => f.sharedMesh != null);
            if (first != null)
            {
                var raw = first.sharedMesh.bounds.size;
                var nodeScale = first.transform.lossyScale;
                report.AppendLine($"{label} RAW mesh.bounds=({raw.x:F4}, {raw.y:F4}, {raw.z:F4}) " +
                                  $"node lossyScale=({nodeScale.x:F3}, {nodeScale.y:F3}, {nodeScale.z:F3}) " +
                                  $"→ scale to reach 2 m wide from RAW: " +
                                  $"{(Mathf.Max(raw.x, raw.z) > 0.0001f ? 2f / Mathf.Max(raw.x, raw.z) : 0f):F3}");
            }

            // Which axis is the thin one tells us how the mesh sits in its own space, which decides
            // whether the frame prefab needs a rotation offset.
            float min = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
            string thin = Mathf.Approximately(min, size.x) ? "X" : Mathf.Approximately(min, size.y) ? "Y" : "Z";
            report.AppendLine($"{label} thin axis={thin} (a wall panel must end up thin on Z once the prefab is built)");

            // Scale to fill the 5 x 4 slot, and how many copies of the panel tile that slot.
            float wide = Mathf.Max(size.x, size.z);
            float tall = size.y;
            if (wide > 0.0001f && tall > 0.0001f)
            {
                report.AppendLine($"{label} to fill the 5x4 slot: scale=({SlotLength / wide:F3} wide, {SlotHeight / tall:F3} tall) " +
                                  $"| at native size it tiles {SlotLength / wide:F2} x {SlotHeight / tall:F2} times " +
                                  $"(aspect {wide / tall:F3})");
            }
        }
    }
}
#endif
