using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-133 — la linterna de manivela como objeto del juego. Modelado sobre
    /// <see cref="SprayLootTests"/>, que es el precedente para un item propio, y cerrando los
    /// mismos modos de fallo SILENCIOSOS que allí ya mordieron: un nombre en la pool sin su
    /// `ItemDefinition` hace que `ChunkLootManager` descarte el slot con un warning, y una
    /// definición sin la etiqueta `Wieldable` da un item que se recoge, se ve en el inventario y
    /// no se deja empuñar, sin un solo error.
    ///
    /// Y dos propios, que son los que de verdad justifican este fichero: el prefab del wieldable
    /// es un CLON DE LA ANTORCHA al que hay que quitarle cosas, y lo que se queda dentro no se
    /// nota hasta jugarlo. Un `WieldableDurabilityDepleter` heredado vacía la linterna en un
    /// segundo dando igual cuánta cuerda le des; una `Light` de fuego encendida la hace alumbrar
    /// naranja desde el puño y le roba el shadow map al haz (ADR-065).
    /// </summary>
    [TestFixture]
    public class CrankFlashlightItemTests
    {
        private const string ItemName = "Crank Flashlight";
        private const string BatteryHealthName = "Battery Health";
        private const string PrefabPath = "Assets/Prefabs/Wieldables/BR_Wieldable_CrankFlashlight.prefab";
        private const string BodyMeshPath = "Assets/Art/Items/CrankFlashlight/BR_CrankFlashlight_Body_Mesh.asset";
        private const string CrankMeshPath = "Assets/Art/Items/CrankFlashlight/BR_CrankFlashlight_Crank_Mesh.asset";

        private static ItemDefinition Definition => ItemDefinition.GetWithName(ItemName);

        private static GameObject Prefab =>
            UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

        /// <summary>
        /// El nombre que está en `ChunkLootRoll.MaterialPool` tiene que RESOLVER. Es la trampa que
        /// el doc de esa clase avisa para los materiales de crafteo de ADR-064: con el nombre
        /// puesto y el asset sin generar, `GetWithName` devuelve null y el slot se cae con un
        /// warning por cada tirada.
        /// </summary>
        [Test]
        public void TheNameInTheLootPoolResolvesToARealDefinition()
        {
            var definition = Definition;

            Assert.IsNotNull(definition,
                $"'{ItemName}' está en la pool de materiales pero no hay ItemDefinition con ese " +
                "nombre. Ejecuta 'Backrooms/Linterna/Crear linterna de manivela'.");
            Assert.IsNotNull(definition.Pickup,
                "sin _pickup el item resuelve por nombre y luego no aparece nunca en el suelo");
        }

        /// <summary>
        /// Sin la etiqueta `Wieldable`, `WieldableInventory` no puede meter la linterna en su
        /// pistolera (la resuelve con `FindContainer(WithTag(WieldableTag))`) y `EquipAction` la
        /// rechaza. Se recoge, se ve, y no se equipa.
        /// </summary>
        [Test]
        public void TheFlashlightIsTaggedAsWieldableOrItCannotBeHeld()
        {
            var definition = Definition;
            Assert.IsNotNull(definition);

            Assert.IsFalse(definition.Tag.IsNull, "la linterna necesita etiqueta para empuñarse");
            Assert.AreEqual(ItemConstants.WieldableTag.Id, definition.Tag.Id,
                "y tiene que ser exactamente la de Wieldable, que es la que mira EquipAction");
        }

        /// <summary>Una por hueco: la carga y la batería son de INSTANCIA, y apilarlas las
        /// promediaría en una sola cifra.</summary>
        [Test]
        public void TheFlashlightDoesNotStack()
        {
            Assert.AreEqual(1, Definition.StackSize);
        }

        /// <summary>
        /// Las DOS propiedades de instancia, que son toda la mecánica: `Durability` es la carga y
        /// `Battery Health` lo que rinde esta linterna concreta. Y las dos con sorteo al nacer —
        /// sin él, todas las linternas del mundo nacen idénticas y encontrar otra no significa nada.
        /// </summary>
        [Test]
        public void TheFlashlightCarriesItsTwoInstanceProperties()
        {
            var definition = Definition;
            Assert.IsNotNull(definition);

            var battery = ItemPropertyDefinition.GetWithName(BatteryHealthName);
            Assert.IsNotNull(battery,
                $"falta la propiedad '{BatteryHealthName}'. La crea " +
                "'Backrooms/Linterna/Crear linterna de manivela'.");

            var properties = new UnityEditor.SerializedObject(definition).FindProperty("_properties");
            Assert.IsNotNull(properties);
            Assert.AreEqual(2, properties.arraySize,
                "la linterna necesita exactamente la carga y la salud de batería");

            bool charge = false, health = false;
            for (int i = 0; i < properties.arraySize; i++)
            {
                var element = properties.GetArrayElementAtIndex(i);
                int id = element.FindPropertyRelative("_itemPropertyId").intValue;
                bool random = element.FindPropertyRelative("_useRandomValue").boolValue;
                var range = element.FindPropertyRelative("_valueRange").vector2Value;

                if (id == ItemConstants.Durability.Id) charge = true;
                else if (id == battery.Id) health = true;
                else Assert.Fail($"propiedad inesperada (id={id}) en la linterna");

                Assert.IsTrue(random, $"la propiedad id={id} tiene que sortearse al nacer");
                Assert.Less(range.x, range.y, $"el rango de id={id} está al revés o es un punto");
            }

            Assert.IsTrue(charge, "falta `Durability`: la linterna nacería sin carga que gastar");
            Assert.IsTrue(health, $"falta '{BatteryHealthName}': todas rendirían lo mismo");
        }

        /// <summary>
        /// EL PREFAB NO PUEDE SEGUIR QUEMÁNDOSE COMO LA ANTORCHA DE LA QUE SALIÓ. El
        /// `WieldableDurabilityDepleter` del donante gasta un punto por segundo: heredado, la
        /// linterna se vacía en un segundo dieras la cuerda que dieras. Y el `WieldableTool`
        /// tampoco puede quedarse: no entrega la fase `Hold`, que es la manivela entera.
        /// </summary>
        [Test]
        public void TheWieldablePrefabDoesNotBurnLikeTheDonorTorch()
        {
            var prefab = Prefab;
            Assert.IsNotNull(prefab, $"falta '{PrefabPath}'. Ejecuta " +
                "'Backrooms/Linterna/Crear linterna de manivela'.");

            Assert.IsNotNull(prefab.GetComponent<CrankFlashlightWieldable>(),
                "sin nuestro wieldable la linterna no da cuerda ni gasta carga");

            foreach (var component in prefab.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string type = component.GetType().Name;
                Assert.AreNotEqual("WieldableDurabilityDepleter", type,
                    "el gastador de la antorcha sigue dentro: vacía la linterna en un segundo");
                Assert.AreNotEqual("WieldableTool", type,
                    "el WieldableTool del donante sigue dentro y se comería la fase Hold");
            }
        }

        /// <summary>
        /// El haz: un SPOT encendido, y ninguna otra luz activa. La luz de fuego que la linterna
        /// hereda tiene que quedarse apagada — si no, alumbra naranja desde el puño y además entra
        /// en el criterio de `TorchShadowCaster` (ADR-065), que promueve a sombras la luz más
        /// potente del wieldable activo.
        /// </summary>
        [Test]
        public void TheOnlyLiveLightIsTheSpotBeam()
        {
            var prefab = Prefab;
            Assert.IsNotNull(prefab);

            int live = 0;
            foreach (var light in prefab.GetComponentsInChildren<Light>(true))
            {
                // `activeInHierarchy` NO sirve sobre un prefab-asset: como no está en ninguna
                // escena, devuelve false para todo y este test daba cero luces vivas sobre un
                // prefab correcto. Hay que subir por los padres mirando `activeSelf`.
                if (!light.enabled || !IsActiveInPrefab(light.transform)) continue;
                live++;
                Assert.AreEqual(LightType.Spot, light.type,
                    "una linterna hace un CONO; un point es la luz de fuego de la antorcha");
            }

            Assert.AreEqual(1, live,
                "tiene que haber exactamente una luz viva: el haz. Cero = no alumbra y los peers " +
                "no la ven (ADR-042); dos = la llama heredada sigue encendida");
        }

        /// <summary>Activo de verdad DENTRO del prefab: `activeSelf` propio y el de todos sus
        /// padres. Ver el comentario de arriba para por qué no vale `activeInHierarchy`.</summary>
        private static bool IsActiveInPrefab(Transform t)
        {
            for (var node = t; node != null; node = node.parent)
                if (!node.gameObject.activeSelf) return false;
            return true;
        }

        /// <summary>
        /// LA MANIVELA ES UN HIJO CON PIVOTE PROPIO, y ésa es la decisión que sostiene la
        /// animación entera: si el cuerpo y la manivela se fundieran en una malla, la manivela no
        /// podría girar. Este test no deja que un rehorneado las junte sin que salte nada.
        /// </summary>
        [Test]
        public void TheCrankIsItsOwnChildAndNotFusedIntoTheBody()
        {
            var prefab = Prefab;
            Assert.IsNotNull(prefab);

            var body = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(BodyMeshPath);
            var crank = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(CrankMeshPath);
            Assert.IsNotNull(body, $"falta '{BodyMeshPath}'. Ejecuta " +
                "'Backrooms/Linterna/Aplicar modelo Meshy'.");
            Assert.IsNotNull(crank, $"falta '{CrankMeshPath}'.");
            Assert.AreNotSame(body, crank, "cuerpo y manivela no pueden ser la misma malla");

            Transform bodyNode = null, crankNode = null;
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Body") bodyNode = t;
                else if (t.name == "Crank") crankNode = t;
            }

            Assert.IsNotNull(bodyNode, "no hay nodo 'Body' en el prefab");
            Assert.IsNotNull(crankNode, "no hay nodo 'Crank': la manivela no podría girar");
            Assert.AreSame(bodyNode.parent, crankNode.parent,
                "cuerpo y manivela cuelgan del mismo nodo del modelo");

            // El pivote de la manivela en su EJE: la malla canónica sale del origen hacia +Y, así
            // que su caja no puede estar centrada en el origen. Con el pivote en el centro el
            // brazo orbitaría en vez de girar — pasó, y sólo se vio en una captura.
            Assert.Greater(crank.bounds.center.y, crank.bounds.extents.y * 0.5f,
                "la malla de la manivela está centrada en su caja: el pivote no está en el eje");
        }

        /// <summary>
        /// La manivela no puede barrer por dentro de la carcasa. Es la condición geométrica exacta
        /// —el brazo gira a X constante, así que su semiancho tiene que caber entero fuera del
        /// semiancho del cuerpo— y es la que se perdió por elegir la separación como una fracción
        /// a ojo.
        /// </summary>
        [Test]
        public void TheCrankSweepsClearOfTheHousing()
        {
            var prefab = Prefab;
            var body = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(BodyMeshPath);
            var crank = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(CrankMeshPath);
            Assert.IsNotNull(prefab);
            Assert.IsNotNull(body);
            Assert.IsNotNull(crank);

            Transform crankNode = null;
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                if (t.name == "Crank") { crankNode = t; break; }

            Assert.IsNotNull(crankNode);
            Assert.GreaterOrEqual(crankNode.localPosition.x,
                body.bounds.extents.x + crank.bounds.extents.x,
                "el eje está demasiado cerca del cuerpo: media vuelta pasaría por dentro");
        }
    }
}
