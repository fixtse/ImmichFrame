using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace ImmichFrame.WebApi.Helpers;

// Announces this server as `_immichframe._tcp` over multicast DNS so OpenNextion
// panels can find it, like Home Assistant's own discovery. It answers PTR
// queries for the service with PTR, SRV, TXT and A records and announces itself
// at start. Set OPENNEXTION_MDNS=false to turn it off. In Docker the packets only
// reach the network with host networking.
//
// Environment: OPENNEXTION_MDNS_NAME (instance name, default the host name),
// OPENNEXTION_URL (address panels should use; default http://<ip>:<port>) and
// OPENNEXTION_MDNS_PORT (default from ASPNETCORE_HTTP_PORTS/URLS, else 8080).
public sealed class OpenNextionMdns(ILogger<OpenNextionMdns> logger) : BackgroundService
{
    private const string Service = "_immichframe._tcp.local";
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("OPENNEXTION_MDNS"), "false", StringComparison.OrdinalIgnoreCase))
            return;
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            udp.JoinMulticastGroup(Group);
            udp.MulticastLoopback = false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OpenNextion discovery (mDNS) is unavailable");
            return;
        }

        using (udp)
        {
            var target = new IPEndPoint(Group, MdnsPort);
            logger.LogInformation("Announcing ImmichFrame for OpenNextion panels over mDNS");
            for (var i = 0; i < 2; i++)
            {
                await Send(udp, target, stoppingToken);
                await Task.Delay(1000, stoppingToken);
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await udp.ReceiveAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                if (AsksForService(received.Buffer))
                    await Send(udp, target, stoppingToken);
            }
        }
    }

    private async Task Send(UdpClient udp, IPEndPoint target, CancellationToken token)
    {
        try
        {
            var packet = BuildResponse(InstanceName(), HostName(), Port(), Addresses(),
                Environment.GetEnvironmentVariable("OPENNEXTION_URL"), Version());
            await udp.SendAsync(packet, target, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "mDNS announcement failed");
        }
    }

    // True for a query (QR=0) with a question for the service (PTR or ANY).
    internal static bool AsksForService(byte[] packet)
    {
        if (packet.Length < 12 || (packet[2] & 0x80) != 0) return false;
        var questions = packet[4] << 8 | packet[5];
        var offset = 12;
        for (var q = 0; q < questions; q++)
        {
            var name = ReadName(packet, ref offset);
            if (name is null || offset + 4 > packet.Length) return false;
            var type = packet[offset] << 8 | packet[offset + 1];
            offset += 4;
            if ((type == 12 || type == 255) && string.Equals(name, Service, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? ReadName(byte[] p, ref int offset)
    {
        var labels = new List<string>();
        var jumped = false;
        var pos = offset;
        for (var guard = 0; guard < 64; guard++)
        {
            if (pos >= p.Length) return null;
            int len = p[pos];
            if (len == 0)
            {
                if (!jumped) offset = pos + 1;
                return string.Join('.', labels);
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= p.Length) return null;
                if (!jumped) offset = pos + 2;
                pos = (len & 0x3F) << 8 | p[pos + 1];
                jumped = true;
                continue;
            }
            if (pos + 1 + len > p.Length) return null;
            labels.Add(Encoding.UTF8.GetString(p, pos + 1, len));
            pos += 1 + len;
        }
        return null;
    }

    internal static byte[] BuildResponse(string instance, string host, int port, IReadOnlyList<IPAddress> addresses,
        string? url, string version)
    {
        var instanceName = instance + "." + Service;
        var hostName = host + ".local";
        var txt = new List<string> { "name=" + instance, "version=" + version, "path=/api/Asset/OpenNextion" };
        if (!string.IsNullOrWhiteSpace(url)) txt.Add("url=" + url.Trim());
        else if (addresses.Count > 0) txt.Add("url=http://" + addresses[0] + ":" + port);

        using var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        void Name(string n)
        {
            foreach (var label in n.Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                var len = Math.Min(b.Length, 63);
                ms.WriteByte((byte)len);
                ms.Write(b, 0, len);
            }
            ms.WriteByte(0);
        }
        void Record(string name, int type, bool flush, uint ttl, byte[] data)
        {
            Name(name);
            U16(type);
            U16(flush ? 0x8001 : 0x0001);
            U32(ttl);
            U16(data.Length);
            ms.Write(data);
        }
        byte[] Data(Action<MemoryStream> write)
        {
            using var d = new MemoryStream();
            write(d);
            return d.ToArray();
        }
        static void DName(MemoryStream d, string n)
        {
            foreach (var label in n.Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                var len = Math.Min(b.Length, 63);
                d.WriteByte((byte)len);
                d.Write(b, 0, len);
            }
            d.WriteByte(0);
        }

        var answers = 3 + addresses.Count;
        U16(0); U16(0x8400); U16(0); U16(answers); U16(0); U16(0);
        Record(Service, 12, false, 4500, Data(d => DName(d, instanceName)));
        Record(instanceName, 33, true, 120, Data(d =>
        {
            d.Write([0, 0, 0, 0, (byte)(port >> 8), (byte)port]);
            DName(d, hostName);
        }));
        Record(instanceName, 16, true, 4500, Data(d =>
        {
            foreach (var t in txt)
            {
                var b = Encoding.UTF8.GetBytes(t);
                var len = Math.Min(b.Length, 255);
                d.WriteByte((byte)len);
                d.Write(b, 0, len);
            }
        }));
        foreach (var a in addresses)
            Record(hostName, 1, true, 120, a.GetAddressBytes());
        return ms.ToArray();
    }

    private static string InstanceName()
    {
        var name = Environment.GetEnvironmentVariable("OPENNEXTION_MDNS_NAME");
        return string.IsNullOrWhiteSpace(name) ? "ImmichFrame " + HostName() : name.Trim();
    }

    private static string HostName()
    {
        var host = new string(Dns.GetHostName().Split('.')[0].Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return host.Length == 0 ? "immichframe" : host;
    }

    internal static int Port()
    {
        foreach (var key in new[] { "OPENNEXTION_MDNS_PORT", "ASPNETCORE_HTTP_PORTS" })
        {
            var value = Environment.GetEnvironmentVariable(key)?.Split(';', ',')[0];
            if (int.TryParse(value, out var p) && p is > 0 and < 65536) return p;
        }
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0];
        if (urls != null)
        {
            var colon = urls.LastIndexOf(':');
            if (colon > 0 && int.TryParse(urls[(colon + 1)..].TrimEnd('/'), out var p) && p is > 0 and < 65536) return p;
        }
        return 8080;
    }

    private static IReadOnlyList<IPAddress> Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .OrderBy(a => a.GetAddressBytes()[0] == 172 ? 1 : 0)  // Docker bridges last
            .Take(4)
            .ToList();

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
}
