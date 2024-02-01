﻿using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using Sentry;

namespace VRCFaceTracking.Core.OSC.Query.mDNS;

public partial class QueryRegistrar : ObservableObject
{
    private struct AdvertisedService
    {
        public readonly string ServiceName;
        public readonly int Port;
        public readonly IPAddress Address;

        public AdvertisedService(string serviceName, int port, IPAddress address)
        {
            ServiceName = serviceName;
            Port = port;
            Address = address;
        }
    }
    
    private static readonly IPAddress MulticastIp = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MdnsEndpointIp4 = new(MulticastIp, 5353);

    private static readonly Dictionary<IPAddress, UdpClient> Senders = new();
    private static readonly Dictionary<UdpClient, CancellationToken> Receivers = new();
    private static readonly Dictionary<string, AdvertisedService> Services = new();

    public static Action OnVrcClientDiscovered = () => { };

    [ObservableProperty] private static IPEndPoint _vrchatClientEndpoint;

    partial void OnVrchatClientEndpointChanged(IPEndPoint? value) => OnVrcClientDiscovered();
        
    private static List<NetworkInterface> GetIpv4NetInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(net =>
            net.OperationalStatus == OperationalStatus.Up &&
            net.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .ToList();

    // Get all ipv4 addresses from a specific network interface
    private static IEnumerable<IPAddress> GetIpv4Addresses(NetworkInterface net) => net.GetIPProperties()
        .UnicastAddresses
        .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(addr => addr.Address);
        
    public QueryRegistrar()
    {
        // Create listeners for all interfaces
        var receiver = new UdpClient(AddressFamily.InterNetwork);
        receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
        Receivers.Add(receiver, new CancellationToken());

        // For each ip address, create a sender udp client to respond to multicast requests
        var interfaces = GetIpv4NetInterfaces();
        var ipAddresses = interfaces
            .SelectMany(GetIpv4Addresses)
            .Where(addr => addr.AddressFamily == AddressFamily.InterNetwork);
            
        // For every ipv4 address discovered in the network interfaces, create a sender udp client set up grouping
        foreach (var ipAddress in ipAddresses)
        {
            var sender = new UdpClient(ipAddress.AddressFamily);
                
            // Add the local ip address to our multicast group
            receiver.JoinMulticastGroup(MulticastIp, ipAddress);
                
            sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sender.Client.Bind(new IPEndPoint(ipAddress, 5353));    // Bind to the local ip address
            sender.JoinMulticastGroup(MulticastIp);                           // Join the multicast group
            sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                
            Receivers.Add(sender, new CancellationToken());
            Senders.Add(ipAddress, sender);
        }

        foreach (var sender in Receivers)
        {
            Listen(sender.Key, sender.Value);
        }
    }

    private static async void ResolveDnsQueries(DNSPacket packet, IPEndPoint remoteEndpoint)
    {
        if (packet.OPCODE != 0)
        {
            return;
        }

        foreach (var question in packet.questions)
        {
            // Ensure the question has three labels. First for service name, second for protocol, third for domain
            // Ensure the question is for local domain
            // Ensure the question is for the _osc._udp service
            if (question.Labels.Count != 3 
                || question.Labels[2] != "local"
                || !Services.TryGetValue($"{question.Labels[0]}.{question.Labels[1]}", out var service))
            {
                continue;
            }

            //foreach (var service in storedServices)
            {
                var qualifiedServiceName = new List<string>
                {
                    service.ServiceName,
                    question.Labels[0],
                    question.Labels[1],
                    question.Labels[2]
                };

                var serviceName = new List<string>
                {
                    service.ServiceName,
                    question.Labels[0].Trim('_'),
                    question.Labels[1].Trim('_')
                };
                            
                var txt = new TXTRecord { Text = new List<string> { "txtvers=1" } };
                var srv = new SRVRecord { 
                    Port = (ushort)service.Port, 
                    Target = serviceName
                };
                var aRecord = new ARecord { Address = service.Address };
                var ptrRecord = new PTRRecord
                {
                    DomainLabels = qualifiedServiceName
                };
                            
                var additionalRecords = new List<DNSResource>
                {
                    new (txt, qualifiedServiceName),
                    new (srv, qualifiedServiceName),
                    new (aRecord, serviceName)
                };

                var answers = new List<DNSResource> { new (ptrRecord, question.Labels) };

                var response = new DNSPacket
                {
                    CONFLICT = true,
                    ID = 0,
                    OPCODE = 0,
                    QUERYRESPONSE = true,
                    RESPONSECODE = 0,
                    TENTATIVE = false,
                    TRUNCATION = false,
                    questions = Array.Empty<DNSQuestion>(),
                    answers = answers.ToArray(),
                    authorities = Array.Empty<DNSResource>(),
                    additionals = additionalRecords.ToArray()
                };

                var bytes = response.Serialize();

                if (remoteEndpoint.Port == 5353)
                {
                    foreach (var sender in Senders)
                    {
                        await sender.Value.SendAsync(bytes, bytes.Length, MdnsEndpointIp4);
                    }

                    continue;
                }

                var unicastClientIp4 = new UdpClient(AddressFamily.InterNetwork);
                await unicastClientIp4.SendAsync(bytes, bytes.Length, remoteEndpoint);
            }
        }
    }

    private void ResolveVrChatClient(DNSPacket packet, IPEndPoint remoteEndpoint)
    {
        if (!packet.QUERYRESPONSE || packet.answers[0].Type != 12)
        {
            return;
        }

        var ptrRecord = packet.answers[0].Data as PTRRecord;
        if (ptrRecord.DomainLabels.Count != 4 || !ptrRecord.DomainLabels[0].StartsWith("VRChat-Client"))
        {
            return;
        }

        if (packet.answers[0].Labels.Count != 3 || packet.answers[0].Labels[0] != "_oscjson" ||
            packet.answers[0].Labels[1] != "_tcp" || packet.answers[0].Labels[2] != "local")
        {
            return;
        }

        // Now we find the first A record in the additional records
        var aRecord = packet.additionals.FirstOrDefault(r => r.Type == 1);
        var srvRecord = packet.additionals.FirstOrDefault(r => r.Type == 33);
        if (aRecord == null || srvRecord == null)
        {
            return;
        }

        var vrChatClientIp = aRecord.Data as ARecord;
        var vrChatClientPort = srvRecord.Data as SRVRecord;
            
        VrchatClientEndpoint = new IPEndPoint(vrChatClientIp.Address, vrChatClientPort.Port);
    }
        
    private async void Listen(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await client.ReceiveAsync(ct);
            try
            {
                var reader = new BigReader(result.Buffer);
                var packet = new DNSPacket(reader);

                // I'm aware this is cringe, but we do this first as it's a lot more likely vrchat beats us to the punch responding to the query
                ResolveVrChatClient(packet, result.RemoteEndPoint);
                ResolveDnsQueries(packet, result.RemoteEndPoint);
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e, scope => scope.SetExtra("bytes", result.Buffer));
            }
        }
    }

    public static void Advertise(string serviceName, string instanceName, int port, IPAddress address)
    {
        // If we're already advertising on this service, we can just update it
        if (Services.ContainsKey(serviceName))
        {
            Services[serviceName] = new AdvertisedService(instanceName, port, address);
        }
        else
        {
            Services.Add(serviceName, new AdvertisedService(instanceName, port, address));
        }
    }
}