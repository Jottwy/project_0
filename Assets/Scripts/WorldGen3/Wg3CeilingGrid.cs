using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// La retícula del falso techo de oficina: dónde caen las luminarias de un tramo y, por
    /// tanto, dónde NO puede caer nada más.
    ///
    /// POR QUÉ ESTÁ AQUÍ Y NO DENTRO DE <c>Wg3SceneAssembler.AddPanels</c>, que es de donde sale.
    /// En cuanto un segundo sistema necesita saber dónde están los paneles —el detalle sonoro, que
    /// cuelga una rejilla de aire y no puede meterla dentro de una luminaria— hay dos opciones:
    /// copiar la fórmula, o compartirla. Copiada, las dos derivan en cuanto alguien toque el paso
    /// o el tope, y el síntoma es una rejilla dentro de un fluorescente en una captura de dentro de
    /// tres semanas. Compartida, no pueden.
    ///
    /// Sin dependencias más allá de <c>Mathf</c> a propósito: así la puede compilar un arnés
    /// headless sin arrastrar el ensamblador entero.
    /// </summary>
    public static class Wg3CeilingGrid
    {
        /// <summary>Placa del techo de la oficina: 60 cm.</summary>
        public const float TileM = 0.6f;

        /// <summary>Paso de la rejilla de luminarias, en placas. Cuatro placas = 2,4 m.</summary>
        public const int PitchTiles = 4;

        /// <summary>Tope de paneles por tramo: en una nave de 25 × 25 el paso se abre hasta
        /// cumplirlo. Son mallas, no luces, pero mil paneles en radio 1 también pesan.</summary>
        public const int MaxPanelsPerSegment = 40;

        /// <summary>
        /// Resuelve la retícula de un tramo de <paramref name="sizeX"/> × <paramref name="sizeZ"/>
        /// metros: cuántas luminarias por eje, con qué paso y con qué origen. El sobrante se
        /// reparte a los dos lados, así que la rejilla queda centrada.
        ///
        /// El centro de la luminaria (i, j) es <c>(ox + i·pitch, oz + j·pitch)</c> en coordenadas
        /// LOCALES del tramo.
        /// </summary>
        public static void Solve(float sizeX, float sizeZ,
            out float pitch, out int cx, out int cz, out float ox, out float oz)
        {
            pitch = PitchTiles * TileM;
            cx = Mathf.Max(1, Mathf.FloorToInt(sizeX / pitch));
            cz = Mathf.Max(1, Mathf.FloorToInt(sizeZ / pitch));
            while (cx * cz > MaxPanelsPerSegment)
            {
                pitch += TileM;
                cx = Mathf.Max(1, Mathf.FloorToInt(sizeX / pitch));
                cz = Mathf.Max(1, Mathf.FloorToInt(sizeZ / pitch));
            }
            ox = (sizeX - cx * pitch) * 0.5f + pitch * 0.5f;
            oz = (sizeZ - cz * pitch) * 0.5f + pitch * 0.5f;
        }

        /// <summary>
        /// Los HUECOS de la retícula en un eje: el punto medio entre dos luminarias consecutivas.
        /// Con <paramref name="count"/> luminarias hay <c>count − 1</c> huecos.
        ///
        /// Es lo que necesita cualquiera que quiera colgar algo del techo sin taparlo. Con una sola
        /// luminaria no hay hueco y el llamante decide qué hacer (típicamente, centrarse).
        /// </summary>
        public static int GapCount(int count) => Mathf.Max(0, count - 1);

        /// <summary>Coordenada local del hueco <paramref name="index"/> (0-based) de un eje.</summary>
        public static float GapAt(float origin, float pitch, int index) =>
            origin + (index + 0.5f) * pitch;
    }
}
