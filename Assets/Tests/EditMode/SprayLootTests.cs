using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-068 S4 — el bote como objeto del mundo: que aparece en el loot y que la definición que
    /// las tablas nombran existe y sirve para lo que se espera.
    ///
    /// Los dos modos de fallo que esto cierra son SILENCIOSOS, y los dos ya han mordido en este
    /// proyecto: un nombre en la pool sin su `ItemDefinition` hace que `ChunkLootManager`
    /// descarte el slot con un warning, y una definición sin la etiqueta `Wieldable` produce un
    /// item que se recoge, se ve en el inventario y no se deja empuñar (pasó exactamente eso el
    /// 2026-08-13).
    /// </summary>
    [TestFixture]
    public class SprayLootTests
    {
        private const string ItemName = "Spray Can";
        private const long Seed = 987654321L;

        private static ZoneLootProfile DefaultProfile => ZoneLootProfile.Default;

        /// <summary>
        /// El bote sale en el loot del suelo. Se barren muchos chunks porque cada uno tira sus
        /// propios dados: pedir que salga en UNO concreto sería fijar el resultado del RNG, que
        /// cambiaría con cualquier retoque de la pool.
        /// </summary>
        [Test]
        public void TheSprayCanIsRolledIntoGroundLoot()
        {
            var seen = new HashSet<string>();
            for (int cx = -30; cx <= 30 && !seen.Contains(ItemName); cx++)
            {
                for (int cz = -30; cz <= 30; cz++)
                {
                    foreach (var entry in ChunkLootRoll.RollItems(Seed, cx, cz, DefaultProfile))
                        seen.Add(entry.Name);
                    if (seen.Contains(ItemName)) break;
                }
            }

            Assert.IsTrue(seen.Contains(ItemName),
                "el bote tiene que poder encontrarse en el mundo; si no, la mecánica depende " +
                "del menú de depuración");
        }

        /// <summary>
        /// El nombre de la pool tiene que RESOLVER. Es la trampa que el doc de ChunkLootRoll
        /// avisa para los materiales de crafteo de ADR-064: con el nombre puesto y el asset sin
        /// generar, GetWithName devuelve null y el slot se cae con un warning por cada tirada.
        /// </summary>
        [Test]
        public void TheNameInTheLootPoolResolvesToARealDefinition()
        {
            var definition = ItemDefinition.GetWithName(ItemName);

            Assert.IsNotNull(definition,
                $"'{ItemName}' está en las pools de loot pero no hay ItemDefinition con ese " +
                "nombre. Ejecuta 'Backrooms/Spray/Crear bote de spray'.");
            Assert.IsNotNull(definition.Pickup,
                "sin _pickup el item resuelve por nombre y luego no aparece nunca en el suelo");
        }

        /// <summary>
        /// LA REGRESIÓN DEL 2026-08-13. Sin la etiqueta `Wieldable`, `WieldableInventory` no
        /// puede meter el bote en su pistolera (la resuelve con
        /// `FindContainer(WithTag(WieldableTag))`) y `EquipAction` lo rechaza. El item se recoge,
        /// se ve, y no se equipa — sin un solo error en consola.
        /// </summary>
        [Test]
        public void TheSprayCanIsTaggedAsWieldableOrItCannotBeHeld()
        {
            var definition = ItemDefinition.GetWithName(ItemName);
            Assert.IsNotNull(definition);

            Assert.IsFalse(definition.Tag.IsNull, "el bote necesita etiqueta para poder empuñarse");
            // Por `.Id` y no comparando los structs: `DataIdReference` es un envoltorio y su
            // igualdad no es algo de lo que este test deba depender.
            Assert.AreEqual(ItemConstants.WieldableTag.Id, definition.Tag.Id,
                "y tiene que ser exactamente la de Wieldable, que es la que mira EquipAction");
        }

        /// <summary>
        /// Un bote por hueco: la carga vive en el componente de la instancia, así que apilarlos
        /// mezclaría varios medio gastados en una sola cifra.
        /// </summary>
        [Test]
        public void TheSprayCanDoesNotStack()
        {
            var definition = ItemDefinition.GetWithName(ItemName);
            Assert.IsNotNull(definition);
            Assert.AreEqual(1, definition.StackSize);
        }
    }
}
