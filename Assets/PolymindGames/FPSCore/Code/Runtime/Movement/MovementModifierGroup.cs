using System.Collections.Generic;

namespace PolymindGames.MovementSystem
{
    public sealed class MovementModifierGroup
    {
        public delegate float ModifierDelegate();

        private readonly List<ModifierDelegate> _modifiers;
        private readonly float _baseValue;

        public MovementModifierGroup()
        {
            _baseValue = 1f;
            _modifiers = new List<ModifierDelegate>(0);
        }

        public MovementModifierGroup(float baseValue)
        {
            _baseValue = baseValue;
            _modifiers = new List<ModifierDelegate>(1);
        }

        public MovementModifierGroup(float baseValue, MovementModifierGroup group)
        {
            _baseValue = baseValue;
            _modifiers = group?._modifiers ?? new List<ModifierDelegate>();
        }

        public float EvaluateValue()
        {
            float mod = _baseValue;

            foreach (var modifier in _modifiers)
                mod *= modifier.Invoke();

            return mod;
        }

        public void AddModifier(ModifierDelegate modifier)
        {
            if (modifier == null)
                return;

            if (!_modifiers.Contains(modifier))
                _modifiers.Add(modifier);
        }

        public void RemoveModifier(ModifierDelegate modifier)
        {
            if (modifier == null)
                return;

            _modifiers.Remove(modifier);
        }
    }
}