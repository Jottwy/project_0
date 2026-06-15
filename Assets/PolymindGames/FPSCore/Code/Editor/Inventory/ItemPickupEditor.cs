using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.InventorySystem.Editor
{
    [CustomEditor(typeof(ItemPickup), true)]
    public class ItemPickupEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            EditorGUILayout.Space();

            using (new BackgroundColorScope(GUIStyles.YellowColor))
            {
                if (GUILayout.Button("Reset Item"))
                {
                    string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot((ItemPickup)target);

                    ItemPickup itemPickup;

                    if (!string.IsNullOrEmpty(path))
                        itemPickup = AssetDatabase.LoadAssetAtPath<ItemPickup>(path);
                    else
                        itemPickup = (ItemPickup)target;

                    if (itemPickup == null)
                        return;

                    foreach (var def in ItemDefinition.Definitions)
                    {
                        if (def.Pickup == itemPickup)
                        {
                            target.SetFieldValue("_item", new DataIdReference<ItemDefinition>(def));
                            break;
                        }
                    }

                    EditorUtility.SetDirty(target);
                }
            }
        }
    }
}