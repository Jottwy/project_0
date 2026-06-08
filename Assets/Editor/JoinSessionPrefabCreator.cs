#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.EditorTools
{
    public static class JoinSessionPrefabCreator
    {
        private const float InputWidth = 300f;
        private const float InputHeight = 40f;
        private const float ButtonWidth = 120f;
        private const float ButtonHeight = 40f;
        private const float Spacing = 10f;
        private const float Padding = 24f;

        [MenuItem("Backrooms/Create JoinSession Prefab")]
        public static void CreatePrefab()
        {
            var panel = BuildPanel();

            string path = "Assets/Prefabs/UI/JoinSessionPanel.prefab";
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs/UI"))
                AssetDatabase.CreateFolder("Assets/Prefabs", "UI");

            PrefabUtility.SaveAsPrefabAsset(panel, path);
            Object.DestroyImmediate(panel);

            Debug.Log($"[JoinSessionPrefabCreator] Prefab saved to {path}");
            AssetDatabase.Refresh();
        }

        private static GameObject BuildPanel()
        {
            // Root panel
            var panel = new GameObject("JoinSessionPanel");
            var rt = panel.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            var bg = panel.AddComponent<Image>();
            bg.color = new Color(0.06f, 0.06f, 0.10f, 0.88f);
            bg.raycastTarget = true;

            var cg = panel.AddComponent<CanvasGroup>();
            cg.alpha = 1f;
            cg.interactable = true;
            cg.blocksRaycasts = true;

            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.spacing = Spacing;
            vlg.padding = new RectOffset((int)Padding, (int)Padding, (int)Padding, (int)Padding);
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;

            var csf = panel.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Title
            CreateLabel(panel.transform, "Title", "BACKROOMS SURVIVAL", 22,
                FontStyle.Bold, Color.white, InputWidth, 36f);

            // Status
            CreateLabel(panel.transform, "Status", "", 14,
                FontStyle.Normal, new Color(0.7f, 0.7f, 0.7f), InputWidth, 24f);

            // Inputs
            CreateInputField(panel.transform, "NameField", "Player Name", "Player");
            CreateInputField(panel.transform, "IPField", "Server IP", "127.0.0.1");
            CreateInputField(panel.transform, "PortField", "Port", "7778");

            // Button row
            var buttonRow = new GameObject("ButtonRow");
            buttonRow.transform.SetParent(panel.transform, false);
            var rowRt = buttonRow.AddComponent<RectTransform>();
            rowRt.sizeDelta = new Vector2(InputWidth, ButtonHeight);
            var rowLe = buttonRow.AddComponent<LayoutElement>();
            rowLe.preferredWidth = InputWidth;
            rowLe.preferredHeight = ButtonHeight;

            var hlg = buttonRow.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.spacing = 16f;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            CreateButton(buttonRow.transform, "HostBtn", "Host", new Color(0.18f, 0.55f, 0.28f));
            CreateButton(buttonRow.transform, "JoinBtn", "Join", new Color(0.20f, 0.40f, 0.70f));

            // Disconnect
            CreateButton(panel.transform, "DisconnectBtn", "Disconnect", new Color(0.60f, 0.20f, 0.20f));

            return panel;
        }

        private static void CreateLabel(Transform parent, string name, string text,
            int fontSize, FontStyle style, Color color, float w, float h)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = w;
            le.preferredHeight = h;

            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.text = text;
            txt.raycastTarget = false;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
            shadow.effectDistance = new Vector2(1f, -1f);
        }

        private static void CreateInputField(Transform parent, string name,
            string placeholder, string defaultValue)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = InputWidth;
            le.preferredHeight = InputHeight;

            var bgImg = go.AddComponent<Image>();
            bgImg.color = new Color(0.13f, 0.13f, 0.18f, 1f);

            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.3f, 0.3f, 0.4f, 0.5f);
            outline.effectDistance = new Vector2(1f, -1f);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(10f, 4f);
            textRt.offsetMax = new Vector2(-10f, -4f);
            var textComp = textGo.AddComponent<Text>();
            textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            textComp.fontSize = 16;
            textComp.color = Color.white;

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(go.transform, false);
            var phRt = phGo.AddComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(10f, 4f);
            phRt.offsetMax = new Vector2(-10f, -4f);
            var phText = phGo.AddComponent<Text>();
            phText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            phText.fontSize = 16;
            phText.fontStyle = FontStyle.Italic;
            phText.color = new Color(0.45f, 0.45f, 0.55f);
            phText.text = placeholder;

            var input = go.AddComponent<InputField>();
            input.textComponent = textComp;
            input.placeholder = phText;
            input.text = defaultValue;
        }

        private static void CreateButton(Transform parent, string name,
            string label, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = ButtonWidth;
            le.preferredHeight = ButtonHeight;

            var img = go.AddComponent<Image>();
            img.color = color;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
            var txt = textGo.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = 16;
            txt.fontStyle = FontStyle.Bold;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.text = label;
            txt.raycastTarget = false;
        }
    }
}
#endif
