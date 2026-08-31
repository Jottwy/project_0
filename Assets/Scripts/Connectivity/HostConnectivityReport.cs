using System;
using System.Collections.Generic;
using System.Text;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// Hasta dónde llegó de verdad la preparación del host para aceptar conexiones de fuera.
    ///
    /// **Es una ESCALERA, no un booleano, y ésa es toda la razón de que exista este tipo.** La
    /// tentación es guardar `bool internetReady` y ponerlo a true en cuanto se conoce la IP
    /// pública. Conocer la IP pública no demuestra absolutamente nada: el router puede no tener
    /// UPnP, puede tenerlo y rechazar el mapeo, puede aceptarlo y no aplicarlo, y por encima de
    /// todo eso puede haber un CGNAT que hace el reenvío imposible. Cada uno de esos fallos es
    /// invisible desde el peldaño anterior.
    ///
    /// Los peldaños son acumulativos en la práctica pero **no se derivan unos de otros**: se
    /// encienden por evidencia, cada uno con su propia prueba.
    /// </summary>
    [Flags]
    public enum ConnectivityRung
    {
        None = 0,

        /// Sabemos qué IP pública nos ve el mundo. **No implica que nadie pueda llegar a ella.**
        PublicIpKnown = 1 << 0,

        /// Se le PIDIÓ al router un mapeo de puerto. Sólo dice que la petición salió.
        PortMappingRequested = 1 << 1,

        /// El router confirmó el mapeo **releyéndolo** (`GetSpecificPortMappingEntry`) y el
        /// cliente interno y el puerto coinciden con los nuestros. Un `AddPortMapping` que
        /// devuelve OK no basta: hay routers que aceptan y no aplican.
        PortMappingConfirmed = 1 << 2,

        /// El endpoint quedó publicado en el lobby de Steam. Es lo que un desconocido puede leer.
        EndpointPublished = 1 << 3,

        /// Se ha visto entrar a un peer remoto. **No demuestra que el camino público funcione**:
        /// un joiner de la misma LAN enciende este peldaño igual. Quién entró por dónde lo sabe el
        /// backend, no Unity.
        RemotePeerObserved = 1 << 4,
    }

    /// <summary>
    /// Por qué la escalera se quedó donde se quedó. Los nombres son los del encargo y viajan
    /// LITERALES al log: se buscan con grep desde el `Player.log` de la máquina de un tester, que
    /// es donde de verdad se diagnostica esto.
    /// </summary>
    public enum ConnectivityCode
    {
        /// No se encontró ningún IGD en la red: el SSDP no obtuvo respuesta, o el dispositivo que
        /// contestó no expone servicio de conexión WAN. Es lo normal en redes de empresa, con el
        /// UPnP apagado en el router, y en la máquina donde se escribió esto.
        UpnpUnavailable,

        /// No se intentó siquiera: apagado por configuración. Se distingue de
        /// <see cref="UpnpUnavailable"/> a propósito — "no hay router que lo soporte" y "no
        /// quisimos preguntar" mandan a mirar sitios distintos.
        UpnpDisabled,

        /// El IGD está y contestó, pero el mapeo no llegó a confirmarse: error SOAP, conflicto de
        /// puerto, o releerlo devolvió otra cosa.
        UpnpMappingFailed,

        /// Hay indicios de NAT de operador o doble NAT. Con este transporte **no tiene arreglo**;
        /// ver <see cref="CgnatHeuristic"/>.
        CgnatSuspected,

        /// Ninguna fuente pudo decir cuál es la IP pública.
        PublicEndpointUnknown,
    }

    /// <summary>
    /// Instantánea de la escalera. Mutable y de un solo dueño (el orquestador la va rellenando
    /// según llegan las evidencias); todo lo que lee es de sólo lectura.
    ///
    /// Sin Unity dentro: se construye y se comprueba en un test headless, que es donde se prueban
    /// las combinaciones raras (mapeo confirmado sin IP pública, IP pública sin mapeo, …).
    /// </summary>
    public sealed class HostConnectivityReport
    {
        private readonly List<ConnectivityCode> _codes = new List<ConnectivityCode>();

        public ConnectivityRung Rungs { get; private set; }

        /// La IP pública, o null. Puede estar puesta y NO tener
        /// <see cref="ConnectivityRung.PublicIpKnown"/> nunca: se ponen juntas por
        /// <see cref="SetPublicIp"/>, que es el único camino.
        public string PublicIp { get; private set; }

        /// De dónde salió la IP pública. Importa: la del IGD es lo que el router cree que es su
        /// WAN, la del eco HTTP es lo que el mundo ve. **Que discrepen es la señal de CGNAT.**
        public PublicIpSource PublicIpSource { get; private set; } = PublicIpSource.None;

        /// La dirección de la interfaz por la que sale esta máquina. Es el cliente interno del
        /// mapeo y el respaldo para un joiner de la misma LAN.
        public string LanIp { get; private set; }

        /// El puerto UDP realmente elegido, no el tecleado.
        public int Port { get; private set; }

        /// Lo que se publicó como `connect_ip`, o null si no se publicó nada.
        public string PublishedHost { get; private set; }

        public IReadOnlyList<ConnectivityCode> Codes => _codes;

        public bool Has(ConnectivityRung rung) => (Rungs & rung) == rung;

        public bool HasCode(ConnectivityCode code) => _codes.Contains(code);

        public void SetLan(string lanIp, int port)
        {
            LanIp = lanIp;
            Port = port;
        }

        /// <summary>
        /// Registra la IP pública. Una dirección que no sea públicamente enrutable **no enciende
        /// el peldaño**: si el router dice que su WAN es `100.64.x.x` eso no es "conocer la IP
        /// pública", es descubrir que no hay ninguna.
        /// </summary>
        public void SetPublicIp(string ip, PublicIpSource source)
        {
            PublicIp = ip;
            PublicIpSource = source;
            if (NatAddressPolicy.IsPubliclyRoutable(ip)) Rungs |= ConnectivityRung.PublicIpKnown;
        }

        public void MarkMappingRequested() => Rungs |= ConnectivityRung.PortMappingRequested;

        /// <summary>
        /// Sólo la llama quien RELEYÓ el mapeo del router y comprobó cliente y puerto. Exige
        /// además que se haya pedido: confirmar algo que nunca se pidió sería un bug del
        /// orquestador, y aquí se corta en vez de propagarse a la UI.
        /// </summary>
        public void MarkMappingConfirmed()
        {
            if (!Has(ConnectivityRung.PortMappingRequested)) return;
            Rungs |= ConnectivityRung.PortMappingConfirmed;
        }

        public void MarkEndpointPublished(string host)
        {
            PublishedHost = host;
            Rungs |= ConnectivityRung.EndpointPublished;
        }

        public void MarkRemotePeerObserved() => Rungs |= ConnectivityRung.RemotePeerObserved;

        /// <summary>Añade un código. Idempotente: el mismo código no se apila.</summary>
        public void AddCode(ConnectivityCode code)
        {
            if (!_codes.Contains(code)) _codes.Add(code);
        }

        /// <summary>
        /// Deshace el mapeo en el registro (no en el router — de eso se encarga el cliente IGD).
        /// El teardown la usa para que un informe reutilizado no siga afirmando que hay un mapeo
        /// abierto después de borrarlo.
        /// </summary>
        public void ClearMapping()
        {
            Rungs &= ~(ConnectivityRung.PortMappingRequested | ConnectivityRung.PortMappingConfirmed);
        }

        /// <summary>
        /// Lo que se puede AFIRMAR, en una frase, sin inventar nada. Deliberadamente incapaz de
        /// decir "internet funciona": el peldaño que lo demostraría —una conexión entrante de
        /// fuera de la LAN— no lo puede observar Unity.
        /// </summary>
        public string DescribeReachability()
        {
            if (HasCode(ConnectivityCode.CgnatSuspected))
                return "El host parece estar detrás de CGNAT y no puede aceptar conexiones directas.";

            if (Has(ConnectivityRung.PortMappingConfirmed) && Has(ConnectivityRung.PublicIpKnown))
                return "El router confirmó el reenvío del puerto. No está comprobado que entre nadie: " +
                       "sólo lo demuestra una conexión real desde fuera.";

            if (Has(ConnectivityRung.PortMappingRequested))
                return "Se pidió el reenvío del puerto pero el router no lo confirmó. " +
                       "Puede que funcione y puede que no.";

            if (Has(ConnectivityRung.PublicIpKnown))
                return "Se conoce la IP pública, pero no hay reenvío de puerto. " +
                       "Conocer la IP no basta para que entren.";

            return "No hay endpoint público conocido. La partida sirve en LAN.";
        }

        /// <summary>
        /// Una línea estable para el log. Formato `clave=valor` a propósito: es lo que ya usan
        /// `NETPROBE` y `MPTRACE`, y se filtra con grep desde el log de un tester.
        /// </summary>
        public string ToLogLine()
        {
            var sb = new StringBuilder("NATPROBE ");
            sb.Append("lan=").Append(LanIp ?? "<none>");
            sb.Append(" port=").Append(Port);
            sb.Append(" public=").Append(PublicIp ?? "<unknown>");
            sb.Append(" public_src=").Append(PublicIpSource);
            sb.Append(" published=").Append(PublishedHost ?? "<none>");
            sb.Append(" rungs=").Append(Rungs == ConnectivityRung.None ? "None" : Rungs.ToString());
            sb.Append(" codes=");
            if (_codes.Count == 0)
            {
                sb.Append("<none>");
            }
            else
            {
                for (int i = 0; i < _codes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(CodeName(_codes[i]));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// El nombre LITERAL del encargo (`UPNP_UNAVAILABLE`, …). No es
        /// <c>code.ToString()</c>: eso daría `UpnpUnavailable`, y entonces el grep que un humano
        /// escribe leyendo la documentación no encontraría nada.
        /// </summary>
        public static string CodeName(ConnectivityCode code)
        {
            switch (code)
            {
                case ConnectivityCode.UpnpUnavailable: return "UPNP_UNAVAILABLE";
                case ConnectivityCode.UpnpDisabled: return "UPNP_DISABLED";
                case ConnectivityCode.UpnpMappingFailed: return "UPNP_MAPPING_FAILED";
                case ConnectivityCode.CgnatSuspected: return "CGNAT_SUSPECTED";
                case ConnectivityCode.PublicEndpointUnknown: return "PUBLIC_ENDPOINT_UNKNOWN";
                default: return code.ToString();
            }
        }
    }

    /// <summary>De dónde salió la IP pública.</summary>
    public enum PublicIpSource
    {
        None,

        /// `GetExternalIPAddress` del propio router. La más fiable y sin terceros: es la única
        /// que dice lo que el ROUTER cree, que es lo que hace falta para oler el CGNAT.
        InternetGatewayDevice,

        /// Un servicio HTTP de eco. Dice lo que el mundo ve, que puede no ser lo mismo.
        ExternalEcho,

        /// La escribió el humano en el panel.
        ManualField,
    }
}
