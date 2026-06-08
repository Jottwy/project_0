using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BackroomsSurvival.Net
{
    public static class PortUtility
    {
        public static bool IsTcpPortAvailable(int port)
        {
            if (!IsValidPort(port)) return false;

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public static bool IsUdpPortAvailable(int port)
        {
            if (!IsValidPort(port)) return false;

            try
            {
                using (new UdpClient(new IPEndPoint(IPAddress.Any, port)))
                    return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public static int FindFreeTcpPort(int startPort)
        {
            return FindFreePort(startPort, IsTcpPortAvailable);
        }

        public static int FindFreeUdpPort(int startPort)
        {
            return FindFreePort(startPort, IsUdpPortAvailable);
        }

        private static int FindFreePort(int startPort, Func<int, bool> isAvailable)
        {
            int port = Math.Max(1, Math.Min(65535, startPort));
            for (int i = port; i <= 65535; i++)
            {
                if (isAvailable(i))
                    return i;
            }

            throw new InvalidOperationException($"No free port found at or above {startPort}");
        }

        private static bool IsValidPort(int port) => port > 0 && port <= 65535;
    }
}
