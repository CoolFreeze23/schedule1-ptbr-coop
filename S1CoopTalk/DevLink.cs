using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace S1CoopTalk
{
    /// <summary>Test builds only: two game processes on one PC have no shared Steam lobby, so they talk over loopback.</summary>
    public static class DevLink
    {
        private const int PortA = 47811, PortB = 47812;
        private static UdpClient _udp;
        private static int _peer;

        public static void Start()
        {
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, PortA));
                _peer = PortB;
            }
            catch (SocketException)
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, PortB));
                _peer = PortA;
            }
            Mod.Log.Msg("DevLink peer port " + _peer);
        }

        public static void Send(string payload)
        {
            byte[] raw = Encoding.UTF8.GetBytes(payload);
            _udp?.Send(raw, raw.Length, new IPEndPoint(IPAddress.Loopback, _peer));
        }

        public static void Tick()
        {
            while (_udp != null && _udp.Available > 0)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] raw = _udp.Receive(ref from);
                Net.DevReceive(Encoding.UTF8.GetString(raw));
            }
        }
    }
}
