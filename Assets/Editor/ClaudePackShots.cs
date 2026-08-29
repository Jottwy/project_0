#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    public static class ClaudePackShots
    {
        private const int Cell = 320;
        private const int Cols = 4;

        private static readonly string[] Targets =
        {
            "Assets/Prefabs/WorldProps/BR_Prop_Reception.prefab",
            "Assets/Prefabs/WorldProps/BR_Prop_Restroom.prefab",
            "Assets/Prefabs/WorldProps/BR_Prop_BreakRoom.prefab",
            "Assets/Prefabs/WorldProps/BR_Prop_Archive.prefab",
            "Assets/Prefabs/WorldProps/BR_Prop_ElevatorBank.prefab",
            "Assets/Prefabs/WorldProps/BR_Prop_LockerRow.prefab",
            "Assets/GroceryStorePropsCollection/Prefabs/URP/SM_ExitSign1.prefab",
            "Assets/GroceryStorePropsCollection/Prefabs/URP/SM_Duct1.prefab",
        };

        [MenuItem("Backrooms/Claude/Packs/9 Contact Sheet")]
        public static void ContactSheet()
        {
            var log = new StringBuilder();
            int rows = Mathf.CeilToInt(Targets.Length / (float)Cols);
            var sheet = new Texture2D(Cols * Cell, rows * Cell, TextureFormat.RGB24, false);
            var clear = Enumerable.Repeat(new Color(0.12f, 0.12f, 0.14f), Cols * Cell * rows * Cell).ToArray();
            sheet.SetPixels(clear);

            var pru = new PreviewRenderUtility();
            try
            {
                for (int i = 0; i < Targets.Length; i++)
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(Targets[i]);
                    if (go == null) { log.AppendLine("NO ENCONTRADO " + Targets[i]); continue; }

                    var tex = RenderOne(pru, go);
                    if (tex == null) { log.AppendLine("SIN RENDER " + Targets[i]); continue; }

                    int col = i % Cols, row = i / Cols;
                    int x = col * Cell, y = (rows - 1 - row) * Cell;
                    sheet.SetPixels(x, y, Cell, Cell, tex.GetPixels());
                    UnityEngine.Object.DestroyImmediate(tex);
                    log.AppendLine("ok " + Targets[i]);
                }
            }
            finally { pru.Cleanup(); }

            sheet.Apply();
            var bytes = sheet.EncodeToPNG();
            File.WriteAllBytes("Temp/claude_contact_sheet.png", bytes);
            UnityEngine.Object.DestroyImmediate(sheet);
            File.WriteAllText("Temp/claude_packs4.log", log.ToString());
            Debug.Log("[ClaudePackShots] hoja lista en Temp/claude_contact_sheet.png");
        }

        private static Texture2D RenderOne(PreviewRenderUtility pru, GameObject prefab)
        {
            var inst = UnityEngine.Object.Instantiate(prefab);
            inst.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var rends = inst.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled).ToArray();
                if (rends.Length == 0) return null;
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                float radius = Mathf.Max(b.extents.magnitude, 0.05f);

                var rect = new Rect(0, 0, Cell, Cell);
                pru.BeginStaticPreview(rect);
                pru.AddSingleGO(inst);

                var cam = pru.camera;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
                cam.orthographic = false;
                cam.fieldOfView = 30f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = radius * 20f;
                var dir = new Vector3(0.6f, 0.45f, -1f).normalized;
                cam.transform.position = b.center + dir * (radius * 3.2f);
                cam.transform.LookAt(b.center);

                pru.lights[0].intensity = 1.2f;
                pru.lights[0].transform.rotation = Quaternion.Euler(35f, 40f, 0f);
                pru.lights[1].intensity = 0.6f;
                pru.ambientColor = new Color(0.35f, 0.35f, 0.38f);

                pru.Render(true, false);
                return pru.EndStaticPreview();
            }
            finally { UnityEngine.Object.DestroyImmediate(inst); }
        }
    }
}
#endif