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

            foreach (var unicast in props.UnicastAddresses)
            {
                var addr = unicast.Address;
                if (!IsRoutableGlobalIpv6(addr))
                    continue;

                if (unicast.DuplicateAddressDetectionState is not (
                    DuplicateAddressDetectionState.Preferred or DuplicateAddressDetectionState.Deprecated))
                    continue;

                var isTemp = unicast.SuffixOrigin == SuffixOrigin.Random;
                var score = 0;
                if (unicast.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
                    score += 100;
                // Windows 下临时地址通常为当前实际出站源地址，但稳定地址不易过期；优先 Preferred + 非隧道网卡
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                    score += 50;
                if (!isTemp)
                    score += 10;

                // 去掉 ScopeId 以免编码/比较干扰
                var cleanAddr = new IPAddress(addr.GetAddressBytes());
                candidates.Add((new Ipv6Candidate(cleanAddr, ni.Name, isTemp, unicast.SuffixOrigin), score));
            }
        }

        return candidates
            .OrderByDescending(x => x.Score)
            .Select(x => x.Candidate)
            .DistinctBy(x => x.Address)
            .ToList();
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
