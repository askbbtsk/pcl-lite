using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCL.Core.Link.Ipv6Direct;

public sealed record Ipv6Candidate(
    IPAddress Address,
    string InterfaceName,
    bool IsTemporary,
    SuffixOrigin SuffixOrigin);

/// <summary>
/// 检测本机可用的公网单播 IPv6 地址（自动剔除 fe80::、fc00::/7、Teredo、IPv4 映射等不可直连地址）。
/// </summary>
public static class Ipv6AddressDetector
{
    public static IReadOnlyList<Ipv6Candidate> GetPublicIpv6Addresses()
    {
        var osOutbound = GetOsPreferredOutboundIpv6();
        var candidates = new List<(Ipv6Candidate Candidate, int Score)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties props;
            try
            {
                props = ni.GetIPProperties();
            }
            catch
            {
                continue;
            }

            var hasIpv6Gateway = props.GatewayAddresses.Any(
                g => g.Address.AddressFamily == AddressFamily.InterNetworkV6);
            var isOsOutboundInterface = osOutbound is not null && props.UnicastAddresses.Any(u =>
                IsRoutableGlobalIpv6(u.Address) && StripScopeId(u.Address).Equals(osOutbound));

            foreach (var unicast in props.UnicastAddresses)
            {
                var addr = unicast.Address;
                if (!IsRoutableGlobalIpv6(addr))
                    continue;

                if (unicast.DuplicateAddressDetectionState is not (
                    DuplicateAddressDetectionState.Preferred or DuplicateAddressDetectionState.Deprecated))
                    continue;

                var cleanAddr = StripScopeId(addr);
                var isTemp = unicast.SuffixOrigin == SuffixOrigin.Random;
                var score = 0;
                if (isOsOutboundInterface)
                    score += 1000;
                if (osOutbound is not null && cleanAddr.Equals(osOutbound))
                    score += 200;
                if (hasIpv6Gateway)
                    score += 300;
                if (unicast.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
                    score += 100;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                    score += 50;
                if (!isTemp)
                    score += 10;

                candidates.Add((new Ipv6Candidate(cleanAddr, ni.Name, isTemp, unicast.SuffixOrigin), score));
            }
        }

        return candidates
            .OrderByDescending(x => x.Score)
            .Select(x => x.Candidate)
            .DistinctBy(x => x.Address)
            .ToList();
    }

    private static IPAddress? GetOsPreferredOutboundIpv6()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("2400:3200::1"), 53));
            if (probe.LocalEndPoint is IPEndPoint ep && IsRoutableGlobalIpv6(ep.Address))
                return StripScopeId(ep.Address);
        }
        catch
        {
            // 本机无公网 IPv6 默认路由
        }
        return null;
    }

    private static IPAddress StripScopeId(IPAddress addr)
    {
        Span<byte> bytes = stackalloc byte[16];
        addr.TryWriteBytes(bytes, out _);
        return new IPAddress(bytes);
    }

    public static bool IsRoutableGlobalIpv6(IPAddress addr)
    {
        if (addr.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        if (IPAddress.IsLoopback(addr) || addr.IsIPv6LinkLocal || addr.IsIPv6Multicast ||
            addr.IsIPv6SiteLocal || addr.IsIPv6Teredo || addr.IsIPv4MappedToIPv6)
            return false;

        Span<byte> bytes = stackalloc byte[16];
        if (!addr.TryWriteBytes(bytes, out _))
            return false;

        // 排除 ULA (fc00::/7) 与 6to4 (2002::/16，内网/中继常不可直连)
        if ((bytes[0] & 0xFE) == 0xFC)
            return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return false;

        // Global Unicast 2000::/3
        return (bytes[0] & 0xE0) == 0x20;
    }
}
