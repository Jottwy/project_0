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
        public void ElCofreSirveAguaDestornilladorYBote()
        {
            CollectionAssert.AreEqual(new[] { "Almond Water" }, Pool("ConsumablePool"),
                "exactamente un agua por cofre: cuenta medida contra los drenajes de sed");
            CollectionAssert.AreEquivalent(new[] { "Spray Can", "Screwdriver" }, Pool("MaterialPool"),
                "el destornillador entra en el cofre (2026-09-02) junto al bote; nada más");
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
