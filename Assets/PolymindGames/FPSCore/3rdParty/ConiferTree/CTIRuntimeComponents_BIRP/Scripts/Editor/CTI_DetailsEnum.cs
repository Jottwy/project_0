using UnityEditor;
using UnityEngine;

namespace CTI
{
    public sealed class CTI_DetailsEnum : MaterialPropertyDrawer
    {
        public enum DetailMode
        {
            Disabled = 0,
            Enabled = 1,
            FadeBaseTextures = 2,
            SkipBaseTextures = 3
        }

        private DetailMode _mStatus;


        public override void OnGUI(Rect position, MaterialProperty prop, string label, MaterialEditor editor)
        {
            Material material = editor.target as Material;

            _mStatus = (DetailMode)((int)prop.floatValue);
            _mStatus = (DetailMode)EditorGUI.EnumPopup(position, label, _mStatus);
            prop.floatValue = (float)_mStatus;

            if (prop.floatValue == 0.0f)
            {
                material.DisableKeyword("GEOM_TYPE_BRANCH");
                material.DisableKeyword("GEOM_TYPE_BRANCH_DETAIL");
                material.DisableKeyword("GEOM_TYPE_FROND");
            }
            else if (prop.floatValue == 1.0f)
            {
                material.EnableKeyword("GEOM_TYPE_BRANCH");
                material.DisableKeyword("GEOM_TYPE_BRANCH_DETAIL");
                material.DisableKeyword("GEOM_TYPE_FROND");
            }
            else if (prop.floatValue == 2.0f)
            {
                material.DisableKeyword("GEOM_TYPE_BRANCH");
                material.EnableKeyword("GEOM_TYPE_BRANCH_DETAIL");
                material.DisableKeyword("GEOM_TYPE_FROND");
            }
            else if (prop.floatValue == 3.0f)
            {
                material.DisableKeyword("GEOM_TYPE_BRANCH");
                material.DisableKeyword("GEOM_TYPE_BRANCH_DETAIL");
                material.EnableKeyword("GEOM_TYPE_FROND");
            }
        }
    }
}