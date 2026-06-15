using PolymindGames.WieldableSystem;
using UnityEngine.UI;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/user-interface/behaviours/ui_wieldables#charge")]
    public sealed class CrosshairChargeUI : CharacterUIBehaviour
    {
        [SerializeField]
        [Tooltip("A gradient used in determining the color of the charge image relative to the current charge value.")]
        private Gradient _fillGradient;

        [SerializeField, ReorderableList(ListStyle.Lined, HasLabels = false)]
        [Tooltip("UI images that will have their fill amount value set to the current charge value.")]
        private Image[] _chargeFillImages;

        private IFirearm _firearm;
        private float _charge = -1f;

        public void SetCharge(float charge)
        {
            if (Math.Abs(_charge - charge) > 0.01f)
                UpdateImages(charge);
            _charge = charge;
        }

        private void UpdateImages(float fillAmount)
        {
            Color chargeColor = _fillGradient.Evaluate(fillAmount);
            foreach (var image in _chargeFillImages)
            {
                image.fillAmount = fillAmount;
                image.color = chargeColor;
            }
        }
    }
}