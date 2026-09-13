using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 R2a: el cliente copia el cuerpo que manda el backend en <c>body_state</c>.</summary>
    public class BodyStateMirrorTests
    {
        private static GameEventMsg Event(string type, object[] zones, bool bleeding, double legSpeed)
            => new GameEventMsg
            {
                eventType = type,
                data = new Dictionary<string, object> { { "zones", zones }, { "bleeding", bleeding }, { "leg_speed", legSpeed } },
            };

        [Test]
        public void ElEventoRellenaLasQuinceZonas()
        {
            var raw = new object[15];
            for (int i = 0; i < raw.Length; i++) raw[i] = 0L;
            raw[13] = 10L;
            raw[12] = 3L;
            var zones = new byte[BodyZones.Count];
            Assert.IsTrue(BodyStateMirror.TryRead(Event("body_state", raw, true, 0.55), zones, out bool bleeding, out float legSpeed));
            Assert.AreEqual(10, zones[13]);
            Assert.AreEqual(3, zones[12]);
            Assert.IsTrue(bleeding);
            Assert.AreEqual(0.55f, legSpeed, 1e-4f);

            Assert.IsFalse(BodyStateMirror.TryRead(Event("phantom_hit", raw, false, 1), zones, out _, out _), "otro evento no toca nada");
            Assert.IsFalse(BodyStateMirror.TryRead(new GameEventMsg { eventType = "body_state", data = null }, zones, out _, out _));
        }

        [Test]
        public void ElEspejoCopiaElEstadoYAvisaDeCadaCambio()
        {
            var body = new BodyState();
            var changed = new List<BodyZone>();
            body.Changed += changed.Add;
            var zones = new byte[BodyZones.Count];
            zones[13] = 10; // corte vendado
            zones[12] = 3;  // fractura
            body.ApplyRaw(zones);
            Assert.AreEqual(BodyInjury.Cut, body.InjuryOf(BodyZone.FootL));
            Assert.IsTrue(body.IsBandaged(BodyZone.FootL));
            Assert.AreEqual(BodyInjury.Fracture, body.InjuryOf(BodyZone.ShinR));
            CollectionAssert.AreEquivalent(new[] { BodyZone.FootL, BodyZone.ShinR }, changed);

            changed.Clear();
            body.ApplyRaw(zones);
            Assert.IsEmpty(changed, "lo mismo otra vez no avisa");
        }
    }
}
