namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// Si el host está detrás de un NAT de operador (CGNAT) o de un doble NAT, en la medida en que
    /// se pueda saber **sin cambiar de transporte**.
    ///
    /// Importa porque es el único fallo de esta lista que **no tiene arreglo por configuración**.
    /// Un firewall se abre, un UPnP se enciende, un puerto se reenvía a mano; un CGNAT no: la IP
    /// pública la comparten cientos de abonados y el reenvío de puertos no existe para el cliente.
    /// Decírselo al usuario le ahorra una tarde entera de tocar el router.
    ///
    /// **Es una heurística y se llama así.** No hay forma de demostrarlo sin STUN, y STUN está
    /// fuera del encargo por la regla de parada. Lo que hay aquí son indicios, cada uno con su
    /// motivo, y un veredicto que nunca dice "seguro".
    ///
    /// Los tres indicios, por orden de fuerza:
    /// 1. La WAN del router está en `100.64.0.0/10` (RFC 6598). Ese rango existe **sólo** para el
    ///    NAT del operador; verlo ahí es prácticamente concluyente.
    /// 2. La WAN del router es RFC 1918. Hay otro router por encima: doble NAT. Puede ser del
    ///    operador o del propio usuario (un router detrás del router del ISP), y las dos son un
    ///    problema para el reenvío, así que se avisa igual.
    /// 3. Lo que el router cree que es su WAN **no coincide** con lo que el mundo ve. Alguien
    ///    traduce por encima.
    ///
    /// Pura: entran dos cadenas y sale un veredicto. Sin sockets ni Unity.
    /// </summary>
    public static class CgnatHeuristic
    {
        public enum Verdict
        {
            /// No hay datos para opinar. **No es lo mismo que "no hay CGNAT"** y por eso no se
            /// llama `Unlikely`: sin la WAN del router no se puede mirar ninguno de los indicios.
            Unknown,

            /// La WAN del router es pública y coincide con lo que ve el mundo. Es lo que se
            /// espera de una conexión donde el reenvío de puertos puede funcionar.
            Unlikely,

            /// Al menos un indicio. Con este transporte no hay conexiones entrantes directas.
            Suspected,
        }

        /// <summary>
        /// Dictamina. `gatewayWanIp` es lo que dijo `GetExternalIPAddress` del IGD (null si no hay
        /// IGD); `observedPublicIp` es lo que devolvió un eco HTTP externo (null si no se
        /// consultó o falló).
        /// </summary>
        /// <param name="reason">Frase para el log y para la UI. Nunca null.</param>
        public static Verdict Evaluate(string gatewayWanIp, string observedPublicIp, out string reason)
        {
            NatAddressPolicy.PublicAddressKind wan = NatAddressPolicy.Classify(gatewayWanIp);

            switch (wan)
            {
                case NatAddressPolicy.PublicAddressKind.CarrierGradeNat:
                    reason = $"la WAN del router es {gatewayWanIp}, que está en el rango 100.64/10 " +
                             "que RFC 6598 reserva al NAT del operador";
                    return Verdict.Suspected;

                case NatAddressPolicy.PublicAddressKind.PrivateRfc1918:
                    reason = $"la WAN del router es {gatewayWanIp}, una dirección privada: hay otro " +
                             "router por encima (doble NAT)";
                    return Verdict.Suspected;

                case NatAddressPolicy.PublicAddressKind.Public:
                    break;

                default:
                    // Sin IGD, o con una WAN que no es una dirección de host. El eco externo por sí
                    // solo NO sirve para este juicio: toda máquina detrás de CGNAT ve una IP
                    // pública perfectamente normal ahí. Es justo el dato que engaña.
                    reason = gatewayWanIp == null
                        ? "no se pudo leer la WAN del router, así que no hay con qué juzgarlo"
                        : $"la WAN del router ({gatewayWanIp}) es {NatAddressPolicy.Describe(wan)}, " +
                          "no una dirección de host con la que juzgar";
                    return Verdict.Unknown;
            }

            // A partir de aquí la WAN es pública. Falta cotejarla con lo que ve el mundo.
            if (!NatAddressPolicy.IsPubliclyRoutable(observedPublicIp))
            {
                reason = $"la WAN del router ({gatewayWanIp}) es pública, pero no se pudo contrastar " +
                         "con lo que ve el mundo";
                return Verdict.Unlikely;
            }

            if (!string.Equals(gatewayWanIp.Trim(), observedPublicIp.Trim(), System.StringComparison.Ordinal))
            {
                reason = $"el router cree que su WAN es {gatewayWanIp} pero el mundo ve " +
                         $"{observedPublicIp}: alguien traduce por encima del router";
                return Verdict.Suspected;
            }

            reason = $"la WAN del router ({gatewayWanIp}) es pública y coincide con lo que ve el mundo";
            return Verdict.Unlikely;
        }
    }
}
