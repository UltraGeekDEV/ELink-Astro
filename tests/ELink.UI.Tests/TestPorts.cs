using System.Net;
using System.Net.Sockets;

namespace ELink.Testing;

/// <summary>Ports for test servers and nodes. Asking the OS for a free port and releasing it is racy when many tests start at
/// once (two can be given the same one, and the second never binds); this hands out each port once per test run.</summary>
public static class TestPorts
{
    private static int _next = 20000 + Random.Shared.Next(0, 20000);
    private static readonly object Gate = new();

    public static int Next()
    {
        lock (Gate)
        {
            for (int tries = 0; tries < 2000; tries++)
            {
                int port = _next++;
                if (_next > 60000) _next = 20000;
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop();
                    return port;
                }
                catch (SocketException) { /* in use: next */ }
            }
            throw new InvalidOperationException("no free port for a test");
        }
    }
}
