using TMPro;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Tokens de la piel «Bajo el fluorescente» (INVENTORY-ROADMAP.md, D12). Un solo asset bajo
    /// Resources para que cualquier UI construida en código (patrón <c>WristWatchDisplay</c>)
    /// coja colores, sprites y fuentes del mismo sitio; cambiar un tono aquí cambia toda la UI.
    ///
    /// Los sprites y los SDF NO se autoran a mano: los genera <c>BackroomsUiThemeBuilder</c>
    /// (menú Backrooms/UI/Build Theme) de forma determinista, y este asset sólo los referencia.
    /// Si un campo sale a null en juego, la cura es relanzar ese menú, no rellenarlo en el
    /// inspector — el builder lo pisaría.
    /// </summary>
    [CreateAssetMenu(fileName = "BackroomsUiTheme", menuName = "Backrooms/UI Theme")]
    public sealed class BackroomsUiTheme : ScriptableObject
    {
        /// <summary>Ruta bajo Resources; la misma que escribe el builder.</summary>
        public const string ResourcePath = "UI/BackroomsUiTheme";

        [Header("Colores (D12)")]
        [Tooltip("Fondo de la pantalla del inventario, bajo el mundo oscurecido.")]
        public Color Ground = Hex(0x15140C);
        [Tooltip("Texto principal.")]
        public Color Ink = Hex(0xEBE7D3);
        [Tooltip("Texto secundario: cifras, etiquetas de parte del cuerpo.")]
        public Color InkDim = Hex(0xA8A48C);
        [Tooltip("La luz del tubo: selección, huecos válidos, plegado automático.")]
        public Color Fluorescent = Hex(0xDFE9B8);
        [Tooltip("Cinta Dymo negra.")]
        public Color Tape = Hex(0x121212);
        [Tooltip("Texto embosado sobre la cinta.")]
        public Color TapeInk = Hex(0xF1F0E8);
        [Tooltip("Cinta roja: sólo para «no se puede».")]
        public Color TapeRed = Hex(0x8F231C);
        [Tooltip("Papel de la etiqueta de almacén.")]
        public Color Paper = Hex(0xE9E2C7);
        [Tooltip("Texto sobre el papel.")]
        public Color PaperInk = Hex(0x221F16);
        [Tooltip("Tinte de cámara barata que va ENCIMA de todo (soft light).")]
        public Color CameraTint = Hex(0xC8D08A);

        [Header("Sprites (9-slice, generados)")]
        public Sprite DymoTape;
        public Sprite DymoTapeRed;
        public Sprite SunkenSlot;
        public Sprite WarehouseLabel;
        public Sprite LabelHole;
        public Sprite NylonStrap;

        [Header("Fuentes (SDF generados de Assets/Art/Fonts)")]
        [Tooltip("Barlow Semi Condensed SemiBold: títulos y nombres de objeto.")]
        public TMP_FontAsset Display;
        [Tooltip("Barlow Semi Condensed Bold: cifras grandes.")]
        public TMP_FontAsset DisplayBold;
        [Tooltip("Barlow Semi Condensed Regular: texto corrido en el papel.")]
        public TMP_FontAsset DisplayRegular;
        [Tooltip("IBM Plex Mono Regular: cintas, cifras tabulares, etiquetas de parte.")]
        public TMP_FontAsset Mono;
        [Tooltip("IBM Plex Mono SemiBold: cintas de cabecera.")]
        public TMP_FontAsset MonoBold;

        public static BackroomsUiTheme Load() => Resources.Load<BackroomsUiTheme>(ResourcePath);

        /// <summary>Un tono como en la maqueta HTML: 0xRRGGBB opaco.</summary>
        public static Color Hex(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}
