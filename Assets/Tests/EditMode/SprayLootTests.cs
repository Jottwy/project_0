using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

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

        private const string BakedMeshPath = "Assets/Art/Items/SprayCan/BR_SprayCan_Mesh.asset";

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

        /// <summary>
        /// EL FALLO QUE ESTE FICHERO NO VEÍA. `Pickup != null` pasaba en verde con el pickup de la
        /// ANTORCHA, que es lo que el bote heredó del donante — y ese prefab es el que instancian
        /// las tres rutas: el `DropAction` del vendor al soltarlo, `StpItemReplicator` en todos los
        /// clientes, y los spawns de loot. Una lata en el suelo era una antorcha para todo el
        /// mundo, y no había test que lo dijera.
        ///
        /// Se comprueba la MALLA y no el nombre del prefab: el nombre se puede cambiar sin arreglar
        /// nada, la malla es lo que se ve.
        /// </summary>
        [Test]
        public void TheDroppedSprayCanShowsTheCanAndNotTheDonorTorch()
        {
            var definition = ItemDefinition.GetWithName(ItemName);
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup, "sin _pickup no aparece nunca en el suelo");

            var baked = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(BakedMeshPath);
            Assert.IsNotNull(baked,
                $"falta '{BakedMeshPath}'. Ejecuta 'Backrooms/Spray/Aplicar modelo Meshy al bote'.");

            var filter = definition.Pickup.GetComponent<MeshFilter>();
            Assert.IsNotNull(filter, "el prefab del suelo tiene que traer su malla en el root");
            Assert.AreSame(baked, filter.sharedMesh,
                "el objeto del suelo no enseña la lata horneada. Ejecuta " +
                "'Backrooms/Spray/Crear el bote del suelo'.");
        }

        /// <summary>
        /// Y que dentro del pickup vaya el item CORRECTO: un clon del prefab de la antorcha llega
        /// con el id del donante, así que recoger la lata metería una antorcha en la mochila.
        /// </summary>
        [Test]
        public void ThePickupHandsBackTheSprayCanAndNotTheDonorItem()
        {
            var definition = ItemDefinition.GetWithName(ItemName);
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup);

            var serialized = new UnityEditor.SerializedObject(definition.Pickup);
            var value = serialized.FindProperty("_item").FindPropertyRelative("_value");

            Assert.AreEqual(definition.Id, value.intValue,
                "el ItemPickup del suelo entrega otro item distinto del bote");
        }

        /// <summary>
        /// Ni un trozo del objeto del suelo puede seguir siendo geometría del vendor. La trampa
        /// concreta son los dos hijos de LOD que traía el prefab donante: llevan la malla de la
        /// ANTORCHA, así que sin quitarlos la lata se convierte en antorcha a partir de cierta
        /// distancia — el mismo fallo, en una banda donde nadie mira mientras prueba.
        /// </summary>
        [Test]
        public void NoPartOfTheDroppedSprayCanIsStillVendorGeometry()
        {
            var definition = ItemDefinition.GetWithName(ItemName);
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup);

            foreach (var filter in definition.Pickup.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                string path = UnityEditor.AssetDatabase.GetAssetPath(filter.sharedMesh);
                StringAssert.DoesNotStartWith("Assets/PolymindGames/", path,
                    $"'{filter.name}' sigue dibujando una malla del vendor ({path})");
            }
        }

        /// <summary>
        /// El root a escala 1 y la lata midiendo lo que mide una lata. Las dos cosas en el mismo
        /// test porque son la misma decisión: la malla se hornea en metros para que el root NO
        /// tenga que escalar, y el root no puede escalar porque el resaltado del vendor
        /// (`MaterialEffect`) es en espacio de OBJETO y se infla con él.
        /// </summary>
        [Test]
        public void TheDroppedSprayCanIsCanSizedWithoutScalingItsRoot()
        {
            var definition = ItemDefinition.GetWithName(ItemName);
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup);

            var root = definition.Pickup.transform;
            Assert.AreEqual(1f, root.localScale.x, 1e-3f, "el root del pickup no puede ir escalado");
            Assert.AreEqual(1f, root.localScale.y, 1e-3f);
            Assert.AreEqual(1f, root.localScale.z, 1e-3f);

            var filter = definition.Pickup.GetComponent<MeshFilter>();
            Assert.IsNotNull(filter);
            Assert.IsNotNull(filter.sharedMesh);

            var size = filter.sharedMesh.bounds.size;
            Assert.AreEqual(0.19f, size.y, 0.02f, "una lata mide ~19 cm de alto");
            Assert.AreEqual(0.066f, size.x, 0.01f, "y ~6,6 cm de diámetro");
            Assert.AreEqual(0.066f, size.z, 0.01f);
        }
    }
}
