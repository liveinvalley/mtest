using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

class TransportInfo
{
    private readonly IPAddress localAddress;
    private readonly IPAddress multicastAddress;
    private readonly int port;

    public TransportInfo(string localIp, string multicastIp, string portNumber)
    {
        this.localAddress = IPAddress.Parse(localIp);
        this.multicastAddress = IPAddress.Parse(multicastIp);
        this.port = Int32.Parse(portNumber);

        if ((this.port < 1) || (65535 < this.port)) throw new ArgumentException();
    }

    public IPAddress LocalAddress
    {
        get { return this.localAddress; }
    }

    public IPAddress MulticastAddress
    {
        get { return this.multicastAddress; }
    }

    public int Port
    {
        get { return this.port; }
    }
}

class MulticastUdpStringClient : IDisposable
{
    private readonly UdpClient udpClient;

    private MulticastUdpStringClient(UdpClient udpClient)
    {
        this.udpClient = udpClient;
    }

    private static int GetInterfaceIndex(IPAddress local)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var prop = nic.GetIPProperties().GetIPv4Properties();
            if (prop == null) continue; 

            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address == local)
                {
                    return prop.Index;
                }
            }

        }

        throw new ArgumentException();
    }

    public static MulticastUdpStringClient CreateListenerSSM(TransportInfo info, IPAddress source)
    {
        IPEndPoint local = new IPEndPoint(info.LocalAddress, info.Port);

        UdpClient client = new UdpClient(local);
        //        byte[] membershipAddresses = new byte[12]; // 3 IPs * 4 bytes (IPv4)
        //        Buffer.BlockCopy(info.MulticastAddress.GetAddressBytes(), 0, membershipAddresses, 0, 4);
        //        Buffer.BlockCopy(source.GetAddressBytes(), 0, membershipAddresses, 4, 4);
        //        Buffer.BlockCopy(info.LocalAddress.GetAddressBytes(), 0, membershipAddresses, 8, 4);
        //        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddSourceMembership, membershipAddresses);
        byte[] interfaceIndex = BitConverter.GetBytes((UInt32)GetInterfaceIndex(info.LocalAddress));
        byte[] membershipAddresses = new byte[264]; // GROUP_SOURCE_REQ
        Buffer.BlockCopy(interfaceIndex, 0, membershipAddresses, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((UInt16)info.MulticastAddress.AddressFamily), 0, membershipAddresses, 8, 2);
        Buffer.BlockCopy(info.MulticastAddress.GetAddressBytes(), 0, membershipAddresses, 12, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((UInt16)info.LocalAddress.AddressFamily), 0, membershipAddresses, 136, 2);
        Buffer.BlockCopy(info.LocalAddress.GetAddressBytes(), 0, membershipAddresses, 140, 4);
        client.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName) 45, membershipAddresses);

        return new MulticastUdpStringClient(client);
    }

    public static MulticastUdpStringClient CreateListener(TransportInfo info)
    {
        IPEndPoint local = new IPEndPoint(info.LocalAddress, info.Port);

        UdpClient client = new UdpClient(local);
        client.JoinMulticastGroup(info.MulticastAddress, info.LocalAddress);

        return new MulticastUdpStringClient(client);
    }

    public static MulticastUdpStringClient CreateSource(TransportInfo info)
    {
        var local = new IPEndPoint(info.LocalAddress, 0);

        var client = new UdpClient(local);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

        client.Connect(new IPEndPoint(info.MulticastAddress, info.Port));

        return new MulticastUdpStringClient(client);
    }

    public string ReceiveString(ref IPEndPoint remote)
    {
        byte[] data = udpClient.Receive(ref remote);

        return Encoding.UTF8.GetString(data);
    }

    public void SendString(string str)
    {
        udpClient.Send(Encoding.UTF8.GetBytes(str));
    }

    public void Dispose()
    {
        this.udpClient.Dispose();
    }

}

public class Program
{
    private String[] args;

    private Program(String[] args)
    {
        this.args = args;
    }

    public static void Main(String[] args)
    {
        new Program(args).Run();
    }

    private TransportInfo CreateUdpTransportInfo()
    {
        if (args.Length < 3)
        {
            throw new ArgumentException();
        }

        var local = args.Length == 4 ? args[3] : "0.0.0.0";
        return new TransportInfo(local, args[1], args[2]);
    }

    private bool IsMulticastSenderMode()
    {
        return (args.Length >= 1) && args[0].Equals("/s", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsMulticastListenerMode()
    {
        if ((args.Length >= 1) && args[0].Equals("/l", StringComparison.OrdinalIgnoreCase)) return true;
        if ((args.Length >= 1) && args[0].StartsWith("/l:", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool IsSourceSpecific()
    {
        return (args.Length >= 1) && args[0].StartsWith("/l:", StringComparison.OrdinalIgnoreCase);
    }

    private IPAddress GetSsmSourceIp()
    {
        if ((args.Length < 1) || !args[0].StartsWith("/l:", StringComparison.OrdinalIgnoreCase)) return new IPAddress(0);

        return IPAddress.Parse(args[0].AsSpan(3));
    }

    private void Run()
    {
        try
        {
            if (IsMulticastSenderMode())
            {
                var info = CreateUdpTransportInfo();
                using (var client = MulticastUdpStringClient.CreateSource(info))
                {
                    while (true)
                    {
                        string? str = Console.ReadLine();
                        if (str == null)
                        {
                            break;
                        }

                        client.SendString(str);
                    }

                }

            }

            else if (IsMulticastListenerMode())
            {
                var info = CreateUdpTransportInfo();
                using (var client = IsSourceSpecific() ? MulticastUdpStringClient.CreateListenerSSM(info, GetSsmSourceIp()) : MulticastUdpStringClient.CreateListener(info))
                {
                    while (true)
                    {
                        IPEndPoint remote = new IPEndPoint(0, 0);
                        string str = client.ReceiveString(ref remote);

                        Console.WriteLine("{0}:{1} - {2}", remote.Address.ToString(), remote.Port.ToString(), str);
                    }
                }
            }

            else
            {
                throw new ArgumentException();
            }
        }
        catch
        {
            PrintUsage();
        }
    }

    private void PrintUsage()
    {
        Console.WriteLine("UDPを用いたマルチキャストの送受信テストを行います。");
        Console.WriteLine("");
        Console.WriteLine("  mtest /s mcast port [local]");
        Console.WriteLine("     マルチキャストソースとして動作します。");
        Console.WriteLine("     mcast  マルチキャストアドレスを指定します。");
        Console.WriteLine("     port   送信ポートを指定します。");
        Console.WriteLine("     local  送信元インターフェースのIPアドレスを指定します。");
        Console.WriteLine("");
        Console.WriteLine("  mtest /l mcast port [local]");
        Console.WriteLine("     マルチキャストリスナとして動作します。");
        Console.WriteLine("     mcast  マルチキャストアドレスを指定します。");
        Console.WriteLine("     port   受信ポートを指定します。");
        Console.WriteLine("     local  受信インターフェースのIPアドレスを指定します。");
        Console.WriteLine("");
        Console.WriteLine("  mtest /l:source mcast port [local]");
        Console.WriteLine("     マルチキャストリスナとして動作します（Source Specific Multicast）。");
        Console.WriteLine("     mcast  マルチキャストアドレスを指定します。");
        Console.WriteLine("     port   受信ポートを指定します。");
        Console.WriteLine("     local  受信インターフェースのIPアドレスを指定します。");
        Console.WriteLine("     source マルチキャストソースのIPアドレスを指定します。");
    }
}
