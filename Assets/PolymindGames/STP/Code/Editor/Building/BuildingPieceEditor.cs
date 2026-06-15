using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.BuildingSystem.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(BuildingPiece), true)]
    public class BuildingPieceEditor : ToolboxEditor
    {
        private static Color _boundsColor = new(0.4f, 1f, 0f, 0.05f);
        private static float _boundsGroundOffset = 0.1f;
        private static float _boundsScale = 1f;

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            ToolboxEditorGui.DrawLine();

            EditorGUILayout.Space();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (GUILayout.Button("Calculate Bounds"))
            {
                target.SetFieldValue("_localBounds", CalculateBounds());
                EditorUtility.SetDirty(target);
            }

            EditorGUILayout.Space();

            _boundsGroundOffset = EditorGUILayout.Slider("Ground Offset", _boundsGroundOffset, 0f, 1f);
            _boundsScale = EditorGUILayout.Slider("Scale", _boundsScale, 0.1f, 2f);
            _boundsColor = EditorGUILayout.ColorField("Color", _boundsColor);

            EditorGUILayout.EndVertical();
        }
    
        protected Bounds CalculateBounds()
        {
            var buildingPiece = (BuildingPiece)target;
            var trs = buildingPiece.transform;

            // Calculate the bounds without modifications
            var rawLocalBounds = new Bounds(trs.position, Vector3.zero);

            Quaternion initialRotation = trs.rotation;
            trs.rotation = Quaternion.identity;

            foreach (var meshRenderer in buildingPiece.GetComponentsInChildren<MeshRenderer>())
                rawLocalBounds.Encapsulate(meshRenderer.bounds);

            rawLocalBounds = new Bounds(rawLocalBounds.center - trs.position, rawLocalBounds.size);

            trs.rotation = initialRotation;

            // Calculate the bounds with modifications
            Vector3 center = rawLocalBounds.center;
            Vector3 extents = rawLocalBounds.extents;
            Vector3 offset = _boundsGroundOffset * extents.y * Vector3.up;

            center += offset;
            extents -= offset;
            extents = Vector3.Scale(extents, new Vector3(_boundsScale, 1f, _boundsScale));

            return new Bounds(center, extents * 2);
        }

        protected virtual void OnSceneGUI()
        {
            if (Event.current.type != EventType.Repaint)
                return;
            
            var buildingPiece = (BuildingPiece)target;
            var bounds = buildingPiece.GetWorldBounds();

            var oldColor = Handles.color;
            var oldMatrix = Handles.matrix;

            Handles.color = _boundsColor;
            Handles.matrix = Matrix4x4.TRS(bounds.center, buildingPiece.transform.rotation, bounds.size);

            Handles.CubeHandleCap(0, Vector3.zero, Quaternion.identity, 1f, EventType.Repaint);

            Handles.color = oldColor;
            Handles.matrix = oldMatrix;
        }
    }
}