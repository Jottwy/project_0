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
            zoneKind         = zoneKind,
            overrideReverb   = true,
            reverbDecay      = 2.6f,
            reverbRoom       = -800f,
            reverbRoomHF     = -400f,
            reverbLevel      = 200f,
            reverbReflect    = -1800f,
            reverbWallMetres = 9.5f,
            reverbDry        = 0f,
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
        public void ElRetardoDelPrimerReboteSaleDeLaDistanciaALaPared()
        {
            // Autorar en metros y no en milisegundos es la mitad de por qué esto se puede
            // ajustar de oído. Ida y vuelta a 343 m/s: 2,5 m ⇒ 5 m de recorrido ⇒ ~14,6 ms.
            Assert.AreEqual(0.01458f, ReverbMixerDriver.ReflectDelayForMetres(2.5f), 0.0002f);
            Assert.AreEqual(0f,       ReverbMixerDriver.ReflectDelayForMetres(0f),   1e-6f);
            // El SFX Reverb corta el Reflect Delay en 0,3 s: una distancia absurda no puede
            // producir un valor que el efecto rechace en silencio.
            Assert.AreEqual(0.3f, ReverbMixerDriver.ReflectDelayForMetres(9999f), 1e-6f);
        }

        [Test]
        public void LaSalaDeLaZonaSeAdoptaEnteraIncluidasLasReflexiones()
        {
            // El override es TODO-O-NADA: mezclar la cola de una zona con las reflexiones de
            // otra daría un sitio que no existe. Este test fija esa unidad.
            var cfg = NewLayer();
            cfg.reverbRoom        = -2000f;
            cfg.reverbReflect     = -700f;   // la capa SÍ tiene paredes
            cfg.reverbWallMetres  = 2.5f;
            cfg.reverbDry         = 0f;
            cfg.zoneAmbienceSets  = new[] { Hall(ZoneOpenHall) };

            var t = cfg.ReverbFor(ZoneOpenHall);

            Assert.AreEqual(-1800f, t.reflect, 0.001f, "la nave impone SUS reflexiones");
            Assert.AreEqual(ReverbMixerDriver.ReflectDelayForMetres(9.5f), t.reflectDelay, 1e-5f,
                "y su distancia a la pared, no la de la capa");
            Object.DestroyImmediate(cfg);
        }

        [Test]
        public void UnaZonaPuedeQuedarseSinSuperficies()
        {
            // reverbReflect a −10000 = sin rebotes tempranos: solo cola difusa, que se
            // percibe como VACÍO y no como habitación. NO es un fallo — es lo que se quiere
            // en BLACKOUT y en el PIT, y está validado en juego. El test existe para que
            // nadie lo "arregle" subiéndolo al retocar valores.
            var cfg = NewLayer();
            var voidZone = Hall(ZoneNormal);
            voidZone.reverbReflect = ReverbMixerDriver.RoomSilent;
            voidZone.reverbDry     = -400f;   // hundido dentro del reverb, no rodeado por él
            cfg.zoneAmbienceSets   = new[] { voidZone };

            var t = cfg.ReverbFor(ZoneNormal);

            Assert.AreEqual(ReverbMixerDriver.RoomSilent, t.reflect, 0.001f);
            Assert.AreEqual(-400f, t.dry, 0.001f);
            Assert.AreNotEqual(ReverbMixerDriver.RoomSilent, t.room,
                "sin reflexiones pero CON cola: el vacío tiene que seguir sonando");
            Object.DestroyImmediate(cfg);
        }

        [Test]
        public void UnaCapaReciénCreadaTampocoTieneSuperficies()
        {
            var cfg = NewLayer();
            var t = cfg.ReverbFor(ZoneNormal);
            Assert.AreEqual(ReverbMixerDriver.RoomSilent, t.reflect, 0.001f);
            Assert.AreEqual(0f, t.dry, 0.001f, "el seco pasa entero mientras nadie diga otra cosa");
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
