using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// El SOAP de UPnP, sin sockets: construir la petición y leer la respuesta. La conversación
    /// vive en <see cref="IgdClient"/>.
    ///
    /// Está separado porque es donde de verdad se falla —una acción mal formada la rechaza el
    /// router con un 500 que no explica nada— y porque así se puede probar sin router.
    /// </summary>
    public static class IgdSoap
    {
        /// <summary>Descripción que verá el usuario en la tabla de reenvíos de su router.</summary>
        public const string MappingDescription = "Backrooms Survival";

        /// <summary>
        /// El valor de la cabecera `SOAPAction`, comillas incluidas. **Las comillas no son
        /// opcionales**: sin ellas hay firmware que devuelve `401 Invalid Action` sin más pista.
        /// </summary>
        public static string ActionHeader(string serviceType, string action) => $"\"{serviceType}#{action}\"";

        /// <summary>
        /// El sobre SOAP de una acción. Los argumentos van EN ORDEN: UPnP los define posicionales
        /// pese a estar escritos con nombre, y hay routers que rechazan un orden distinto del del
        /// SCPD. Por eso la firma pide una lista y no un diccionario.
        /// </summary>
        public static string BuildEnvelope(string serviceType, string action,
            IReadOnlyList<KeyValuePair<string, string>> arguments)
        {
            var sb = new StringBuilder(512);
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
            sb.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
            sb.Append("<s:Body>");
            sb.Append("<u:").Append(action).Append(" xmlns:u=\"").Append(Escape(serviceType)).Append("\">");

            if (arguments != null)
            {
                for (int i = 0; i < arguments.Count; i++)
                {
                    KeyValuePair<string, string> argument = arguments[i];
                    sb.Append('<').Append(argument.Key).Append('>');
                    sb.Append(Escape(argument.Value));
                    sb.Append("</").Append(argument.Key).Append('>');
                }
            }

            sb.Append("</u:").Append(action).Append('>');
            sb.Append("</s:Body></s:Envelope>");
            return sb.ToString();
        }

        /// <summary>
        /// El valor de un elemento de la respuesta por nombre local, o null.
        ///
        /// Se busca por nombre local en TODO el árbol a propósito: la respuesta viene envuelta en
        /// dos namespaces (el de SOAP y el del servicio) y hay firmware que se deja alguno. Buscar
        /// con el nombre cualificado funciona con el router de quien escribió el código y falla
        /// con el del usuario.
        /// </summary>
        public static string ReadValue(string xml, string localName)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;

            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch (Exception)
            {
                return null;
            }

            return FindValue(document.Root, localName);
        }

        /// <summary>
        /// Lee un fallo UPnP. Devuelve false si la respuesta no es un fallo — que es lo normal.
        ///
        /// El código importa mucho más que el texto: `718` (conflicto) pide reintentar con otro
        /// puerto, `725` pide repetir sin caducidad, y `714` sobre una relectura significa
        /// sencillamente que el mapeo no está.
        /// </summary>
        public static bool TryReadFault(string xml, out int errorCode, out string description)
        {
            errorCode = 0;
            description = null;
            if (string.IsNullOrWhiteSpace(xml)) return false;

            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch (Exception)
            {
                return false;
            }

            string raw = FindValue(document.Root, "errorCode");
            if (raw == null) return false;
            if (!int.TryParse(raw.Trim(), out errorCode)) return false;

            description = FindValue(document.Root, "errorDescription") ?? DescribeError(errorCode);
            return true;
        }

        /// <summary>
        /// Los códigos que este código sabe interpretar. El resto se registra con su número, que
        /// es lo único honesto que se puede hacer con el error propietario de un firmware.
        /// </summary>
        public static string DescribeError(int errorCode)
        {
            switch (errorCode)
            {
                case 401: return "InvalidAction — el servicio no conoce esa acción";
                case 402: return "InvalidArgs — argumentos mal formados o en otro orden";
                case 501: return "ActionFailed — el router la rechazó sin decir por qué";
                case 606: return "ActionNotAuthorized — el router exige autenticación";
                case 714: return "NoSuchEntryInArray — no hay mapeo con esos datos";
                case 715: return "WildCardNotPermittedInSrcIP";
                case 716: return "WildCardNotPermittedInExtPort";
                case 718: return "ConflictInMappingEntry — ese puerto externo ya está mapeado a otro";
                case 724: return "SamePortValuesRequired — este router exige puerto externo = interno";
                case 725: return "OnlyPermanentLeasesSupported — hay que pedirlo sin caducidad";
                case 726: return "RemoteHostOnlySupportsWildcard";
                case 727: return "ExternalPortOnlySupportsWildcard";
                default: return $"error UPnP {errorCode}";
            }
        }

        /// <summary>El router exige mapeos sin caducidad (`725`), así que hay que reintentar.</summary>
        public static bool DemandsPermanentLease(int errorCode) => errorCode == 725;

        /// <summary>Ese puerto externo ya lo tiene otro (`718`), así que hay que probar otro.</summary>
        public static bool IsPortConflict(int errorCode) => errorCode == 718;

        /// <summary>La relectura dice que no hay tal mapeo (`714`). No es un error de transporte.</summary>
        public static bool IsNoSuchEntry(int errorCode) => errorCode == 714;

        private static string FindValue(XElement node, string localName)
        {
            if (node == null) return null;

            foreach (XElement child in node.Elements())
            {
                if (string.Equals(child.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                    return child.Value;

                string nested = FindValue(child, localName);
                if (nested != null) return nested;
            }

            return null;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }
    }
}
