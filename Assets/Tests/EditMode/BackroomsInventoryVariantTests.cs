using System.IO;
using BackroomsSurvival.UI;
using NUnit.Framework;
using PolymindGames.UserInterface;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Rebanada 1a — la variante de UI existe, sigue siendo variante del vendor y lleva la piel
    /// puesta; y la escena viva la usa. Rutas y nombres son el CONTRATO con
    /// <c>BackroomsInventoryUiBuilder</c> (Backrooms/UI/Build Inventory Variant), que esta suite no
    /// puede referenciar. La escena se lee como TEXTO, no se abre: abrir Showcase en un test cuesta
    /// minutos y cambia la escena activa del que lo corra en el editor.
    /// </summary>
    public class BackroomsInventoryVariantTests
    {
        private const string VariantPath = "Assets/Prefabs/UI/BR_UI_Player.prefab";
        private const string BasePath = "Assets/PolymindGames/STP/Prefabs/UI/STP_UI_Player.prefab";
        private const string ScenePath = "Assets/Scenes/BR_InventoryTest.unity";
        private const string ShowcasePath = "Assets/PolymindGames/STP/Demo/Scenes/Showcase/STP_Showcase.unity";
        private const string ThemePath = "Assets/Resources/UI/BackroomsUiTheme.asset";

        private static GameObject Variant()
        {
            var v = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            Assert.IsNotNull(v, $"falta '{VariantPath}': lanza Backrooms/UI/Build Inventory Variant");
            return v;
        }

        [Test]
        public void EsUnaVarianteDelPrefabDelVendorNoUnaCopia()
        {
            var v = Variant();
            Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(v),
                "una copia suelta no recibe las actualizaciones del vendor");
            var basePrefab = PrefabUtility.GetCorrespondingObjectFromSource(v);
            Assert.AreEqual(BasePath, AssetDatabase.GetAssetPath(basePrefab));
            Assert.IsNotNull(v.GetComponent<PlayerUI>(), "GameMode instancia por PlayerUI en la raíz");
        }

        [Test]
        public void LaEscenaDePruebasApuntaALaVarianteYShowcaseSigueConElVendor()
        {
            string guid = AssetDatabase.AssetPathToGUID(VariantPath);
            Assert.IsTrue(File.Exists(ScenePath), $"falta '{ScenePath}': Backrooms/UI/Build Inventory Test Scene");
            string scene = File.ReadAllText(ScenePath);
            Assert.IsTrue(scene.Contains($"guid: {guid}"), "la escena de pruebas no apunta a BR_UI_Player");
            // Showcase recibe la variante por el enganche, NUNCA editando su YAML: es del vendor y un reimport la pisa.
            Assert.IsFalse(File.ReadAllText(ShowcasePath).Contains($"guid: {guid}"),
                "STP_Showcase apunta a la variante en su YAML: tiene que entrar por BackroomsShowcasePlayerUi");
        }

        [Test]
        public void ShowcaseRecibeLaVariantePorElEnganche()
        {
            var hook = Resources.Load<BackroomsShowcasePlayerUi>(BackroomsShowcasePlayerUi.ResourcePath);
            Assert.IsNotNull(hook, $"falta Resources/{BackroomsShowcasePlayerUi.ResourcePath}");
            Assert.IsNotNull(hook.PlayerUIPrefab, "el enganche no apunta a ninguna PlayerUI");
            Assert.AreEqual(VariantPath, AssetDatabase.GetAssetPath(hook.PlayerUIPrefab));

            Assert.IsTrue(BackroomsShowcasePlayerUi.ShouldInstall("STP_Showcase", false));
            Assert.IsFalse(BackroomsShowcasePlayerUi.ShouldInstall("STP_Showcase", true), "ya hay PlayerUI: no se duplica");
            Assert.IsFalse(BackroomsShowcasePlayerUi.ShouldInstall("BR_InventoryTest", false), "la escena de pruebas ya la trae");
            Assert.IsFalse(BackroomsShowcasePlayerUi.ShouldInstall("MainMenu", false));
        }

        [Test]
        public void SinContenedorElPanelSeEscondeYLosHuecosNoSeCapan()
        {
            // Cada panel que la variante ata por nombre a un contenedor de prototipo tiene que estar en la lista del escondite.
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(VariantPath), @"_containerName: (\w+)"))
                CollectionAssert.Contains(BackroomsHideUnboundContainers.PrototypeContainers, m.Groups[1].Value,
                    $"el panel '{m.Groups[1].Value}' de la variante no se escondería en STP_Showcase");

            string[] names = BackroomsHideUnboundContainers.PrototypeContainers;
            Assert.IsTrue(BackroomsHideUnboundContainers.ShouldHide("Back", names, _ => false));
            Assert.IsFalse(BackroomsHideUnboundContainers.ShouldHide("Back", names, _ => true), "la escena de pruebas sí lo tiene");
            Assert.IsFalse(BackroomsHideUnboundContainers.ShouldHide("Storage", names, _ => false), "Alrededor se ata a contenedores ajenos");
            Assert.IsFalse(BackroomsHideUnboundContainers.ShouldHide("", names, _ => false));

            Assert.AreEqual(6, BackroomsSurvival.Wearables.BackroomsWornSlotsUI.VisibleSlots(true, 2, 0, 6), "sin cintura: los 6 de la funda");
            Assert.AreEqual(4, BackroomsSurvival.Wearables.BackroomsWornSlotsUI.VisibleSlots(false, 2, 2, 8), "con cintura: manos + cinturón");
        }

        [Test]
        public void ElFondoOscureceElMundoYNoBloqueaElRaton()
        {
            var v = Variant();
            var backdrop = v.GetComponentInChildren<BackroomsInventoryBackdrop>(true);
            Assert.IsNotNull(backdrop, "sin BR_Backdrop");
            Assert.AreEqual(0, backdrop.transform.GetSiblingIndex(), "el fondo va DEBAJO de todo: primer hijo");
            Assert.IsFalse(backdrop.GetComponent<Image>().raycastTarget, "un fondo que captura el ratón mata el drag");
            Assert.AreEqual(0f, backdrop.GetComponent<CanvasGroup>().alpha, "nace apagado; lo enciende la inspección");
        }

        [Test]
        public void LosSlotsYLasCintasLlevanLosSpritesDelTema()
        {
            var v = Variant();
            var theme = AssetDatabase.LoadAssetAtPath<BackroomsUiTheme>(ThemePath);
            Assert.IsNotNull(theme);

            var slots = v.GetComponentsInChildren<ItemSlotUI>(true);
            Assert.Greater(slots.Length, 0);
            foreach (var s in slots)
                Assert.AreEqual(theme.SunkenSlot, s.GetComponent<Image>().sprite, $"slot sin hueco hundido: {s.name}");

            int tapes = 0;
            foreach (var t in v.GetComponentsInChildren<Transform>(true))
                if (t.name == "Header" && t.TryGetComponent<Image>(out var img) && img.sprite == theme.DymoTape) tapes++;
            Assert.Greater(tapes, 0, "ninguna cabecera lleva cinta Dymo");

            var inv = v.GetComponentInChildren<InventoryUI>(true);
            foreach (var tmp in inv.GetComponentsInChildren<TextMeshProUGUI>(true))
                Assert.IsTrue(tmp.font == theme.Display || tmp.font == theme.Mono || tmp.font == theme.MonoBold
                              || tmp.font == theme.DisplayBold || tmp.font == theme.DisplayRegular,
                    $"'{tmp.name}' sigue con la fuente del vendor ({(tmp.font ? tmp.font.name : "null")})");
        }

        [Test]
        public void LosConmutadoresDeColumnaEstanCableados()
        {
            var v = Variant();
            var body = v.GetComponentInChildren<BackroomsBodyViewToggle>(true);
            Assert.IsNotNull(body, "sin conmutador Ropa | Heridas");
            AssertWired(body, "_ropaButton", "_heridasButton", "_previewRoot", "_woundsPanel");
            var around = v.GetComponentInChildren<BackroomsAroundViewToggle>(true);
            Assert.IsNotNull(around, "sin conmutador Alrededor | Crafteo");
            AssertWired(around, "_aroundButton", "_craftButton", "_tailorButton", "_workstations", "_emptyLabel", "_tailoringPanel");
            var tailoring = v.GetComponentInChildren<BackroomsTailoringPanel>(true);
            Assert.IsNotNull(tailoring, "sin panel de sastrería");
            AssertWired(tailoring, "_content", "_rowTemplate", "_empty", "_materials", "_notice");
            var template = (RectTransform)new SerializedObject(tailoring).FindProperty("_rowTemplate").objectReferenceValue;
            foreach (var child in new[] { "Name", "State", "SewBtn", "TapeBtn" })
                Assert.IsNotNull(template.Find(child), $"a la fila de sastrería le falta {child}");
            Assert.IsFalse(template.gameObject.activeSelf, "la plantilla de fila nace apagada");
            Assert.IsNotNull(tailoring.GetComponent<BackroomsInspectionOnly>(), "la sastrería cuelga fuera de los paneles del vendor");
            Assert.IsNotNull(around.GetComponent<BackroomsInspectionOnly>(),
                "la cabecera de Alrededor cuelga fuera de los paneles del vendor: sin esto se queda pintada al cerrar TAB");
        }

        private static void AssertWired(Object component, params string[] fields)
        {
            var so = new SerializedObject(component);
            foreach (var f in fields)
                Assert.IsNotNull(so.FindProperty(f)?.objectReferenceValue, $"{component.GetType().Name}.{f} sin asignar");
        }
    }
}
