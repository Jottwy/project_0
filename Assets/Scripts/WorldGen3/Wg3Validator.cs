using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// REGLA L23, escrita como validador y no como intención.
    ///
    /// Tal y como estaba redactada en el documento de diseño ("no permitir puertas dentro de
    /// paredes, escaleras imposibles, techos incompatibles…") era una lista de deseos, y las
    /// intenciones no fallan un test. Cada punto de L23 que es comprobable está aquí como una
    /// función que devuelve un motivo concreto.
    ///
    /// Lo usa DOS veces, y las dos importan:
    ///  · <see cref="ValidatePiece"/> al hornear — REGLA R6: una pieza que no pasa no existe. Es
    ///    la defensa contra el modo de fallo que ya se ha pagado dos veces en las salas autoradas:
    ///    algo que se hornea, se exporta, se sortea y se descarta EN SILENCIO, así que el síntoma
    ///    no es un error sino un mundo al que le falta contenido y nadie sabe por qué.
    ///  · <see cref="ValidateConnection"/> al colocar, antes de aceptar una candidata.
    /// </summary>
    public static class Wg3Validator
    {
        /// <summary>Hueco libre mínimo para que un jugador pase. Valor propio de WG3: el
        /// <c>MinCeilingHeight</c> de <c>RoomDefinition</c> (1,2 m) es el mínimo para que un techo
        /// inclinado siga siendo geometría válida, no para que quepa alguien de pie.</summary>
        public const float MinHeadroom = 2.0f;

        /// <summary>Tolerancia al casar cotas de suelo entre dos bocas. F0 exige coincidencia
        /// prácticamente exacta; F5 subirá esto y añadirá pendiente máxima por pieza.</summary>
        public const float FloorMatchTolerance = 0.01f;

        /// <summary>Margen para comparar anchuras. Milímetro: dos bocas de 2,4 m autoradas por
        /// separado tienen que casar, pero 2,4 y 2,5 son incompatibles a propósito (L6).</summary>
        public const float WidthMatchTolerance = 0.001f;

        // ── horneado ────────────────────────────────────────────────────────────────────────

        /// <summary>Comprueba una pieza contra sí misma. Devuelve la lista de motivos; vacía = ok.</summary>
        public static List<string> ValidatePiece(Wg3Piece p)
        {
            var issues = new List<string>();
            if (p == null) { issues.Add("pieza nula"); return issues; }
            if (string.IsNullOrEmpty(p.id)) issues.Add("pieza sin id");
            if (p.sizeX <= 0f || p.sizeZ <= 0f)
                issues.Add($"{p.id}: huella no positiva ({p.sizeX}×{p.sizeZ})");
            if (p.heightMeters < MinHeadroom)
                issues.Add($"{p.id}: altura {p.heightMeters:0.##} m por debajo del hueco mínimo {MinHeadroom} m");
            if (p.sockets == null || p.sockets.Length == 0)
                { issues.Add($"{p.id}: sin bocas — una pieza sin socket no se puede colocar nunca"); return issues; }
            if (p.weight <= 0f)
                issues.Add($"{p.id}: peso no positivo, nunca saldría sorteada");

            for (int i = 0; i < p.sockets.Length; i++)
            {
                Wg3Socket s = p.sockets[i];
                string tag = $"{p.id}[{i}]";
                if (s.side < 0 || s.side > 3) { issues.Add($"{tag}: lado {s.side} fuera de 0..3"); continue; }
                if (s.width <= 0f) { issues.Add($"{tag}: anchura no positiva"); continue; }

                // "Puerta dentro de una pared" de L23: la boca tiene que caber ENTERA en su lado.
                float len = Wg3Piece.SideLength(s.side, p.sizeX, p.sizeZ);
                float half = s.width * 0.5f;
                if (s.offset - half < -1e-4f || s.offset + half > len + 1e-4f)
                    issues.Add($"{tag}: boca de {s.width:0.##} m centrada en {s.offset:0.##} " +
                               $"no cabe en un lado de {len:0.##} m");

                if (s.ceilingY - s.floorY < MinHeadroom)
                    issues.Add($"{tag}: hueco de {(s.ceilingY - s.floorY):0.##} m, por debajo de {MinHeadroom} m");
                if (s.floorY < -1e-4f || s.ceilingY > p.heightMeters + 1e-4f)
                    issues.Add($"{tag}: cotas {s.floorY:0.##}..{s.ceilingY:0.##} fuera de la pieza (0..{p.heightMeters:0.##})");

                // Dos bocas que se pisan en el mismo lado no son dos bocas, es una mal medida.
                for (int j = i + 1; j < p.sockets.Length; j++)
                {
                    Wg3Socket o = p.sockets[j];
                    if (o.side != s.side) continue;
                    float gap = Mathf.Abs(o.offset - s.offset) - (s.width + o.width) * 0.5f;
                    if (gap < -1e-4f)
                        issues.Add($"{p.id}: las bocas {i} y {j} se solapan en el lado {s.side}");
                }
            }
            return issues;
        }

        public static List<string> ValidateCatalog(IReadOnlyList<Wg3Piece> catalog)
        {
            var issues = new List<string>();
            var seen = new HashSet<string>();
            for (int i = 0; i < catalog.Count; i++)
            {
                Wg3Piece p = catalog[i];
                issues.AddRange(ValidatePiece(p));
                if (p != null && !string.IsNullOrEmpty(p.id) && !seen.Add(p.id))
                    issues.Add($"id duplicado en el catálogo: {p.id}");
            }

            // REGLA L21 depende de esto: todo socket sin pareja se tapona, y el tapón tiene que
            // EXISTIR para su tipo. Sin tapón, un socket abierto es un agujero al vacío.
            foreach (Wg3SocketType t in System.Enum.GetValues(typeof(Wg3SocketType)))
            {
                bool used = false, capped = false;
                for (int i = 0; i < catalog.Count; i++)
                {
                    Wg3Piece p = catalog[i];
                    if (p?.sockets == null) continue;
                    for (int s = 0; s < p.sockets.Length; s++)
                    {
                        if (p.sockets[s].type != t) continue;
                        used = true;
                        if (p.sockets.Length == 1) capped = true; // una sola boca ⇒ sirve de tapón
                    }
                }
                if (used && !capped)
                    issues.Add($"el tipo {t} se usa pero no hay ninguna pieza de una sola boca que lo tapone");
            }
            return issues;
        }

        // ── colocación ──────────────────────────────────────────────────────────────────────

        /// <summary>¿Casan estas dos bocas? <paramref name="reason"/> queda con el motivo si no.</summary>
        public static bool ValidateConnection(in Wg3Socket a, in Wg3Socket b, out string reason)
        {
            if (a.type != b.type) { reason = $"tipo {a.type} contra {b.type}"; return false; }
            if (Mathf.Abs(a.width - b.width) > WidthMatchTolerance)
            { reason = $"anchura {a.width:0.###} contra {b.width:0.###}"; return false; }
            if (Mathf.Abs(a.floorY - b.floorY) > FloorMatchTolerance)
            { reason = $"cota de suelo {a.floorY:0.###} contra {b.floorY:0.###}"; return false; }
            if (Mathf.Min(a.ceilingY, b.ceilingY) - Mathf.Max(a.floorY, b.floorY) < MinHeadroom)
            { reason = "el hueco común no llega al mínimo caminable"; return false; }
            reason = null;
            return true;
        }

        // ── mundo compuesto ─────────────────────────────────────────────────────────────────

        /// <summary>Invariantes que tiene que cumplir un mundo ya compuesto. Es lo que convierte
        /// "conectividad por construcción" (§13) en algo comprobable en vez de declarado.</summary>
        public static List<string> ValidateWorld(Wg3World world)
        {
            var issues = new List<string>();
            if (world == null) { issues.Add("mundo nulo"); return issues; }

            for (int i = 0; i < world.placements.Count; i++)
            {
                Wg3Placement a = world.placements[i];
                for (int j = i + 1; j < world.placements.Count; j++)
                {
                    Wg3Placement b = world.placements[j];
                    if (a.Overlaps(b.originX, b.originZ, b.SizeX, b.SizeZ))
                        issues.Add($"solape entre {a.piece.id}#{i} y {b.piece.id}#{j}");
                }
                for (int s = 0; s < a.socketState.Length; s++)
                    if (a.socketState[s] == Wg3World.SocketOpen)
                        issues.Add($"{a.piece.id}#{i} deja la boca {s} abierta al vacío (L21: falta tapón)");
            }
            return issues;
        }
    }
}
