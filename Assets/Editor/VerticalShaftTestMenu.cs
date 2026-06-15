#if UNITY_EDITOR
using BackroomsSurvival.Gameplay.Shaft;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Editor helpers: drop a single shaft or a 5×5 shaft grid into the open scene
    /// and build it immediately so it is visible/explorable without entering Play.
    /// </summary>
    public static class VerticalShaftTestMenu
    {
        [MenuItem("Backrooms/Create Vertical Shaft Test")]
        public static void CreateTestShaft()
        {
            var go = new GameObject("VerticalShaftChunk");
            Undo.RegisterCreatedObjectUndo(go, "Create Vertical Shaft Test");
            go.transform.position = Vector3.zero;

            var shaft = go.AddComponent<VerticalShaftChunk>();
            shaft.Generate(); // build now (edit mode)

            Selection.activeGameObject = go;
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();

            Debug.Log("[VerticalShaftTestMenu] Created a vertical shaft at the origin. " +
                      "Place your player on the grate (Y ≈ 0) to explore it.");
        }

        [MenuItem("Backrooms/Create Vertical Shaft Grid Test")]
        public static void CreateTestShaftGrid()
        {
            var go = new GameObject("VerticalShaftGrid");
            Undo.RegisterCreatedObjectUndo(go, "Create Vertical Shaft Grid Test");
            go.transform.position = Vector3.zero;

            var grid = go.AddComponent<VerticalShaftGrid>();
            grid.Generate(); // build now (edit mode)

            Selection.activeGameObject = go;
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();

            Debug.Log("[VerticalShaftTestMenu] Created a 5×5 vertical shaft grid (25 shafts, " +
                      "25×25 m) at the origin. Place your player on a grate (Y ≈ 0) to explore.");
        }
    }
}
#endif
