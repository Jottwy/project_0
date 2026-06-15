using UnityEngine.UI;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/user-interface/behaviours/ui_vitals#player-vitals-ui")]
    public sealed class PlayerStatsUI : CharacterUIBehaviour
    {
        [SerializeField, SubGroup]
        private VitalUI _thirstUI;

        [SerializeField, SubGroup]
        private VitalUI _hungerUI;

        private IHungerManagerCC _hunger;
        private IThirstManagerCC _thirst;

        protected override void Awake()
        {
            base.Awake();
            enabled = false;
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            _thirst = character.GetCC<IThirstManagerCC>();
            _hunger = character.GetCC<IHungerManagerCC>();
            enabled = true;
        }

        protected override void OnCharacterDetached(ICharacter character) => enabled = false;

        private void FixedUpdate()
        {
            _thirstUI.Update(_thirst.Thirst, _thirst.MaxThirst);
            _hungerUI.Update(_hunger.Hunger, _hunger.MaxHunger);
        }

        [Serializable]
        private sealed class VitalUI
        {
            [SerializeField]
            private Image _bar;

            private float _value;
            
            public void Update(float newValue, float maxValue)
            {
                if (Mathf.Abs(_value - newValue) > 0.01f)
                {
                    _value = newValue;
                    _bar.fillAmount = newValue / maxValue;
                }
            }
        }
    }
}