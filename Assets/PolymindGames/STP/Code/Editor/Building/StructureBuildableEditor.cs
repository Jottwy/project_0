namespace PolymindGames.BuildingSystem.Editor
{
    /*
    public sealed class StructureBuildableEditor : ObjectEditor
    {
        private enum Axis { X, Z }
        
        private StructureBuildable _buildable;
        private GameObject _currentPreview;
        private SerializedProperty _offsets;
        private ReorderableList _offsetsList;
        private NewSocket.BuildableOffset _selectedOffset;
        private Socket _socket;
        
        private static readonly GUIContent s_GizmoHelpText = new("Edit in scene view (enable gizmos)");

        private static bool s_AlignRotationToggle;
        private static bool s_InvertRotationToggle;
        private static Axis s_MirrorAxis = Axis.X;
        private static bool s_PreviewEnabled;
        private static int s_SelectedBuildableIndex = -1;
        private static int s_SelectedOffsetIndex;


        #region Initialization
        private void OnEnable()
        {
            _socket = (Socket)target;

            if (s_PreviewEnabled)
                Tools.current = Tool.None;

            // Get the parent buildable..
            _buildable = _socket.GetComponentInParent<StructureBuildable>();
            _offsets = serializedObject.FindProperty("m_Offsets");

            PrefabStage.prefabSaving += OnPrefabSave;

            // Initialize the piece list..
            _offsetsList = new ReorderableList(serializedObject, _offsets)
            {
                drawHeaderCallback = rect => GUI.Label(rect, "Categories"),
                drawElementCallback = DrawPieceElement
            };

            _offsetsList.onSelectCallback += OnPieceSelect;
            _offsetsList.index = s_SelectedOffsetIndex < _offsetsList.count ? s_SelectedOffsetIndex : 0;

            _offsetsList.onSelectCallback.Invoke(_offsetsList);
        }

        private void OnDestroy()
        {
            Tools.hidden = false;

            if (_currentPreview != null)
                DestroyImmediate(_currentPreview);
        }

        private void OnPrefabSave(GameObject prefab)
        {
            PrefabStage.prefabSaving -= OnPrefabSave;
            OnDestroy();
        }
        #endregion

        #region Inspector
        public override void DrawCustomInspector()
        {
            serializedObject.Update();

            EditorGUILayout.Space();
            GuiLayout.Separator();
            EditorGUILayout.Space();

            GUI.color = s_PreviewEnabled ? Color.grey : Color.white;

            if (s_PreviewEnabled && _selectedOffset == null)
            {
                _offsetsList.index = Mathf.Clamp(s_SelectedOffsetIndex, 0, _offsetsList.count - 1);
                _offsetsList.onSelectCallback.Invoke(_offsetsList);
            }

            if (GUILayout.Button("Enable Preview"))
            {
                if (!s_PreviewEnabled)
                    EnablePreview();
                else
                    DisablePreview();
            }

            GUI.color = Color.white;

            if (Selection.objects.Length == 1)
                _offsetsList.DoLayoutList();

            DrawSocketTools();

            if (serializedObject.ApplyModifiedProperties())
                _offsetsList.onSelectCallback.Invoke(_offsetsList);

            EditorGUILayout.HelpBox(s_GizmoHelpText);
        }

        private void EnablePreview()
        {
            if (s_PreviewEnabled)
                return;

            Tools.current = Tool.None;

            s_PreviewEnabled = true;
            _offsetsList.onSelectCallback.Invoke(_offsetsList);
        }

        private void DisablePreview()
        {
            if (!s_PreviewEnabled)
                return;

            Tools.current = Tool.Move;

            if (_currentPreview != null)
                DestroyImmediate(_currentPreview);

            s_PreviewEnabled = false;
        }

        private void DrawPieceElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            EditorGUI.BeginChangeCheck();

            bool canSelect = true;
            var categoryRef = _offsets.GetArrayElementAtIndex(index).FindPropertyRelative("m_Category");
            var category = ((DataIdReference<BuildableCategoryDefinition>)categoryRef.GetProperValue(categoryRef.GetFieldInfo())).Def;

            if (category == null || !category.HasMembers)
            {
                GUI.contentColor = Style.DisabledColor;
                canSelect = false;
            }

            float xOffset = rect.width / 10f;
            Rect newRect = new Rect(rect.x + xOffset, rect.y, rect.width - xOffset, 16f);
            EditorGUI.PropertyField(newRect, categoryRef);

            if (canSelect && EditorGUI.EndChangeCheck())
                TrySelectPiece(index);

            GUI.contentColor = Style.NormalColor;
        }

        private void OnPieceSelect(ReorderableList list) => TrySelectPiece(list.index);

        private void TrySelectPiece(int index)
        {
            if (!s_PreviewEnabled)
                return;

            if (_offsetsList.count > 0)
            {
                if (_currentPreview != null)
                    DestroyImmediate(_currentPreview);

                index = Mathf.Clamp(index, 0, _offsetsList.count - 1);
                _selectedOffset = _socket.Offsets[index];

                _offsetsList.index = index;
                s_SelectedOffsetIndex = index;

                BuildingPiece nextBuildable = null;

                if (_selectedOffset != null && !_selectedOffset.Category.IsNull)
                {
                    var buildableDef = _selectedOffset.Category.Def.Members.SelectSequence(ref s_SelectedBuildableIndex);
                    if (((IList)BuildingPieceDefinition.StructureBuildingPiecesDefinitions).Contains(buildableDef))
                        nextBuildable = buildableDef.Prefab;
                }

                CreatePreview(nextBuildable);
            }
        }

        private void CreatePreview(BuildingPiece targetBuildable)
        {
            if (targetBuildable != null)
            {
                GameObject preview = Instantiate(targetBuildable.gameObject, _buildable.transform);
                preview.hideFlags = HideFlags.HideAndDontSave;
                preview.name = "(Preview)";

                preview.GetComponent<StructureBuildable>().enabled = false;

                foreach (var rootComponent in preview.GetComponentsInChildren<Component>())
                {
                    if (rootComponent is StructureBuildable)
                        DestroyImmediate(rootComponent.gameObject);
                    else if (rootComponent is not Transform
                             && rootComponent is not MeshFilter
                             && rootComponent is not MeshRenderer)
                    {
                        DestroyImmediate(rootComponent);
                    }
                }

                _currentPreview = preview;
            }
            else
                _currentPreview = null;
        }
        #endregion

        #region Socket Tools
        private void DrawSocketTools()
        {
            GuiLayout.Separator();

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);

            if (GUILayout.Button("Create Mirror Socket"))
            {
                Vector3 mirrorAxisVector = Vector3.right;

                if (s_MirrorAxis == Axis.Z)
                    mirrorAxisVector = Vector3.forward;

                MirrorSocket(mirrorAxisVector);
            }

            GUI.color = Style.DisabledColor;

            Rect rect = EditorGUILayout.GetControlRect();

            Rect popupRect = new Rect(rect.x + rect.width * 0.75f, rect.y, rect.width * 0.25f, rect.height);
            Rect labelRect = new Rect(rect.xMax - popupRect.width - 72, rect.y, 72, rect.height);

            s_MirrorAxis = (Axis)EditorGUI.EnumPopup(popupRect, s_MirrorAxis);

            EditorGUI.LabelField(labelRect, "Mirror Axis: ");

            GUI.color = Style.NormalColor;

            EditorGUILayout.Space();

            if (GUILayout.Button("Create Perpendicular Socket (90 Degrees)"))
                CreatePerpendicularSocket();

            GUI.color = Style.DisabledColor;

            rect = EditorGUILayout.GetControlRect();

            popupRect = new Rect(rect.x + rect.width * 0.75f, rect.y, rect.width * 0.25f, rect.height);
            labelRect = new Rect(rect.xMax - popupRect.width - 44, rect.y, 44, rect.height);

            s_InvertRotationToggle = EditorGUI.Toggle(popupRect, s_InvertRotationToggle);

            EditorGUI.LabelField(labelRect, "Invert: ");

            rect = EditorGUILayout.GetControlRect();

            popupRect = new Rect(rect.x + rect.width * 0.75f, rect.y, rect.width * 0.25f, rect.height);
            labelRect = new Rect(rect.xMax - popupRect.width - 44, rect.y, 44, rect.height);

            s_AlignRotationToggle = EditorGUI.Toggle(popupRect, s_AlignRotationToggle);

            EditorGUI.LabelField(labelRect, "Align: ");

            GUI.color = Style.NormalColor;
        }

        private void MirrorSocket(in Vector3 mirrorAxisVec)
        {
            Vector3 mirrorScaler = new Vector3(-Mathf.Clamp01(Mathf.Abs(mirrorAxisVec.x)), -Mathf.Clamp01(Mathf.Abs(mirrorAxisVec.y)), -Mathf.Clamp01(Mathf.Abs(mirrorAxisVec.z)));

            if (mirrorScaler.x == 0f)
                mirrorScaler.x = 1f;

            if (mirrorScaler.y == 0f)
                mirrorScaler.y = 1f;

            if (mirrorScaler.z == 0f)
                mirrorScaler.z = 1f;

            Vector3 originalPosition = _socket.transform.localPosition;
            Vector3 mirrorPosition = Vector3.Scale(originalPosition, mirrorScaler);

            GameObject mirrorSocket = Instantiate(_socket.gameObject, _socket.transform.parent);
            mirrorSocket.transform.localPosition = mirrorPosition;

            mirrorSocket.name = "Socket";

            var offsets = mirrorSocket.GetComponent<Socket>().Offsets;

            foreach (var offset in offsets)
                offset.PositionOffset = Vector3.Scale(offset.PositionOffset, mirrorScaler);

            Undo.RegisterCreatedObjectUndo(mirrorSocket, "Create Mirror Socket");

            Selection.activeGameObject = mirrorSocket;
        }

        private void CreatePerpendicularSocket()
        {
            Quaternion rotator = Quaternion.Euler(0f, 90f * (s_InvertRotationToggle ? -1 : 1), 0f);

            Vector3 rotatedSocketPosition = (rotator * _socket.transform.localPosition).Round(3);

            GameObject mirrorSocket = Instantiate(_socket.gameObject, _socket.transform.parent);
            Undo.RegisterCreatedObjectUndo(mirrorSocket, "Crate Perpendicular Socket");

            mirrorSocket.transform.localPosition = rotatedSocketPosition;

            mirrorSocket.name = "Socket";

            var offsets = mirrorSocket.GetComponent<Socket>().Offsets;

            foreach (var offset in offsets)
            {
                offset.PositionOffset = (rotator * offset.PositionOffset).Round(3);

                if (s_AlignRotationToggle)
                    offset.RotationOffset = (rotator * offset.RotationOffset).Round(3);
            }

            Selection.activeGameObject = mirrorSocket;
        }
        #endregion

        #region Scene View
        private void OnSceneGUI()
        {
            if (!CanDisplayMesh() || _buildable == null)
            {
                SceneView.RepaintAll();
                return;
            }

            if (HasValidPiece())
            {
                RefreshPreviewPosition();

                Color prevHandlesColor = Handles.color;
                Handles.color = Color.white;

                var labelStyle = new GUIStyle(EditorStyles.whiteBoldLabel)
                {
                    alignment = TextAnchor.MiddleCenter
                };
                Handles.Label(_currentPreview.transform.position, _currentPreview.name, labelStyle);

                Handles.color = prevHandlesColor;

                // Draw the piece tools (move & rotate).
                DoPieceOffsetTools();
            }

            SceneView.RepaintAll();

            // Draw the inspector for the piece offset for the selected socket, so you can modify the position and rotation precisely.
            DoPieceOffsetInspectorWindow();
        }

        private void RefreshPreviewPosition()
        {
            if (_socket.Offsets.Count <= s_SelectedOffsetIndex)
                return;

            Transform transform = _buildable.transform;
            Vector3 position = _socket.transform.position + transform.TransformVector(_socket.Offsets[s_SelectedOffsetIndex].PositionOffset);
            Quaternion rotation = Quaternion.Euler(transform.rotation * _socket.Offsets[s_SelectedOffsetIndex].RotationOffset);

            _currentPreview.transform.SetPositionAndRotation(position, rotation);
        }

        private void DoPieceOffsetTools()
        {
            Vector3 pieceWorldPos = _socket.transform.position + _socket.transform.TransformVector(_selectedOffset.PositionOffset);

            EditorGUI.BeginChangeCheck();
            Vector3 handlePos = Handles.PositionHandle(pieceWorldPos, Quaternion.Euler(_socket.transform.rotation * _selectedOffset.RotationOffset));

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(target, "Socket");

                handlePos = _socket.transform.InverseTransformPoint(handlePos).Round(3);
                _selectedOffset.PositionOffset = handlePos;
            }
        }

        private void DoPieceOffsetInspectorWindow()
        {
            Color color = Color.white;
            GUI.backgroundColor = color;

            var windowRect = new Rect(16f, 32f, 256f, 112f);
            Rect totalRect = new Rect(windowRect.x, windowRect.y - 16f, windowRect.width, windowRect.height);

            GUI.backgroundColor = Color.white;
            GUI.Window(1, windowRect, DrawPieceOffsetInspector, "Position & Rotation");

            Event e = Event.current;

            if (totalRect.Contains(e.mousePosition))
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

                if (e.type != EventType.Layout && e.type != EventType.Repaint)
                    e.Use();
            }
        }

        private void DrawPieceOffsetInspector(int windowID)
        {
            if (!HasValidPiece())
            {
                EditorGUI.HelpBox(new Rect(0f, 32f, 512f, 32f), "No valid piece selected!", UnityEditor.MessageType.Warning);
                return;
            }

            var pieceOffset = _selectedOffset;

            EditorGUI.BeginChangeCheck();

            // Position field.
            var positionOffset = EditorGUI.Vector3Field(new Rect(6f, 32f, 240f, 16f), "Position", pieceOffset.PositionOffset);

            // Rotation field.
            var rotationOffset = EditorGUI.Vector3Field(new Rect(6f, 64f, 240f, 16f), "Rotation", pieceOffset.RotationOffset);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(target, "Socket");

                positionOffset = positionOffset.Round(3);
                rotationOffset = rotationOffset.Round(3);

                pieceOffset.PositionOffset = positionOffset;
                pieceOffset.RotationOffset = rotationOffset;
            }
        }

        private bool HasValidPiece() => _currentPreview != null
                                        && _offsetsList.count != 0
                                        && s_SelectedOffsetIndex >= 0
                                        && _selectedOffset != null
                                        && _selectedOffset.Category != 0;

        private bool CanDisplayMesh() => s_PreviewEnabled
                                         && Selection.activeGameObject != null
                                         && Selection.activeGameObject == _socket.gameObject;
        #endregion
        
        #region Internal Types
        // private class SocketDrawer
        // {
        //     private static int s_SelectedOffsetIndex;
        //     private static int s_SelectedBuildableIndex = -1;
        //     private StructureBuildable _buildable;
        //     private GameObject _currentPreview;
        //
        //     private SerializedProperty _offsets;
        //
        //     private ReorderableList _offsetsList;
        //     private Socket.BuildableOffset _selectedOffset;
        //     private Socket _socket;
        // }

        private static class Style
        {
            public static readonly Color DisabledColor = new(0.9f, 0.9f, 0.9f, 0.9f);
            public static readonly Color NormalColor = Color.white;
        }
        #endregion
    }*/
}