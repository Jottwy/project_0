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
        private const string ScenePath = "Assets/PolymindGames/STP/Demo/Scenes/Showcase/STP_Showcase.unity";
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
        public void LaEscenaVivaApuntaALaVariante()
        {
            string guid = AssetDatabase.AssetPathToGUID(VariantPath);
            string scene = File.ReadAllText(ScenePath);
            StringAssert.Contains("propertyPath: _playerUIPrefab", scene, "sin override en STP_Showcase");
            Assert.IsTrue(scene.Contains($"guid: {guid}"), "el override no apunta a BR_UI_Player: Backrooms/UI/Point GameMode at BR_UI_Player");
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
    }
}
