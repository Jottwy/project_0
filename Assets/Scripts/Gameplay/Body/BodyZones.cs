using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// ADR-149 D1: las 15 zonas del cuerpo, índice estable de 4 bits. APPEND-ONLY: nunca se reordena ni se reutiliza un
    /// valor (el 15 queda reservado). Los 11 huesos con hitbox de los proxies más manos y pies, que son slots de equipo.
    /// </summary>
    public enum BodyZone : byte
    {
        Head = 0,
        Chest = 1,
        Abdomen = 2,
        UpperArmL = 3,
        UpperArmR = 4,
        ForearmL = 5,
        ForearmR = 6,
        HandL = 7,
        HandR = 8,
        ThighL = 9,
        ThighR = 10,
        ShinL = 11,
        ShinR = 12,
        FootL = 13,
        FootR = 14,
    }

    /// <summary>ADR-149 D3: lesión de una zona (3 bits). Ordinal estable; el orden es también la gravedad.</summary>
    public enum BodyInjury : byte
    {
        None = 0,
        Scratch = 1,
        Cut = 2,
        Fracture = 3,
    }

    public static class BodyZones
    {
        public const int Count = 15;

        private static readonly string[] Labels =
        {
            "Cabeza", "Pecho", "Abdomen", "Brazo izq.", "Brazo der.", "Antebrazo izq.", "Antebrazo der.", "Mano izq.", "Mano der.",
            "Muslo izq.", "Muslo der.", "Espinilla izq.", "Espinilla der.", "Pie izq.", "Pie der.",
        };

        public static string Label(BodyZone zone) => (int)zone < Labels.Length ? Labels[(int)zone] : zone.ToString();

        public static bool IsLeg(BodyZone zone) => zone >= BodyZone.ThighL;

        /// <summary>Brazos, manos y piernas: lo que se puede fracturar de un golpe.</summary>
        public static bool IsLimb(BodyZone zone) => zone >= BodyZone.UpperArmL;
    }

    /// <summary>
    /// ADR-149 D2: en qué zona cae un golpe, como función pura. Por orden de preferencia: (a) el hueso, cuando lo hay (PvP
    /// contra proxy); (b) la banda de altura y el lado sobre la cápsula del jugador, cuando hay punto de impacto; (c) un
    /// sorteo ponderado por causa, determinista con su semilla, cuando no hay punto (las caídas del vendor llegan sin él).
    /// R0: la tabla vive aquí con goldens en EditMode; el oráculo JSON común con Rust nace en R2.
    /// </summary>
    public static class BodyZoneResolver
    {
        /// <summary>Altura de la cápsula del jugador (<c>FPS_Player</c>: 1,7 m) sobre la que están medidas las bandas.</summary>
        public const float ReferenceHeight = 1.7f;

        // Pesos sobre 100, indexados por BodyZone. Caída: pies y espinillas; algo de muslos y manos (apoyarse al caer).
        private static readonly byte[] FallWeights = { 0, 0, 0, 0, 0, 0, 0, 5, 5, 5, 5, 20, 20, 20, 20 };
        // Sin causa ni punto: el tronco se lleva más, las puntas menos.
        private static readonly byte[] GenericWeights = { 8, 20, 16, 6, 6, 6, 6, 4, 4, 6, 6, 4, 4, 2, 2 };

        public static bool TryFromBone(string boneName, out BodyZone zone)
        {
            switch (boneName)
            {
                case "Head": zone = BodyZone.Head; return true;
                case "MiddleSpine": zone = BodyZone.Chest; return true;
                case "Pelvis": zone = BodyZone.Abdomen; return true;
                case "UpperArm.L": zone = BodyZone.UpperArmL; return true;
                case "UpperArm.R": zone = BodyZone.UpperArmR; return true;
                case "LowerArm.L": zone = BodyZone.ForearmL; return true;
                case "LowerArm.R": zone = BodyZone.ForearmR; return true;
                case "Hand.L": zone = BodyZone.HandL; return true;
                case "Hand.R": zone = BodyZone.HandR; return true;
                case "UpperLeg.L": zone = BodyZone.ThighL; return true;
                case "UpperLeg.R": zone = BodyZone.ThighR; return true;
                case "LowerLeg.L": zone = BodyZone.ShinL; return true;
                case "LowerLeg.R": zone = BodyZone.ShinR; return true;
                case "Foot.L": zone = BodyZone.FootL; return true;
                case "Foot.R": zone = BodyZone.FootR; return true;
                default: zone = BodyZone.Chest; return false;
            }
        }

        /// <summary>
        /// Punto en el espacio del jugador (pies en y = 0, +x a su derecha). Las bandas se escalan a la altura real.
        /// </summary>
        public static BodyZone FromLocalPoint(Vector3 local, float bodyHeight = ReferenceHeight)
        {
            float y = local.y * (ReferenceHeight / Mathf.Max(bodyHeight, 0.1f));
            bool left = local.x < 0f;
            float side = Mathf.Abs(local.x);
            if (y >= 1.50f) return BodyZone.Head;
            if (y >= 1.10f) return side > 0.18f ? Pick(left, BodyZone.UpperArmL, BodyZone.UpperArmR) : BodyZone.Chest;
            if (y >= 0.85f) return side > 0.20f ? Pick(left, BodyZone.ForearmL, BodyZone.ForearmR) : BodyZone.Abdomen;
            if (y >= 0.70f) return side > 0.20f ? Pick(left, BodyZone.HandL, BodyZone.HandR) : BodyZone.Abdomen;
            if (y >= 0.45f) return Pick(left, BodyZone.ThighL, BodyZone.ThighR);
            if (y >= 0.12f) return Pick(left, BodyZone.ShinL, BodyZone.ShinR);
            return Pick(left, BodyZone.FootL, BodyZone.FootR);
        }

        /// <summary>Sorteo por causa. Misma semilla, misma zona: se puede testear y repetir en el servidor.</summary>
        public static BodyZone Draw(DamageType cause, uint seed)
        {
            var weights = WeightsFor(cause);
            uint roll = Hash(seed) % 100u;
            for (int i = 0; i < weights.Length; i++)
            {
                if (roll < weights[i]) return (BodyZone)i;
                roll -= weights[i];
            }
            return BodyZone.Chest;
        }

        public static byte[] WeightsFor(DamageType cause) => cause == DamageType.Fall ? FallWeights : GenericWeights;

        /// <summary>Lo que no es un golpe (veneno, radiación) no abre herida en ninguna zona.</summary>
        public static bool OpensWound(DamageType type) => type != DamageType.Poison && type != DamageType.Radiation;

        private static BodyZone Pick(bool left, BodyZone l, BodyZone r) => left ? l : r;

        private static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }
    }
}
