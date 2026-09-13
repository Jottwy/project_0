using System.IO;
using BackroomsSurvival.Gameplay.Body;
using NUnit.Framework;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-149 R2: el cuerpo por zonas del cliente tiene que decidir EXACTAMENTE igual que el del backend. Los dos lados leen
    /// <c>docs/data/body-zones.json</c>; si una cifra, un peso o el hash cambian en un solo lado, esta suite o la de Rust
    /// (<c>player::body::tests</c>) se ponen en rojo.
    /// </summary>
    public class BodyZonesOracleTests
    {
        [System.Serializable]
        private class Weights
        {
            public int[] fall;
            public int[] generic;
        }

        [System.Serializable]
        private class DrawCase
        {
            public string table;
            public long seed;
            public int zone;
        }

        [System.Serializable]
        private class InjuryCase
        {
            public float damage;
            public string cause;
            public int zone;
            public int injury;
        }

        [System.Serializable]
        private class Oracle
        {
            public int zone_count;
            public float min_wound_damage, cut_damage, fracture_fall_damage, fracture_blunt_damage;
            public float scratch_heal_seconds, bandaged_heal_seconds, splinted_fracture_heal_seconds;
            public float bleed_per_second, fracture_speed, splinted_fracture_speed;
            public Weights weights;
            public DrawCase[] draws;
            public InjuryCase[] injuries;
        }

        private static Oracle Load()
        {
            string path = Path.Combine(Application.dataPath, "..", "docs", "data", "body-zones.json");
            Assert.IsTrue(File.Exists(path), "falta docs/data/body-zones.json");
            return JsonUtility.FromJson<Oracle>(File.ReadAllText(path));
        }

        [Test]
        public void LasCifrasSonLasDelOraculo()
        {
            var o = Load();
            Assert.AreEqual(o.zone_count, BodyZones.Count);
            Assert.AreEqual(o.min_wound_damage, BodyState.MinWoundDamage);
            Assert.AreEqual(o.cut_damage, BodyState.CutDamage);
            Assert.AreEqual(o.fracture_fall_damage, BodyState.FractureFallDamage);
            Assert.AreEqual(o.fracture_blunt_damage, BodyState.FractureBluntDamage);
            Assert.AreEqual(o.scratch_heal_seconds, BodyState.ScratchHealSeconds);
            Assert.AreEqual(o.bandaged_heal_seconds, BodyState.BandagedHealSeconds);
            Assert.AreEqual(o.splinted_fracture_heal_seconds, BodyState.SplintedFractureHealSeconds);
            Assert.AreEqual(o.bleed_per_second, BodyState.BleedPerSecond, 1e-6f);
            Assert.AreEqual(o.fracture_speed, BodyState.FractureSpeed, 1e-6f);
            Assert.AreEqual(o.splinted_fracture_speed, BodyState.SplintedFractureSpeed, 1e-6f);
            CollectionAssert.AreEqual(o.weights.fall, System.Array.ConvertAll(BodyZoneResolver.WeightsFor(DamageType.Fall), w => (int)w));
            CollectionAssert.AreEqual(o.weights.generic, System.Array.ConvertAll(BodyZoneResolver.WeightsFor(DamageType.Undefined), w => (int)w));
        }

        [Test]
        public void ElSorteoYLasLesionesSonLasDelOraculo()
        {
            var o = Load();
            int checkedDraws = 0;
            foreach (var d in o.draws)
            {
                if (d.table == "phantom_hit") continue; // tabla solo del servidor
                var cause = d.table == "fall" ? DamageType.Fall : DamageType.Undefined;
                Assert.AreEqual(d.zone, (int)BodyZoneResolver.Draw(cause, (uint)d.seed), $"sorteo {d.table} semilla {d.seed}");
                checkedDraws++;
            }
            Assert.Greater(checkedDraws, 0);
            foreach (var c in o.injuries)
            {
                var type = System.Enum.TryParse(c.cause, out DamageType parsed) ? parsed : DamageType.Undefined;
                Assert.AreEqual(c.injury, (int)BodyState.InjuryFor(c.damage, type, (BodyZone)c.zone), $"lesión {c.damage} {c.cause} zona {c.zone}");
            }
        }
    }
}
