using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>El servicio de conexión WAN de un router, ya listo para hablarle.</summary>
    public sealed class IgdService
    {
        /// `urn:schemas-upnp-org:service:WANIPConnection:1` o similar. Viaja en la cabecera
        /// `SOAPAction` de cada llamada y tiene que ser el que el dispositivo declaró, no una
        /// constante: un router que expone `:2` rechaza las acciones dirigidas a `:1`.
        public string ServiceType { get; }

        /// URL absoluta del punto de control, ya resuelta.
        public Uri ControlUrl { get; }

        public IgdService(string serviceType, Uri controlUrl)
        {
            ServiceType = serviceType;
            ControlUrl = controlUrl;
        }

        public override string ToString() => $"{ServiceType} @ {ControlUrl}";
    }

    /// <summary>
    /// Lee el XML de descripción de un dispositivo UPnP y saca de él el servicio de conexión WAN.
    /// **Pura**: entra el XML y la URL de donde vino, sale el servicio. La descarga vive en
    /// <see cref="IgdClient"/>.
    ///
    /// Tres cosas que este parseo tiene que hacer bien y que se rompen fácil:
    ///
    /// 1. **El servicio está ANIDADO tres niveles**: `InternetGatewayDevice` → `WANDevice` →
    ///    `WANConnectionDevice` → `serviceList`. Un buscador que sólo mire el `serviceList` de la
    ///    raíz no encuentra nada en ningún router real.
    /// 2. **`controlURL` suele ser relativa** (`/ctl/IPConn`) y hay que resolverla contra
    ///    `URLBase` si el documento lo trae, y contra la `LOCATION` del SSDP si no. Resolverla
    ///    contra la raíz del host cuando hay `URLBase` con path manda las llamadas a un 404.
    /// 3. **El namespace por defecto es `urn:schemas-upnp-org:device-1-0`**, así que buscar
    ///    `Element("service")` a secas devuelve null siempre. Se compara por `LocalName`, que
    ///    además aguanta a los firmwares que se dejan el namespace.
    ///
    /// La preferencia entre servicios es deliberada: `WANIPConnection:2` antes que `:1`, y
    /// `WANPPPConnection:1` el último. Un router con PPPoE expone el PPP y ahí es el único que
    /// hay; uno con las dos cosas responde mejor por el IP.
    /// </summary>
    public static class IgdDescription
    {
        /// En orden de preferencia. El primero que aparezca en el documento gana.
        private static readonly string[] PreferredServiceTypes =
        {
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        };

        /// <summary>
        /// Saca el servicio de conexión WAN, o null si el documento no tiene ninguno (que es lo
        /// que pasa cuando el SSDP lo contestó una impresora, una tele o un NAS: son UPnP y no son
        /// routers).
        /// </summary>
        /// <param name="xml">El cuerpo del XML de descripción.</param>
        /// <param name="location">La URL de la que se descargó, para resolver rutas relativas.</param>
        /// <param name="reason">Por qué no salió nada, cuando no sale nada. Nunca null.</param>
        public static IgdService TryParse(string xml, Uri location, out string reason)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                reason = "la descripción del dispositivo vino vacía";
                return null;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch (Exception e)
            {
                reason = $"la descripción del dispositivo no es XML válido: {e.Message}";
                return null;
            }

            Uri baseUrl = ResolveBase(document, location);
            if (baseUrl == null)
            {
                reason = "no hay URL base con la que resolver el controlURL";
                return null;
            }

            var services = new List<XElement>();
            CollectByLocalName(document.Root, "service", services);

            if (services.Count == 0)
            {
                reason = "el dispositivo no declara ningún servicio";
                return null;
            }

            foreach (string wanted in PreferredServiceTypes)
            {
                foreach (XElement service in services)
                {
                    string type = ChildValue(service, "serviceType");
                    if (!string.Equals(type, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    string control = ChildValue(service, "controlURL");
                    if (string.IsNullOrWhiteSpace(control)) continue;

                    if (!Uri.TryCreate(baseUrl, control.Trim(), out Uri absolute)) continue;
                    if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) continue;

                    reason = "";
                    return new IgdService(type, absolute);
                }
            }

            reason = "el dispositivo responde a UPnP pero no expone conexión WAN " +
                     "(suele ser una impresora, una tele o un NAS, no un router)";
            return null;
        }

        /// <summary>
        /// La base contra la que se resuelven las rutas. `URLBase` manda si está y es absoluta;
        /// si no, la propia `LOCATION`, que es lo que recomienda el propio UPnP 1.1 tras haber
        /// deprecado `URLBase`.
        /// </summary>
        private static Uri ResolveBase(XDocument document, Uri location)
        {
            string declared = null;
            if (document.Root != null)
            {
                foreach (XElement element in document.Root.Elements())
                {
                    if (!string.Equals(element.Name.LocalName, "URLBase", StringComparison.OrdinalIgnoreCase)) continue;
                    declared = element.Value;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(declared) &&
                Uri.TryCreate(declared.Trim(), UriKind.Absolute, out Uri fromDocument))
            {
                return fromDocument;
            }

            return location != null && location.IsAbsoluteUri ? location : null;
        }

        private static void CollectByLocalName(XElement node, string localName, List<XElement> into)
        {
            if (node == null) return;

            foreach (XElement child in node.Elements())
            {
                if (string.Equals(child.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                    into.Add(child);

                CollectByLocalName(child, localName, into);
            }
        }

        private static string ChildValue(XElement parent, string localName)
        {
            foreach (XElement child in parent.Elements())
            {
                if (string.Equals(child.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                    return child.Value;
            }

            return null;
        }
    }
}
