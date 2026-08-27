using System.Collections.Generic;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// QUÉ CATÁLOGO ESTÁ VIGENTE, decidido en UN solo sitio.
    ///
    /// Lo preguntan dos: el exportador del manifiesto —que es lo que acaba viendo el servidor— y el
    /// streamer del cliente —que es lo que se dibuja y contra lo que se choca—. Si cada uno lo
    /// decidiera por su cuenta, la primera pieza autorada dejaría al servidor colocando de un
    /// catálogo y al cliente dibujando de otro.
    ///
    /// El daño no sería sutil: el índice de pieza es lo que viaja por el wire, así que dos catálogos
    /// distintos no dan "una pieza rara", dan el mundo entero cambiado de sitio. El digest lo caza
    /// —el streamer se apaga si no casa con el del saludo— pero apagarse no es arreglarlo, y esa
    /// comprobación existe para el caso de un cliente desactualizado, no para tapar que dos rutas
    /// del MISMO build no se pongan de acuerdo.
    /// </summary>
    public static class Wg3ActiveCatalog
    {
        public static List<Wg3Piece> Build() => Build(out _);

        /// <summary>
        /// El catálogo vigente. <paramref name="source"/> sale para poder decirlo en el log: saber
        /// de dónde salieron las piezas es la primera pregunta cuando el digest no casa.
        ///
        /// La biblioteca SUSTITUYE al catálogo de código, no se suma. Sumarlos pondría las piezas
        /// autoradas detrás de las de código y, como el índice entra en el hash de colocación, cada
        /// pieza nueva reescribiría todos los mundos ya generados.
        ///
        /// Una biblioteca que no valida NO cae de vuelta al catálogo de código en silencio: eso
        /// daría un cliente que arranca contento con un mundo que no es el del servidor. Se devuelve
        /// vacío, que es lo que el llamante puede detectar.
        /// </summary>
        public static List<Wg3Piece> Build(out string source)
        {
            Wg3PieceLibrary library = Wg3PieceLibrary.Load();
            if (library == null || library.pieces.Length == 0)
            {
                source = "catálogo de código";
                return Wg3Catalog.Build();
            }

            List<string> issues = library.Validate();
            if (issues.Count > 0)
            {
                source = $"biblioteca INVÁLIDA ({issues.Count} motivos): {string.Join(" · ", issues)}";
                return new List<Wg3Piece>();
            }

            source = $"biblioteca autorada ({library.pieces.Length} piezas)";
            return library.BuildCatalog();
        }
    }
}
