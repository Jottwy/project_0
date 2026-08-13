using BackroomsSurvival.Gameplay.Audio;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Contrato del reverb por zona: qué sala se elige para un <c>zone_kind</c>.
    ///
    /// Solo se ejercita el RESOLVEDOR, que es la parte con reglas. Escribir en el
    /// AudioMixer es Play-only y además depende de que el asset tenga el efecto SFX Reverb
    /// con los cinco parámetros expuestos — <see cref="ReverbMixerDriver.MixerIsAuthored"/>
    /// existe justo para poder comprobar eso en juego, no aquí.
    ///
    /// Lo que estos tests protegen es la propiedad que hace el sistema inofensivo hasta que
    /// alguien lo autore: una capa recién creada suena MUDA, así que añadir el reverb no
    /// cambió cómo suena ninguna zona existente.
    /// </summary>
    [TestFixture]
    public class ZoneReverbTests
    {
        private const int ZoneNormal   = 0;
        private const int ZoneOpenHall = 4;
        private const int ZoneUnknown  = -1;

        private static LayerVisualConfig NewLayer() =>
            ScriptableObject.CreateInstance<LayerVisualConfig>();

        private static ZoneAmbienceSet Hall(int zoneKind) => new ZoneAmbienceSet
        {
            zoneKind       = zoneKind,
            overrideReverb = true,
            reverbDecay    = 2.6f,
            reverbRoom     = -800f,
            reverbRoomHF   = -400f,
            reverbLevel    = 200f,
        };

        [Test]
        public void UnaCapaReciénCreadaEstáMuda()
        {
            var cfg = NewLayer();
            var t = cfg.ReverbFor(ZoneNormal);

            Assert.AreEqual(ReverbMixerDriver.RoomSilent, t.room, 0.001f,
                "el default tiene que ser silencio real, no un reverb tenue: añadir el " +
                "sistema no puede cambiar cómo suena una zona que nadie ha autorado");
            Object.DestroyImmediate(cfg);
        }

        [Test]
        public void SinOverrideDeZonaMandaLaCapa()
        {
            var cfg = NewLayer();
            cfg.reverbRoom   = -2000f;
            cfg.reverbDecay  = 1.2f;
            cfg.reverbRoomHF = -1500f;
            cfg.reverbLevel  = 100f;
            // Un set que autora niebla pero NO reverb no debe robar la sala de la capa.
            cfg.zoneAmbienceSets = new[]
            {
                new ZoneAmbienceSet { zoneKind = ZoneNormal, overrideFogDensity = true, fogDensity = 0.03f },
            };

            var t = cfg.ReverbFor(ZoneNormal);

            Assert.AreEqual(-2000f, t.room,   0.001f);
            Assert.AreEqual(1.2f,   t.decay,  0.001f);
            Assert.AreEqual(-1500f, t.roomHF, 0.001f);
            Assert.AreEqual(100f,   t.level,  0.001f);
            Object.DestroyImmediate(cfg);
        }

        [Test]
        public void ElOverrideDeZonaGanaSobreLaCapa()
        {
            var cfg = NewLayer();
            cfg.reverbRoom  = -2000f;
            cfg.reverbDecay = 1.2f;
            cfg.zoneAmbienceSets = new[] { Hall(ZoneOpenHall) };

            var t = cfg.ReverbFor(ZoneOpenHall);

            Assert.AreEqual(-800f, t.room,   0.001f, "la nave impone su presencia");
            Assert.AreEqual(2.6f,  t.decay,  0.001f, "y su cola larga");
            Assert.AreEqual(-400f, t.roomHF, 0.001f);
            Assert.AreEqual(200f,  t.level,  0.001f);
            Object.DestroyImmediate(cfg);
        }

        [Test]
        public void UnaZonaSinSetPropioNoHeredaLaDeOtra()
        {
            // El fallo que este test existe para atrapar: que un pasillo normal suene a nave
            // porque OPEN_HALL fue la última zona autorada de la lista.
            var cfg = NewLayer();
            cfg.reverbRoom  = -2000f;
            cfg.reverbDecay = 1.2f;
            cfg.zoneAmbienceSets = new[] { Hall(ZoneOpenHall) };

            var t = cfg.ReverbFor(ZoneNormal);

            Assert.AreEqual(-2000f, t.room,  0.001f, "NORMAL se queda con la sala de la capa");
            Assert.AreEqual(1.2f,   t.decay, 0.001f);
            Object.DestroyImmediate(cfg);
        }

        [Test]
        public void ZonaDesconocidaCaeALaCapaPeroUnComodínSíAplica()
        {
            // −1 = ZoneRegistry todavía no ha contestado. Misma degradación que tinte, luz y
            // props: no casa con un set específico, pero sí con uno marcado anyZoneKind.
            var cfg = NewLayer();
            cfg.reverbRoom = -2000f;
            cfg.zoneAmbienceSets = new[] { Hall(ZoneOpenHall) };
            Assert.AreEqual(-2000f, cfg.ReverbFor(ZoneUnknown).room, 0.001f);

            var wild = Hall(ZoneOpenHall);
            wild.anyZoneKind = true;
            cfg.zoneAmbienceSets = new[] { wild };
            Assert.AreEqual(-800f, cfg.ReverbFor(ZoneUnknown).room, 0.001f);
            Object.DestroyImmediate(cfg);
        }

        [Test]
        public void UnaZonaPuedeAutorarseExplícitamenteMuda()
        {
            // −10000 es un valor legítimo, no "sin cambio": un hueco tan pequeño o tan
            // absorbente que no devuelve nada. Por eso el override es un booleano y no un
            // centinela sobre el propio dB.
            var cfg = NewLayer();
            cfg.reverbRoom = -800f; // la capa SÍ tiene sala
            var mute = Hall(ZoneNormal);
            mute.reverbRoom = ReverbMixerDriver.RoomSilent;
            cfg.zoneAmbienceSets = new[] { mute };

            Assert.AreEqual(ReverbMixerDriver.RoomSilent, cfg.ReverbFor(ZoneNormal).room, 0.001f);
            Object.DestroyImmediate(cfg);
        }

        [Test]
        public void ElSetDeReverbCuentaComoAutoríaParaCapturarLaZona()
        {
            // HasAnyOverride decide si un set captura la zona. Si el reverb no contara, un
            // set que SOLO describe la sala se saltaría en silencio.
            var onlyReverb = new ZoneAmbienceSet { zoneKind = ZoneNormal, overrideReverb = true };
            Assert.IsTrue(onlyReverb.HasAnyOverride);

            var empty = new ZoneAmbienceSet { zoneKind = ZoneNormal };
            Assert.IsFalse(empty.HasAnyOverride, "un set sin nada marcado sigue sin capturar");
        }
    }
}
