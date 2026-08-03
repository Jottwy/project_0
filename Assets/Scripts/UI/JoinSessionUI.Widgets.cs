using System;
using System.Collections;
using BackroomsSurvival.Net;
using PolymindGames;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    // Fabricas uGUI genericas: no saben nada de esta pantalla ni del estado de sesion.
    // Viven en una particion aparte para que JoinSessionUI.cs sea solo ciclo de vida,
    // estado y red.
    public sealed partial class JoinSessionUI
    {
        public static void EnsureEventSystem()
        {
            var existing = EventSystem.current ?? UnityEngine.Object.FindFirstObjectByType<EventSystem>();
            if (existing != null)
            {
                Debug.Log($"[JoinSessionUI] EventSystem found: {existing.name}");
                EnsureInputModule(existing.gameObject);
                return;
            }

            // SCENE-SCOPED on purpose (was DontDestroyOnLoad): a DDOL EventSystem created in
            // MainMenu (which has none baked) survived into STP_Showcase, whose BAKED EventSystem
            // then coexisted with it → Unity's "multiple EventSystems" warning spam every frame
            // and a fragile input setup. Scenes that lack a baked EventSystem still get one here
            // (EnsureEventSystem runs again per scene); scenes that have one keep theirs alone.
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            EnsureInputModule(go);
            Debug.Log("[JoinSessionUI] EventSystem created (scene-scoped)");
        }

        private static void EnsureInputModule(GameObject go)
        {
#if ENABLE_INPUT_SYSTEM
            if (go.GetComponent<InputSystemUIInputModule>() != null)
                return;

            foreach (var module in go.GetComponents<BaseInputModule>())
                UnityEngine.Object.Destroy(module);

            go.AddComponent<InputSystemUIInputModule>();
            Debug.Log("[JoinSessionUI] EventSystem using InputSystemUIInputModule");
#else
            if (go.GetComponent<BaseInputModule>() != null)
                return;

            go.AddComponent<StandaloneInputModule>();
            Debug.Log("[JoinSessionUI] EventSystem using StandaloneInputModule");
#endif
        }

        private static Text CreateLabel(Transform parent, string name, string text,
            int fontSize, FontStyle style, Color color, float width, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.preferredHeight = height;

            var txt = go.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.text = text;
            txt.raycastTarget = false;
            txt.supportRichText = true;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
            shadow.effectDistance = new Vector2(1f, -1f);

            return txt;
        }

        private static InputField CreateInputField(Transform parent, string name,
            string placeholder, string defaultValue)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = InputWidth;
            le.preferredHeight = InputHeight;

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.13f, 0.13f, 0.18f, 1f);

            // Rounded feel via outline
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.3f, 0.3f, 0.4f, 0.5f);
            outline.effectDistance = new Vector2(1f, -1f);

            // Text child
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
            textComp.supportRichText = false;
            textComp.raycastTarget = false;

            // Placeholder child
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
            phText.raycastTarget = false;

            var input = go.AddComponent<InputField>();
            input.targetGraphic = bg;
            input.textComponent = textComp;
            input.placeholder = phText;
            input.text = defaultValue;
            input.interactable = true;

            return input;
        }

        private static Button CreateButton(Transform parent, string name,
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
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.9f, 0.9f, 0.9f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
            colors.selectedColor = Color.white;
            btn.colors = colors;
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

            return btn;
        }
    }
}
