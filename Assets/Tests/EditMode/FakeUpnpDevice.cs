using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Un router UPnP de mentira que habla HTTP de verdad, en loopback y en un puerto efímero.
    ///
    /// **Por qué un socket y no un mock.** Lo que puede fallar en <c>IgdGateway</c> no es la
    /// lógica sino el trato con HTTP: que el `SOAPAction` viaje, que el `Content-Length` cuadre,
    /// que un fallo SOAP llegue como **HTTP 500 con cuerpo** y que el cuerpo se lea de la
    /// excepción en vez de perderse. Un doble de prueba que devolviera cadenas no probaría nada de
    /// eso, que es justo lo que se rompe contra un router real.
    ///
    /// No se usa <c>HttpListener</c> a propósito: en Windows exige reserva de espacio de nombres en
    /// HTTP.SYS y falla para un usuario sin privilegios. <c>TcpListener</c> en `127.0.0.1` no pide
    /// permiso a nadie, y aquí el servidor es media docena de líneas.
    /// </summary>
    public sealed class FakeUpnpDevice : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly Queue<Func<string, string>> _responders = new Queue<Func<string, string>>();
        private readonly List<string> _requests = new List<string>();
        private readonly object _gate = new object();
        private volatile bool _running = true;

        /// Cuánto se queda callado el servidor antes de contestar. Con esto se prueba el tope de
        /// tiempo del cliente sin depender de ningún router lento de verdad.
        public int ResponseDelayMs { get; set; }

        public Uri ControlUrl { get; }

        public FakeUpnpDevice()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            ControlUrl = new Uri($"http://127.0.0.1:{port}/ctl/IPConn");

            _thread = new Thread(Serve) { IsBackground = true, Name = "FakeUpnpDevice" };
            _thread.Start();
        }

        /// <summary>Los cuerpos que ya ha recibido, en orden. Es donde se comprueba qué se envió.</summary>
        public IReadOnlyList<string> Requests
        {
            get { lock (_gate) return _requests.ToArray(); }
        }

        /// <summary>Encola una respuesta 200 con ese cuerpo.</summary>
        public void EnqueueOk(string body) => Enqueue(_ => HttpResponse(200, "OK", body));

        /// <summary>
        /// Encola un fallo UPnP. Va como **HTTP 500**, que es como lo manda un router de verdad:
        /// el cliente tiene que sacar el cuerpo de la excepción o pierde el código de error.
        /// </summary>
        public void EnqueueFault(int errorCode, string description)
        {
            string body =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault>" +
                "<faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
                "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">" +
                $"<errorCode>{errorCode}</errorCode><errorDescription>{description}</errorDescription>" +
                "</UPnPError></detail></s:Fault></s:Body></s:Envelope>";

            Enqueue(_ => HttpResponse(500, "Internal Server Error", body));
        }

        public void Enqueue(Func<string, string> responder)
        {
            lock (_gate) _responders.Enqueue(responder);
        }

        private void Serve()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    using (client)
                    using (NetworkStream stream = client.GetStream())
                    {
                        string body = ReadRequest(stream, out string headers);
                        lock (_gate) _requests.Add(headers + "\r\n\r\n" + body);

                        if (ResponseDelayMs > 0) Thread.Sleep(ResponseDelayMs);
                        if (!_running) return;

                        Func<string, string> responder;
                        lock (_gate)
                        {
                            responder = _responders.Count > 0
                                ? _responders.Dequeue()
                                : (_ => HttpResponse(500, "Internal Server Error", ""));
                        }

                        byte[] payload = Encoding.UTF8.GetBytes(responder(body));
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                }
                catch (Exception)
                {
                    // El listener cerrado durante Dispose entra por aquí. Nada que decir.
                    try { client?.Close(); }
                    catch (Exception) { /* ya estaba cerrado */ }
                    if (!_running) return;
                }
            }
        }

        private static string ReadRequest(Stream stream, out string headers)
        {
            var raw = new MemoryStream();
            var one = new byte[1];
            int contentLength = 0;
            headers = "";

            // Cabeceras, byte a byte hasta la línea en blanco. Es lento y da igual: son peticiones
            // de doscientos bytes en loopback.
            while (true)
            {
                int read = stream.Read(one, 0, 1);
                if (read <= 0) break;
                raw.WriteByte(one[0]);

                string text = Encoding.ASCII.GetString(raw.ToArray());
                if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal)) continue;

                headers = text.Substring(0, text.Length - 4);
                foreach (string line in headers.Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                    int.TryParse(trimmed.Substring("Content-Length:".Length).Trim(), out contentLength);
                }

                break;
            }

            if (contentLength <= 0) return "";

            var body = new byte[contentLength];
            int got = 0;
            while (got < contentLength)
            {
                int read = stream.Read(body, got, contentLength - got);
                if (read <= 0) break;
                got += read;
            }

            return Encoding.UTF8.GetString(body, 0, got);
        }

        private static string HttpResponse(int status, string reason, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
            return $"HTTP/1.1 {status} {reason}\r\n" +
                   "Content-Type: text/xml; charset=\"utf-8\"\r\n" +
                   $"Content-Length: {payload.Length}\r\n" +
                   "Connection: close\r\n" +
                   "\r\n" +
                   (body ?? "");
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); }
            catch (Exception) { /* parar dos veces no es noticia */ }
            _thread.Join(2000);
        }
    }
}
