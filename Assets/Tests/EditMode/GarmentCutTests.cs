using BackroomsSurvival.Gameplay.Body;
using NUnit.Framework;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 R4b (Joel: «adaptado a corte, bala, etc.»): cada rotura recuerda qué la hizo y el shader le da su forma.</summary>
    public class GarmentCutTests
    {
        [Test]
        public void CadaCausaDejaSuCorte()
        {
            Assert.AreEqual(GarmentCut.Bullet, GarmentState.CutFor(DamageType.Ballistic));
            Assert.AreEqual(GarmentCut.Stab, GarmentState.CutFor(DamageType.Pierce));
            Assert.AreEqual(GarmentCut.Slash, GarmentState.CutFor(DamageType.Slash));
            Assert.AreEqual(GarmentCut.None, GarmentState.CutFor(DamageType.Blunt));

            var zone = new GarmentZone(BodyZone.Chest, 0.2f);
            var state = new GarmentState();
            state.ApplyHit(0, zone, DamageType.Ballistic, 8f, out _);
            Assert.AreEqual(GarmentCut.Bullet, state.CutOf(0));
            Assert.AreEqual((float)GarmentDamage.Cut + 8f * (float)GarmentCut.Bullet, state.VisualCode(0));

            state.ApplyHit(0, zone, DamageType.Pierce, 8f, out _);
            Assert.AreEqual(GarmentCut.Bullet, state.CutOf(0), "un corte igual de grave no cambia la forma que ya había");

            state.ApplyHit(0, zone, DamageType.Slash, 30f, out _);
            Assert.AreEqual(GarmentDamage.Torn, state.DamageOf(0));
            Assert.AreEqual(GarmentCut.Slash, state.CutOf(0), "el zarpazo que desgarra manda");

            state.Repair(0, GarmentRepair.Tape);
            Assert.AreEqual(GarmentCut.Slash, state.CutOf(0), "la cinta tapa la forma del desgarro: se recuerda");
            Assert.AreEqual((float)GarmentDamage.Patched + 8f * (float)GarmentCut.Slash, state.VisualCode(0));
        }

        [Test]
        public void ElCorteViajaEnSuPropiaPropiedad()
        {
            var state = new GarmentState();
            var zones = new[] { new GarmentZone(BodyZone.Chest, 0.2f), new GarmentZone(BodyZone.Abdomen, 0.2f), new GarmentZone(BodyZone.ForearmL, 0.2f) };
            state.ApplyHit(0, zones[0], DamageType.Ballistic, 8f, out _);
            state.ApplyHit(2, zones[2], DamageType.Pierce, 8f, out _);
            uint damage = state.Pack();
            uint cuts = state.PackCuts();

            var copy = new GarmentState();
            copy.Unpack(damage);
            copy.UnpackCuts(cuts);
            Assert.AreEqual(GarmentCut.Bullet, copy.CutOf(0));
            Assert.AreEqual(GarmentCut.None, copy.CutOf(1));
            Assert.AreEqual(GarmentCut.Stab, copy.CutOf(2));
            Assert.AreEqual(state.Pack(), copy.Pack(), "el daño no cambia de formato: los guardados viejos siguen valiendo");
            Assert.AreEqual((double)cuts, (double)(uint)(double)cuts);
        }

        [Test]
        public void LasCoordenadasDeUnaZonaVanEnMetros()
        {
            // Un cilindro de 5 cm de radio y 30 cm de largo, tumbado en X (un brazo en cruz), a la derecha del cuerpo.
            const int ring = 16, rows = 4;
            var positions = new Vector3[ring * rows + 1];
            var zones = new BodyZone[positions.Length];
            for (int r = 0; r < rows; r++)
            for (int i = 0; i < ring; i++)
            {
                float a = i * Mathf.PI * 2f / ring;
                positions[r * ring + i] = new Vector3(0.3f + r * 0.1f, 1.4f + 0.05f * Mathf.Sin(a), 0.05f * Mathf.Cos(a));
                zones[r * ring + i] = BodyZone.UpperArmR;
            }
            positions[^1] = new Vector3(0f, 1f, 0f);
            zones[^1] = BodyZone.Chest;

            var coords = new Vector2[positions.Length];
            GarmentVisualState.ProjectZones(positions, zones, Vector3.up, Vector3.forward, coords);

            // El vértice del frente (z = +radio) está en u = 0; la vuelta entera mide 2·π·r.
            var front = coords[1 * ring + 0];
            Assert.AreEqual(0f, front.x, 1e-3f, "el frente del brazo es u = 0");
            var side = coords[1 * ring + ring / 4];
            Assert.AreEqual(Mathf.PI * 0.5f * 0.05f, Mathf.Abs(side.x), 2e-3f, "un cuarto de vuelta son π·r/2 metros");
            Assert.AreEqual(0.1f, Mathf.Abs(coords[2 * ring].y - coords[1 * ring].y), 1e-3f, "a lo largo, metros");
            Assert.Greater(coords[3 * ring].y, coords[0].y, "el eje de un miembro apunta hacia fuera del cuerpo");
        }
    }
}
