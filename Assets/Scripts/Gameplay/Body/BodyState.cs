using System;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>ADR-149 D3: tratamientos de la primera rebanada.</summary>
    public enum BodyTreatment : byte
    {
        Bandage = 1,
        Splint = 2,
    }

    /// <summary>
    /// ADR-149 D3-D4, rebanada R0: el estado del cuerpo, un byte por zona (lesión en 3 bits + flags) y un temporizador de
    /// curación. Objeto plano sin Unity para probarlo en EditMode. En R0 lo usa solo el prototipo local; en R2 pasa a ser
    /// espejo del backend. Cifras de maqueta, <c>TODO(balance)</c>.
    /// </summary>
    public sealed class BodyState
    {
        /// <summary>Debajo de esto el golpe duele y no deja marca (la misma cifra que la venda por brazo).</summary>
        public const float MinWoundDamage = 8f;
        public const float CutDamage = 15f;
        public const float FractureFallDamage = 25f;
        public const float FractureBluntDamage = 30f;

        public const float ScratchHealSeconds = 60f;
        public const float BandagedHealSeconds = 180f;
        public const float SplintedFractureHealSeconds = 600f;

        /// <summary>Salud por segundo que quita cada corte sin vendar.</summary>
        public const float BleedPerSecond = 0.15f;

        public const float FractureSpeed = 0.55f;
        public const float SplintedFractureSpeed = 0.8f;

        private const byte InjuryMask = 0b0000_0111;
        private const byte BandagedBit = 1 << 3;
        private const byte SplintedBit = 1 << 4;

        private readonly byte[] _zones = new byte[BodyZones.Count];
        private readonly float[] _heal = new float[BodyZones.Count];

        /// <summary>Zona cuyo byte acaba de cambiar.</summary>
        public event Action<BodyZone> Changed;

        public byte Raw(BodyZone zone) => _zones[(int)zone];

        public BodyInjury InjuryOf(BodyZone zone) => (BodyInjury)(_zones[(int)zone] & InjuryMask);

        public bool IsBandaged(BodyZone zone) => (_zones[(int)zone] & BandagedBit) != 0;

        public bool IsSplinted(BodyZone zone) => (_zones[(int)zone] & SplintedBit) != 0;

        public bool IsBleeding(BodyZone zone) => InjuryOf(zone) == BodyInjury.Cut && !IsBandaged(zone);

        /// <summary>La lesión que abre un golpe, pura.</summary>
        public static BodyInjury InjuryFor(float damage, DamageType type, BodyZone zone)
        {
            if (damage < MinWoundDamage || !BodyZoneResolver.OpensWound(type)) return BodyInjury.None;
            if (type == DamageType.Fall && BodyZones.IsLeg(zone) && damage >= FractureFallDamage) return BodyInjury.Fracture;
            if (type == DamageType.Blunt && BodyZones.IsLimb(zone) && damage >= FractureBluntDamage) return BodyInjury.Fracture;
            return damage >= CutDamage ? BodyInjury.Cut : BodyInjury.Scratch;
        }

        public static BodyTreatment TreatmentFor(BodyInjury injury)
            => injury == BodyInjury.Fracture ? BodyTreatment.Splint : BodyTreatment.Bandage;

        /// <summary>
        /// Aplica un golpe. Una lesión menor que la que ya hay no cambia nada; una igual o peor la sustituye y quita venda y
        /// férula (el golpe nuevo las abre). Devuelve la lesión abierta, o <see cref="BodyInjury.None"/>.
        /// </summary>
        public BodyInjury ApplyDamage(BodyZone zone, float damage, DamageType type)
        {
            var injury = InjuryFor(damage, type, zone);
            if (injury == BodyInjury.None || injury < InjuryOf(zone)) return BodyInjury.None;
            Set(zone, (byte)injury);
            return injury;
        }

        public bool CanTreat(BodyZone zone, BodyTreatment treatment)
        {
            var injury = InjuryOf(zone);
            return treatment switch
            {
                BodyTreatment.Bandage => (injury == BodyInjury.Scratch || injury == BodyInjury.Cut) && !IsBandaged(zone),
                BodyTreatment.Splint => injury == BodyInjury.Fracture && !IsSplinted(zone),
                _ => false,
            };
        }

        public bool Treat(BodyZone zone, BodyTreatment treatment)
        {
            if (!CanTreat(zone, treatment)) return false;
            byte bit = treatment == BodyTreatment.Splint ? SplintedBit : BandagedBit;
            Set(zone, (byte)(_zones[(int)zone] | bit));
            return true;
        }

        /// <summary>Avanza la curación. Devuelve la salud que se pierde por sangrado en este paso.</summary>
        public float Tick(float deltaTime)
        {
            float bleed = 0f;
            for (int i = 0; i < _zones.Length; i++)
            {
                var zone = (BodyZone)i;
                switch (InjuryOf(zone))
                {
                    case BodyInjury.Scratch:
                        Heal(i, IsBandaged(zone) ? BandagedHealSeconds : ScratchHealSeconds, deltaTime);
                        break;
                    case BodyInjury.Cut:
                        if (IsBandaged(zone)) Heal(i, BandagedHealSeconds, deltaTime);
                        else bleed += BleedPerSecond * deltaTime;
                        break;
                    case BodyInjury.Fracture:
                        if (IsSplinted(zone)) Heal(i, SplintedFractureHealSeconds, deltaTime);
                        break;
                }
            }
            return bleed;
        }

        /// <summary>ADR-149 D4: piernas y pies frenan. Manda la peor zona.</summary>
        public float LegSpeedMultiplier()
        {
            float speed = 1f;
            for (int i = (int)BodyZone.ThighL; i < _zones.Length; i++)
            {
                var zone = (BodyZone)i;
                if (InjuryOf(zone) == BodyInjury.Fracture)
                    speed = Mathf.Min(speed, IsSplinted(zone) ? SplintedFractureSpeed : FractureSpeed);
            }
            return speed;
        }

        /// <summary>ADR-149 R2a: copia los bytes que manda el backend (espejo). Avisa solo de las zonas que cambian.</summary>
        public void ApplyRaw(byte[] zones)
        {
            if (zones == null) return;
            for (int i = 0; i < _zones.Length && i < zones.Length; i++)
                Set((BodyZone)i, zones[i]);
        }

        public void Clear()
        {
            for (int i = 0; i < _zones.Length; i++)
                if (_zones[i] != 0) Set((BodyZone)i, 0);
        }

        private void Heal(int index, float seconds, float deltaTime)
        {
            _heal[index] += deltaTime;
            // Dos escrituras en el mismo frame no pasan: Set solo dispara si el byte cambia.
            if (_heal[index] >= seconds) Set((BodyZone)index, 0);
        }

        private void Set(BodyZone zone, byte value)
        {
            int i = (int)zone;
            _heal[i] = 0f;
            if (_zones[i] == value) return;
            _zones[i] = value;
            Changed?.Invoke(zone);
        }
    }
}
