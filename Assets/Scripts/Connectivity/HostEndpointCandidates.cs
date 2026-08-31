using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// En qué ORDEN se ofrecen las direcciones del host a la política de anuncio.
    ///
    /// La precedencia entera, contando lo que ya decide <c>LobbyEndpointPolicy</c>:
    ///
    /// 1. **Lo que escribió el humano**, si sirve. Puede saber algo que nosotros no: un reenvío
    ///    hecho a mano en el router, un DNS dinámico, una IP fija.
    /// 2. **La IP pública con mapeo CONFIRMADO.** Confirmado es exigente
    ///    (<see cref="HostConnectivityReport.ConfirmedPublicHost"/>): conocer la IP, que el router
    ///    haya confirmado el reenvío al RELEERLO, y que no haya sospecha de CGNAT.
    /// 3. **La dirección local**, que es lo que se anunciaba antes de que existiera todo esto.
    /// 4. **Nada.** Si no hay ninguna defendible no se anuncia la partida, porque un endpoint malo
    ///    le cuesta 15 s de espera a quien lo elige y no anunciarlo no le cuesta nada.
    ///
    /// Esto sólo hace el paso 2. Los otros tres ya eran de `LobbyEndpointPolicy`, que sigue siendo
    /// **pura y sin E/S**: recibe los candidatos ya ordenados, y eso es exactamente su contrato.
    /// Meter aquí la consulta al router habría metido sockets en una clase que tiene 25 tests
    /// apoyados en que no los tiene.
    /// </summary>
    public static class HostEndpointCandidates
    {
        private static string _cachedFor;
        private static IReadOnlyList<string> _cachedSource;
        private static List<string> _cached;

        /// <summary>
        /// La lista con la IP pública confirmada delante, o la lista tal cual si no hay ninguna.
        ///
        /// **Cacheada por identidad de la lista de origen**: el llamante es un `Update` a 60 Hz y
        /// no puede reservar una lista por frame. La de origen ya viene cacheada 10 s aguas
        /// arriba, así que comparar por referencia basta y es barato.
        /// </summary>
        public static IReadOnlyList<string> WithConfirmedPublicFirst(HostConnectivityReport report,
            IReadOnlyList<string> local)
        {
            string publicHost = report?.ConfirmedPublicHost;
            if (string.IsNullOrEmpty(publicHost)) return local;

            if (_cached != null &&
                ReferenceEquals(_cachedSource, local) &&
                string.Equals(_cachedFor, publicHost, StringComparison.Ordinal))
            {
                return _cached;
            }

            int count = local?.Count ?? 0;
            var combined = new List<string>(count + 1) { publicHost };
            for (int i = 0; i < count; i++)
            {
                // La pública puede aparecer también en la lista local cuando el PC tiene IP
                // pública directa (sin NAT). Duplicarla no rompe nada, pero ensucia el log.
                if (!string.Equals(local[i], publicHost, StringComparison.Ordinal)) combined.Add(local[i]);
            }

            _cachedFor = publicHost;
            _cachedSource = local;
            _cached = combined;
            return combined;
        }

        /// <summary>Tira la caché. Para los tests y para el cambio de sesión.</summary>
        public static void ResetCache()
        {
            _cachedFor = null;
            _cachedSource = null;
            _cached = null;
        }
    }
}
