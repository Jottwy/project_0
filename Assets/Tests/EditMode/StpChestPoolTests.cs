using System.Reflection;
using NUnit.Framework;
using PolymindGames.InventorySystem;
using BackroomsSurvival.Net;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Las pools del cofre son privadas y estáticas a propósito (el recorte de escasez vive en
    /// ellas). Se leen por reflexión para fijar el catálogo: cada nombre tiene que resolver en la
    /// base de definiciones, o el cofre avisa y sirve un hueco. Pide el editor (AssetDatabase
    /// detrás de `ItemDefinition.Definitions`).
    /// </summary>
    public class StpChestPoolTests
    {
        private static string[] Pool(string field) => Pool(typeof(StpChestSpawner), field);

        private static string[] Pool(System.Type owner, string field)
        {
            var f = owner.GetField(field, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(f, $"{owner.Name}.{field} ya no existe con ese nombre");
            return (string[])f.GetValue(null);
        }

        [Test]
        public void ElCofreSirveAguaDestornilladorBoteYLinterna()
        {
            CollectionAssert.AreEqual(new[] { "Almond Water" }, Pool("ConsumablePool"),
                "exactamente un agua por cofre: cuenta medida contra los drenajes de sed");
            CollectionAssert.AreEquivalent(new[] { "Spray Can", "Screwdriver", "Crank Flashlight" }, Pool("MaterialPool"),
                "destornillador (2026-09-02) y linterna (2026-09-12) entran en el cofre junto al bote; nada más");
        }

        [Test]
        public void LaLinternaNoSaleDelSueloMientrasDureElRecorte()
        {
            // ADR-133 la dejó en la pool del suelo detrás de RestrictCacheCatalog; la decisión de
            // Joel (2026-09-12) es que salga de los cofres SIN levantar el recorte de escasez.
            CollectionAssert.DoesNotContain(Pool(typeof(ChunkLootRoll), "RestrictedCachePool"), "Crank Flashlight");
            var gate = typeof(ChunkLootRoll).GetField("RestrictCacheCatalog", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(gate, "ChunkLootRoll.RestrictCacheCatalog ya no existe con ese nombre");
            Assert.IsTrue((bool)gate.GetValue(null), "el recorte de escasez del suelo sigue en pie: la linterna no lo levanta");
        }

        [Test]
        public void TodoLoQueSirveElCofreResuelve()
        {
            foreach (string field in new[] { "ConsumablePool", "MaterialPool" })
            {
                foreach (string name in Pool(field))
                {
                    var def = ItemDefinition.GetWithName(name);
                    Assert.IsNotNull(def, $"'{name}' no está en la base de definiciones: el cofre lo saltaría con aviso");
                    Assert.IsNotNull(def.Pickup, $"'{name}' sin pickup no aparece al soltarlo del cofre");
                }
            }
        }

        [Test]
        public void ElDestornilladorNoSaleDelSuelo()
        {
            // Paridad cofre/mundo rota a propósito: herramienta sólo en cofres, como el agua.
            CollectionAssert.DoesNotContain(Pool(typeof(ChunkLootRoll), "MaterialPool"), "Screwdriver");
        }
    }
}
