using BackroomsSurvival.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// D12 — el tema «Bajo el fluorescente» está completo y es lo que el builder promete. Pide el
    /// editor (AssetDatabase). Las rutas son el CONTRATO con <c>BackroomsUiThemeBuilder</c>, que
    /// esta suite no puede referenciar: si algo falla, la cura es el menú Backrooms/UI/Build Theme.
    /// Un sprite 9-slice sin borde se estira entero y la cinta Dymo saldría como un chicle: por eso
    /// se comprueban los bordes y no sólo que el sprite exista.
    /// </summary>
    public class BackroomsUiThemeTests
    {
        private const string ThemePath = "Assets/Resources/UI/BackroomsUiTheme.asset";

        private static BackroomsUiTheme Theme()
        {
            var theme = AssetDatabase.LoadAssetAtPath<BackroomsUiTheme>(ThemePath);
            Assert.IsNotNull(theme, $"falta '{ThemePath}': lanza Backrooms/UI/Build Theme");
            return theme;
        }

        [Test]
        public void ElTemaSeCargaPorResources()
        {
            Assert.IsNotNull(BackroomsUiTheme.Load(), "Resources.Load no encuentra " + BackroomsUiTheme.ResourcePath);
        }

        [Test]
        public void LosSeisSpritesExistenYLosNueveSliceTienenBorde()
        {
            var t = Theme();
            AssertNineSlice(t.DymoTape, nameof(t.DymoTape));
            AssertNineSlice(t.DymoTapeRed, nameof(t.DymoTapeRed));
            AssertNineSlice(t.SunkenSlot, nameof(t.SunkenSlot));
            AssertNineSlice(t.WarehouseLabel, nameof(t.WarehouseLabel));
            AssertNineSlice(t.NylonStrap, nameof(t.NylonStrap));
            Assert.IsNotNull(t.LabelHole, "LabelHole");
        }

        [Test]
        public void LasCincoFuentesSonLasDeLaMaqueta()
        {
            var t = Theme();
            AssertFace(t.DisplayRegular, "Barlow Semi Condensed", nameof(t.DisplayRegular));
            AssertFace(t.Display, "Barlow Semi Condensed", nameof(t.Display));
            AssertFace(t.DisplayBold, "Barlow Semi Condensed", nameof(t.DisplayBold));
            AssertFace(t.Mono, "IBM Plex Mono", nameof(t.Mono));
            AssertFace(t.MonoBold, "IBM Plex Mono", nameof(t.MonoBold));
        }

        [Test]
        public void LosColoresSonLosTokensDeD12()
        {
            var t = Theme();
            Assert.AreEqual(BackroomsUiTheme.Hex(0xDFE9B8), t.Fluorescent, "la luz del tubo cambió sin tocar D12");
            Assert.AreEqual(BackroomsUiTheme.Hex(0x8F231C), t.TapeRed, "el rojo es sólo para «no se puede»");
        }

        private static void AssertNineSlice(Sprite s, string field)
        {
            Assert.IsNotNull(s, field);
            Assert.Greater(s.border.sqrMagnitude, 0f, $"{field}: sin borde 9-slice, se estiraría entero");
        }

        private static void AssertFace(TMPro.TMP_FontAsset f, string family, string field)
        {
            Assert.IsNotNull(f, field);
            StringAssert.Contains(family, f.faceInfo.familyName, field);
        }
    }
}
