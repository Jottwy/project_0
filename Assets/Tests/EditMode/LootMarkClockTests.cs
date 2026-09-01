using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-115 — la traducción del sello de mundo al reloj local. Es una resta, y es exactamente
    /// el sitio donde un error no se ve: el mundo simplemente regeneraría antes o después de lo
    /// que dice el ADR, sin un solo error en el log.
    /// </summary>
    [TestFixture]
    public class LootMarkClockTests
    {
        private const float CarryableRespawnSeconds = 1800f; // el de ChunkLootManager, sin tocar

        [Test]
        public void Una_marca_recien_puesta_queda_sellada_ahora()
        {
            float stamp = LootMarkClock.StampFromWorldTime(localNow: 12.5f, worldNow: 4000, takenAt: 4000);
            Assert.That(stamp, Is.EqualTo(12.5f).Within(0.001f));
        }

        /// El caso del ADR: se guarda, se sale, se vuelve. El sello no puede llegar como "ahora",
        /// o cada relog reiniciaría la cuenta y el punto no volvería nunca.
        [Test]
        public void Una_marca_vieja_queda_sellada_en_el_pasado()
        {
            // 40 min de mundo transcurridos entre la toma y la carga.
            float stamp = LootMarkClock.StampFromWorldTime(localNow: 5f, worldNow: 6000, takenAt: 3600);
            Assert.That(stamp, Is.EqualTo(5f - 2400f).Within(0.001f));
        }

        /// Las dos cadencias que ADR-115 D13 mantiene SEPARADAS, medidas sobre el mismo sello: a
        /// los 40 min de mundo el material ya ha vuelto (30 min) y el item todavía no (2 h).
        [Test]
        public void A_los_cuarenta_minutos_el_material_ya_volvio_y_el_item_no()
        {
            float now = 5f;
            float stamp = LootMarkClock.StampFromWorldTime(now, worldNow: 6000, takenAt: 3600);
            float elapsed = now - stamp;

            Assert.GreaterOrEqual(elapsed, CarryableRespawnSeconds, "40 min pasan de los 30 del material");
            Assert.Less(elapsed, LootMarkClock.TtlSeconds, "pero no llegan a las 2 h del item");
        }

        /// Un sello por delante del reloj de mundo no debería existir (el reloj sólo avanza), pero
        /// si llega no puede caducar sola ni tirar nada: el punto sigue saqueado.
        [Test]
        public void Un_sello_del_futuro_no_caduca_por_las_bravas()
        {
            float now = 100f;
            float stamp = LootMarkClock.StampFromWorldTime(now, worldNow: 1000, takenAt: 1500);
            Assert.Greater(stamp, now, "queda por delante del reloj local");
            Assert.Less(now - stamp, LootMarkClock.TtlSeconds, "y por tanto NO está caducada");
        }

        /// El espejo del número del backend. No lo comprueba nadie más, y separarlos no da error:
        /// sólo hace que la cadencia deje de ser la del ADR.
        [Test]
        public void El_ttl_del_cliente_es_el_del_backend()
        {
            Assert.That(LootMarkClock.TtlSeconds, Is.EqualTo(7200f).Within(0.0001f));
        }
    }
}
