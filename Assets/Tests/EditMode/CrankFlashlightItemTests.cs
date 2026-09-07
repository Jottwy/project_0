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
        private const string IconPath = "Assets/Art/Items/BR_CrankFlashlight_Icon.png";

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

        /// <summary>
        /// EL FALLO QUE ERA UN BUG Y NO ARTE PRESTADO. `Pickup != null` pasaba en verde con el pickup
        /// de la ANTORCHA, que es lo que la linterna heredó del donante — y ese prefab es el que
        /// instancian las tres rutas (el `DropAction` al soltar, `StpItemReplicator` en todos los
        /// clientes, los spawns de loot) y el que `ProxyHeldItemHook` cuelga de la mano del avatar
        /// remoto (ADR-023). Una linterna en el suelo era una antorcha para todo el mundo.
        ///
        /// Se comprueba la MALLA y no el nombre del prefab: el nombre se cambia sin arreglar nada.
        /// Y las DOS mallas, porque una linterna del suelo sin manivela es otro objeto.
        /// </summary>
        [Test]
        public void TheDroppedFlashlightShowsBothBakedMeshesAndNotTheDonor()
        {
            var definition = Definition;
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup, "sin _pickup no aparece nunca en el suelo");

            var body = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(BodyMeshPath);
            var crank = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(CrankMeshPath);
            Assert.IsNotNull(body);
            Assert.IsNotNull(crank);

            var filter = definition.Pickup.GetComponent<MeshFilter>();
            Assert.IsNotNull(filter, "el prefab del suelo tiene que traer su malla en el root");
            Assert.AreSame(body, filter.sharedMesh,
                "el objeto del suelo no enseña el cuerpo horneado. Ejecuta " +
                "'Backrooms/Linterna/Crear la linterna del suelo'.");

            var crankNode = definition.Pickup.transform.Find("Crank");
            Assert.IsNotNull(crankNode, "el pickup no tiene el hijo 'Crank': en el suelo no hay manivela");
            var crankFilter = crankNode.GetComponent<MeshFilter>();
            Assert.IsNotNull(crankFilter);
            Assert.AreSame(crank, crankFilter.sharedMesh, "la manivela del suelo no es la horneada");
        }

        /// <summary>
        /// Y que dentro del pickup vaya el item CORRECTO: un clon del prefab donante llega con el
        /// `_item` del donante, así que recoger la linterna del suelo metía OTRA COSA en la mochila.
        /// </summary>
        [Test]
        public void ThePickupHandsBackTheFlashlightAndNotTheDonorItem()
        {
            var definition = Definition;
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup);

            var serialized = new UnityEditor.SerializedObject(definition.Pickup);
            var value = serialized.FindProperty("_item").FindPropertyRelative("_value");

            Assert.AreEqual(definition.Id, value.intValue,
                "el ItemPickup del suelo entrega otro item distinto de la linterna");
        }

        /// <summary>
        /// Ni un trozo del objeto del suelo puede seguir siendo geometría del vendor: el donante
        /// traía dos hijos de LOD con la malla del HACHA, y sin quitarlos la linterna se convierte
        /// en hacha a partir de cierta distancia — en una banda donde nadie mira mientras prueba.
        /// El único hijo con malla permitido es la manivela, y con la malla horneada.
        /// </summary>
        [Test]
        public void NoPartOfTheDroppedFlashlightIsStillVendorGeometry()
        {
            var definition = Definition;
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup);

            var body = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(BodyMeshPath);
            var crank = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(CrankMeshPath);

            Assert.IsNull(definition.Pickup.GetComponent<LODGroup>(),
                "el LODGroup del hacha sigue en el pickup");

            foreach (var filter in definition.Pickup.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                Assert.IsTrue(mesh == body || mesh == crank,
                    $"'{filter.gameObject.name}' lleva la malla '{(mesh != null ? mesh.name : "null")}', " +
                    "que no es ni el cuerpo ni la manivela horneados: geometría del donante");
            }
        }

        /// <summary>
        /// El icono es el propio, importado como Sprite en FullRect. Sin Sprite no se puede asignar
        /// y la linterna se queda con la antorcha en el inventario; con el mesh en `Tight` Unity
        /// recorta el borde transparente y el hueco cuadrado del inventario la estira.
        /// </summary>
        [Test]
        public void TheIconIsItsOwnFullRectSprite()
        {
            var definition = Definition;
            Assert.IsNotNull(definition);

            var sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(IconPath);
            Assert.IsNotNull(sprite, $"falta '{IconPath}' o no se importó como Sprite. " +
                "Ejecuta 'tools/dev/MakeItemIcon.py' y 'Backrooms/Linterna/Asignar icono de la linterna'.");
            Assert.AreSame(sprite, definition.Icon, "la definición no lleva el icono propio");

            var importer = UnityEditor.AssetImporter.GetAtPath(IconPath) as UnityEditor.TextureImporter;
            Assert.IsNotNull(importer);
            var settings = new UnityEditor.TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            Assert.AreEqual(SpriteMeshType.FullRect, settings.spriteMeshType,
                "con Tight el inventario estira el icono");
        }

        /// <summary>
        /// El hook que hace girar la manivela en la mano del VECINO se engancha en runtime al modelo
        /// instanciado (ADR-133: no está en el prefab del avatar, para no rehornearlo). Este test
        /// cierra las dos puertas de ese acuerdo: que el pickup de la linterna tiene el hijo que el
        /// hook busca, y que un modelo sin manivela no recibe hook — si un día el hijo se renombra
        /// en el creador del pickup y no en el hook, el vecino deja de girar sin ningún error.
        ///
        /// POR REFLEXIÓN, y no por gusto: el hook vive en `Assembly-CSharp` (la carpeta de los
        /// proxies no tiene asmdef) y un asmdef como `EditModeTests` no puede referenciar el
        /// assembly por defecto — el gate de compilación lo dijo con tres CS0234. Un test que no
        /// existe es peor que uno feo.
        /// </summary>
        [Test]
        public void TheProxyCrankHookAttachesToTheFlashlightAndToNothingElse()
        {
            var definition = Definition;
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup);

            var hookType = System.Type.GetType(
                "BackroomsSurvival.Migration.STPIntegration.ProxyCrankHook, Assembly-CSharp");
            Assert.IsNotNull(hookType, "no existe ProxyCrankHook en Assembly-CSharp: el vecino no gira");
            var attach = hookType.GetMethod("AttachIfCranked",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(attach, "ProxyCrankHook.AttachIfCranked ya no existe o cambió de firma");

            var root = new GameObject("FakeProxyRoot");
            var model = Object.Instantiate(definition.Pickup.gameObject, root.transform, false);
            var plain = new GameObject("NoCrank");
            plain.transform.SetParent(root.transform, false);
            try
            {
                var hook = attach.Invoke(null, new object[] { model, root.transform }) as Component;
                Assert.IsNotNull(hook, "el modelo de la linterna no recibe ProxyCrankHook: falta el " +
                    "hijo 'Crank' que el hook busca");
                Assert.AreSame(model, hook.gameObject, "el hook tiene que vivir en el modelo de mano, " +
                    "para morir con él");

                Assert.IsNull(attach.Invoke(null, new object[] { plain, root.transform }) as Component,
                    "un modelo sin manivela no puede recibir el hook");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// LA MANO Y EL SUELO SON EL MISMO OBJETO. Joel corrigió a mano la manivela del pickup (giro
        /// de 90° en Y, y = −0,0206) y pidió que «los otros modelos se corrijan también»; la
        /// respuesta correcta no es copiar números a dos prefabs sino que los dos salgan del mismo
        /// sitio (`CrankLocalPosition`/`CrankLocalRotation` del aplicador). Este test es lo que
        /// impide que vuelvan a separarse: compara el nodo `Crank` del wieldable con el del pickup.
        /// </summary>
        [Test]
        public void TheCrankSitsIdenticallyInHandAndOnTheGround()
        {
            var definition = Definition;
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.Pickup);
            var prefab = Prefab;
            Assert.IsNotNull(prefab);

            var ground = definition.Pickup.transform.Find("Crank");
            Assert.IsNotNull(ground, "el pickup no tiene 'Crank'");

            Transform hand = null;
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                if (t.name == "Crank") { hand = t; break; }
            Assert.IsNotNull(hand, "el wieldable no tiene 'Crank'");

            Assert.Less(Vector3.Distance(hand.localPosition, ground.localPosition), 1e-4f,
                $"la manivela está en {hand.localPosition} en la mano y en {ground.localPosition} en el suelo");
            Assert.Less(Quaternion.Angle(hand.localRotation, ground.localRotation), 0.01f,
                "la manivela no gira igual en la mano que en el suelo");

            // Y los dos con la corrección de Joel: 90° en Y, que es lo que pone el disco plano
            // contra el costado. Si alguien vuelve a Quaternion.identity en el aplicador, salta aquí.
            Assert.Less(Quaternion.Angle(ground.localRotation, Quaternion.Euler(0f, 90f, 0f)), 0.01f,
                "la manivela del suelo ya no lleva el giro de 90° en Y que Joel dio por bueno");
        }

        /// <summary>
        /// El eje sobre el que gira la manivela tiene que ser EL MISMO para el jugador
        /// (`CrankFlashlightWieldable.crankAxis`, serializado en el prefab) y para el vecino
        /// (`ProxyCrankHook.Axis`). Viven en assemblies distintos y no pueden compartir la constante,
        /// así que esto es lo único que los ata — con ejes distintos, la manivela del vecino orbita.
        /// </summary>
        [Test]
        public void TheCrankSpinsOnTheSameAxisInHandAndOnTheProxy()
        {
            var prefab = Prefab;
            Assert.IsNotNull(prefab);
            var flashlight = prefab.GetComponent<CrankFlashlightWieldable>();
            Assert.IsNotNull(flashlight);

            var handAxis = new UnityEditor.SerializedObject(flashlight).FindProperty("crankAxis").vector3Value;

            var hookType = System.Type.GetType(
                "BackroomsSurvival.Migration.STPIntegration.ProxyCrankHook, Assembly-CSharp");
            Assert.IsNotNull(hookType, "no existe ProxyCrankHook en Assembly-CSharp");
            var field = hookType.GetField("Axis",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(field, "ProxyCrankHook.Axis ya no existe o cambió de nombre");
            var proxyAxis = (Vector3)field.GetValue(null);

            Assert.Less(Vector3.Distance(handAxis.normalized, proxyAxis.normalized), 1e-4f,
                $"el jugador gira la manivela sobre {handAxis} y el vecino sobre {proxyAxis}");
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
