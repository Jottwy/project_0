using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    public static class GUIStyles
    {
        #region Colors
        public static readonly Color BlueColor = EditorGUIUtility.isProSkin ? new Color(0.8f, 0.92f, 1.065f, 0.78f) : new Color(0.9f, 0.97f, 1.065f, 0.75f);
        public static readonly Color GreenColor = new(0.5f, 1f, 0.5f, 0.75f);
        public static readonly Color RedColor = new(1f, 0.5f, 0.5f, 0.75f);
        public static readonly Color YellowColor = new(1f, 1f, 0.8f, 0.75f);
        public static readonly Color DefaultTextColor = EditorGUIUtility.isProSkin ? new Color(1, 1, 1, 0.7f) : new Color(0.1f, 0.1f, 0.1f, 0.85f);
        #endregion

        #region Styles
        private static GUIStyle _box;
        public static GUIStyle Box
        {
            get
            {
                _box ??= new GUIStyle("box");
                return _box;
            }
        }

        private static GUIStyle _title;
        public static GUIStyle Title
        {
            get
            {
                if (_title == null)
                {
                    _title = new GUIStyle(EditorStyles.helpBox);
                    _title.fontSize = 12;
                    _title.alignment = TextAnchor.MiddleCenter;
                    _title.normal.textColor *= 1.1f;
                }
                return _title;
            }
        }
        
        private static GUIStyle _miniLabelSuffix;
        public static GUIStyle MiniLabelSuffix
        {
            get
            {
                if (_miniLabelSuffix == null)
                {
                    _miniLabelSuffix = new GUIStyle(EditorStyles.miniLabel);
                    _miniLabelSuffix.alignment = TextAnchor.MiddleRight;
                    _miniLabelSuffix.fontSize--;
                }

                return _miniLabelSuffix;
            }
        }

        private static GUIStyle _largeTitleLabel;
        public static GUIStyle LargeTitleLabel
        {
            get
            {
                if (_largeTitleLabel == null)
                {
                    _largeTitleLabel = new GUIStyle(EditorStyles.boldLabel);
                    _largeTitleLabel.fontSize = 14;
                    _largeTitleLabel.normal.textColor = new Color(1, 1, 1, 0.65f);
                }
                return _largeTitleLabel;
            }
        }

        private static GUIStyle _boldMiniGreyLabel;
        public static GUIStyle BoldMiniGreyLabel
        {
            get
            {
                if (_boldMiniGreyLabel == null)
                {
                    _boldMiniGreyLabel = new GUIStyle(EditorStyles.centeredGreyMiniLabel);
                    _boldMiniGreyLabel.alignment = TextAnchor.MiddleLeft;
                    _boldMiniGreyLabel.fontSize = 11;
                    _boldMiniGreyLabel.fontStyle = FontStyle.Bold;
                }
                return _boldMiniGreyLabel;
            }
        }
        
        private static GUIStyle _smallFoldout;
        public static GUIStyle SmallFoldout
        {
            get
            {
                if (_smallFoldout == null)
                {
                    _smallFoldout = new GUIStyle(EditorStyles.foldout);
                    _smallFoldout.normal.textColor = DefaultTextColor;
                    _smallFoldout.padding.left += 2;
                    _smallFoldout.fontSize = 11;
                    _smallFoldout.richText = true;
                }
                return _smallFoldout;
            }
        }
        
        private static GUIStyle _foldout;
        public static GUIStyle Foldout
        {
            get
            {
                if (_foldout == null)
                {
                    _foldout = new GUIStyle(SmallFoldout);
                    _foldout.fontSize = 12;
                    _foldout.fontStyle = FontStyle.Bold;
                }
                return _foldout;
            }
        }

        private static GUIStyle _radioButton;
        public static GUIStyle RadioButton
        {
            get
            {
                if (_radioButton == null)
                {
                    _radioButton = new GUIStyle(EditorStyles.radioButton);
                    _radioButton.padding.left = 16;
                    _radioButton.padding.top -= 2;
                }
                return _radioButton;
            }
        }

        private static GUIStyle _button;
        public static GUIStyle Button
        {
            get
            {
                if (_button == null)
                {
                    _button = EditorGUIUtility.GetBuiltinSkin(EditorSkin.Scene).button;
                    _button.fontStyle = FontStyle.Normal;
                    _button.alignment = TextAnchor.MiddleCenter;
                    _button.padding = new RectOffset(0, 0, 3, 3);
                    _button.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
                    _button.fontSize = 12;
                }
                return _button;
            }
        }
        #endregion
    }
}