using UnityEngine;
using UnityEngine.UI;

namespace PolymindGames.UserInterface
{
    public sealed class SimpleCrosshairUI : CrosshairBehaviourUI
    {
        private Image[] _crosshairImages;

        public override void SetSize(float accuracy, float scale) { }

        public override void SetColor(Color color)
        {
            foreach (Image image in _crosshairImages)
                image.color = color;
        }

        private void Awake()
        {
            _crosshairImages = GetComponentsInChildren<Image>();
        }
    }
}