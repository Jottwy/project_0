namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Las cuatro anclas de la fase 1 de ADR-103, en el orden de la escalera de u.</summary>
    public enum Wg3LevelAnchor : byte
    {
        /// <summary>Level 0 «Threshold» — el mundo de hoy. Todos los factores a 1.0, EXACTAMENTE.</summary>
        Threshold = 0,
        /// <summary>0.1 «Zenith Station» — amplio y alto.</summary>
        ZenithStation = 1,
        /// <summary>0.2 «Remodeled Mess» — irregular, roto.</summary>
        RemodeledMess = 2,
        /// <summary>0.3 «The Icy Rooms» — apretado y bajo.</summary>
        IcyRooms = 3,
    }

    /// <summary>El perfil de perillas (ADR-103 D1): factores sobre constantes que ya existen en el
    /// plan del servidor. El cliente NO aplica geometría con esto — la asimetría que hace seguro el
    /// espejo (D8): el servidor usa el campo para GEOMETRÍA, el cliente sólo para ATMÓSFERA. Una
    /// deriva entre espejos tiñe mal una celda de frontera; nunca cierra un pasillo.</summary>
    public struct Wg3LevelProfile
    {
        public float TargetAreaMul;
        public float WeirdSpreadMul;
        public float VoidChanceMul;
        public float ClearHeightMul;
        public float BandWidthMul;
        public float MaxDepthDelta;
    }

    /// <summary>Lo que devuelve el campo (D2): DOS anclas y un peso, no un perfil. Celda pura =
    /// misma ancla a los dos lados y peso 0.</summary>
    public struct Wg3LevelMix
    {
        public Wg3LevelAnchor Lower;
        public Wg3LevelAnchor Upper;
        /// <summary>En [0,1): 0 = todo Lower, hacia 1 = todo Upper.</summary>
        public float TowardUpper;

        public bool IsPure => Lower == Upper;

        /// <summary>Perilla a perilla, la media ponderada de las dos anclas.</summary>
        public Wg3LevelProfile Profile()
        {
            Wg3LevelProfile a = Wg3Identity.AnchorProfile(Lower);
            Wg3LevelProfile b = Wg3Identity.AnchorProfile(Upper);
            float w = TowardUpper;
            return new Wg3LevelProfile
            {
                TargetAreaMul = Blend(a.TargetAreaMul, b.TargetAreaMul, w),
                WeirdSpreadMul = Blend(a.WeirdSpreadMul, b.WeirdSpreadMul, w),
                VoidChanceMul = Blend(a.VoidChanceMul, b.VoidChanceMul, w),
                ClearHeightMul = Blend(a.ClearHeightMul, b.ClearHeightMul, w),
                BandWidthMul = Blend(a.BandWidthMul, b.BandWidthMul, w),
                MaxDepthDelta = Blend(a.MaxDepthDelta, b.MaxDepthDelta, w),
            };
        }

        /// <summary>En float y en este orden, para dar los mismos bits que `identity.rs`.</summary>
        private static float Blend(float a, float b, float w) => a + (b - a) * w;
    }

    /// <summary>
    /// ADR-103 — la identidad de nivel: un campo de MEZCLA de perfiles, no un nivel por sitio.
    ///
    /// Espejo EXACTO de `identity.rs` en el backend (D8: el cliente RECALCULA la identidad con el
    /// world_seed del HandshakeAck, cero wire). La mezcla es por CELDA de 900 m, nunca por metro:
    /// dentro de la celda es constante y la frontera es un escalón duro (D3), la misma premisa que
    /// Wg3ScaleField. La `y` está en la firma y va clavada a 0 hasta la fase 2 (D7).
    ///
    /// La semilla es la GLOBAL del compositor (los 32 bits bajos del world_seed), la misma que
    /// sortea las puertas de junta — nunca la de una región: la celda cubre 6×6 regiones y todas
    /// tienen que leer lo mismo.
    /// </summary>
    public static class Wg3Identity
    {
        /// <summary>Lado de la celda de identidad, en regiones. MÚLTIPLO EXACTO de la región (D4):
        /// una región nunca cae a caballo de dos celdas.</summary>
        public const int IdentityCellRegions = 6;

        /// <summary>Lado de región en metros — espejo de `world::REGION_M` (3 chunks de 50 m).</summary>
        public const float RegionM = 150f;

        /// <summary>Lado de la celda de identidad en metros: 6 × 150 = 900.</summary>
        public const float IdentityCellM = IdentityCellRegions * RegionM;

        /// <summary>D2 — probabilidad de que la celda encaje en el ancla más próxima y salga pura.
        /// Sin polos puros todo el mundo sería mezcla: papilla gris.</summary>
        public const float PurityChance = 0.45f;

        private const uint SaltIdentityU = 0x1DE17000u;
        private const uint SaltIdentityPurity = 0x1DE17001u;

        /// <summary>Posición de cada ancla en la escalera de u (D2).</summary>
        private static readonly float[] AnchorU = { 0f, 0.33f, 0.66f, 1f };

        /// <summary>Perfil de cada ancla (D5). Threshold todo a 1.0 EXACTAMENTE: es la guardia de
        /// regresión — una celda pura de Level 0 es el mundo servido de hoy byte a byte.</summary>
        private static readonly Wg3LevelProfile[] AnchorProfiles =
        {
            new Wg3LevelProfile { TargetAreaMul = 1f, WeirdSpreadMul = 1f, VoidChanceMul = 1f,
                ClearHeightMul = 1f, BandWidthMul = 1f, MaxDepthDelta = 0f },
            new Wg3LevelProfile { TargetAreaMul = 1.9f, WeirdSpreadMul = 1f, VoidChanceMul = 0.4f,
                ClearHeightMul = 1.35f, BandWidthMul = 1.25f, MaxDepthDelta = 0f },
            new Wg3LevelProfile { TargetAreaMul = 0.8f, WeirdSpreadMul = 1.8f, VoidChanceMul = 2.2f,
                ClearHeightMul = 1f, BandWidthMul = 1f, MaxDepthDelta = 2f },
            new Wg3LevelProfile { TargetAreaMul = 0.45f, WeirdSpreadMul = 1f, VoidChanceMul = 0.3f,
                ClearHeightMul = 0.8f, BandWidthMul = 1f, MaxDepthDelta = 0f },
        };

        public static Wg3LevelProfile AnchorProfile(Wg3LevelAnchor anchor)
            => AnchorProfiles[(int)anchor];

        /// <summary>La identidad en un punto del mundo. `worldSeed` es la semilla del compositor
        /// (int), la misma que recibe Wg3ScaleField. La `y` se ignora en fase 1 (D7).</summary>
        public static Wg3LevelMix At(int worldSeed, float x, float y, float z)
        {
            int cx = FloorDiv(x, IdentityCellM);
            int cz = FloorDiv(z, IdentityCellM);
            float u = Wg3Hash.ToUnit(Wg3Hash.Mix(worldSeed, cx, cz, unchecked((int)SaltIdentityU)));
            float purity = Wg3Hash.ToUnit(
                Wg3Hash.Mix(worldSeed, cx, cz, unchecked((int)SaltIdentityPurity)));

            if (purity < PurityChance)
            {
                Wg3LevelAnchor a = NearestAnchor(u);
                return new Wg3LevelMix { Lower = a, Upper = a, TowardUpper = 0f };
            }

            // Escalera de anclas: u da a la vez qué dos son vecinas y en qué proporción se mezclan.
            int i = 0;
            while (i + 2 < AnchorU.Length && u >= AnchorU[i + 1]) i++;
            return new Wg3LevelMix
            {
                Lower = (Wg3LevelAnchor)i,
                Upper = (Wg3LevelAnchor)(i + 1),
                TowardUpper = (u - AnchorU[i]) / (AnchorU[i + 1] - AnchorU[i]),
            };
        }

        /// <summary>D4 — la identidad de una región (coordenada de región, no de chunk), resuelta
        /// UNA VEZ en su centro. Cualquier punto de la región daría lo mismo; el centro es la
        /// consulta canónica.</summary>
        public static Wg3LevelMix ForRegion(int worldSeed, int regionX, int regionZ)
        {
            return At(worldSeed,
                regionX * RegionM + RegionM * 0.5f,
                0f,
                regionZ * RegionM + RegionM * 0.5f);
        }

        /// <summary>El ancla más próxima. Empates hacia la de índice menor, con `&lt;` estricto,
        /// para que el espejo Rust no pueda discrepar ni en el empate.</summary>
        private static Wg3LevelAnchor NearestAnchor(float u)
        {
            int best = 0;
            float bestD = System.Math.Abs(u - AnchorU[0]);
            for (int i = 1; i < AnchorU.Length; i++)
            {
                float d = System.Math.Abs(u - AnchorU[i]);
                if (d < bestD)
                {
                    best = i;
                    bestD = d;
                }
            }
            return (Wg3LevelAnchor)best;
        }

        /// <summary>División con suelo, copiada de Wg3ScaleField letra a letra: `(int)(v / size)`
        /// trunca hacia cero y espejaría el campo en el origen.</summary>
        private static int FloorDiv(float v, float size)
        {
            float q = v / size;
            int i = (int)q;
            return (q < 0f && q != i) ? i - 1 : i;
        }
    }
}
