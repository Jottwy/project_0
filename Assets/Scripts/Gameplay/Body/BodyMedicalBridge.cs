using BackroomsSurvival.Gameplay.Medical;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// ADR-149 R2c (D6): la venda por brazo (<see cref="PlayerMedicalState"/>, sus bits 7/8 y sus visuales) pasa a DERIVARSE del
    /// cuerpo por zonas. Un brazo está herido si alguna de sus zonas (brazo, antebrazo, mano) tiene un rasguño o un corte sin
    /// vendar, y vendado si alguna lo tiene vendado; una fractura no cuenta porque no se venda. Con
    /// <see cref="PlayerMedicalState.RestrictToLeftArm"/> (Alpha 1, validado por Joel) las heridas de los dos brazos se pintan y
    /// se tratan en el izquierdo, igual que antes.
    /// </summary>
    public static class BodyMedicalBridge
    {
        private static readonly BodyZone[] LeftArm = { BodyZone.ForearmL, BodyZone.UpperArmL, BodyZone.HandL };
        private static readonly BodyZone[] RightArm = { BodyZone.ForearmR, BodyZone.UpperArmR, BodyZone.HandR };
        private static readonly BodyZone[] BothArms =
            { BodyZone.ForearmL, BodyZone.UpperArmL, BodyZone.HandL, BodyZone.ForearmR, BodyZone.UpperArmR, BodyZone.HandR };

        private static BodyZone[] ZonesFor(BodyPartSide side, bool restrictToLeft)
            => restrictToLeft ? BothArms : side == BodyPartSide.Left ? LeftArm : RightArm;

        /// <summary>El estado de un brazo, puro.</summary>
        public static BodyPartCondition ArmCondition(BodyState body, BodyPartSide side, bool restrictToLeft)
        {
            if (restrictToLeft && side == BodyPartSide.Right) return BodyPartCondition.Healthy;
            bool open = false, bandaged = false;
            foreach (var zone in ZonesFor(side, restrictToLeft))
            {
                var injury = body.InjuryOf(zone);
                if (injury != BodyInjury.Scratch && injury != BodyInjury.Cut) continue;
                if (body.IsBandaged(zone)) bandaged = true;
                else open = true;
            }
            return open ? BodyPartCondition.Wounded : bandaged ? BodyPartCondition.Bandaged : BodyPartCondition.Healthy;
        }

        /// <summary>La zona que trata una venda en ese brazo: la abierta más grave (corte antes que rasguño).</summary>
        public static bool TryPickArmZone(BodyState body, BodyPartSide side, bool restrictToLeft, out BodyZone zone)
        {
            zone = BodyZone.ForearmL;
            if (restrictToLeft && side == BodyPartSide.Right) return false;
            bool found = false;
            foreach (var candidate in ZonesFor(side, restrictToLeft))
            {
                if (!body.CanTreat(candidate, BodyTreatment.Bandage)) continue;
                if (!found || body.InjuryOf(candidate) > body.InjuryOf(zone))
                {
                    zone = candidate;
                    found = true;
                }
            }
            return found;
        }

        public static void Sync(BodyState body, PlayerMedicalState medical)
        {
            medical.SetFromBody(BodyPartSide.Left, ArmCondition(body, BodyPartSide.Left, medical.RestrictToLeftArm));
            medical.SetFromBody(BodyPartSide.Right, ArmCondition(body, BodyPartSide.Right, medical.RestrictToLeftArm));
        }
    }
}
