using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MyApp.BuildingBlocks.Web.Security;

/// <summary>
/// The reverse proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers are trusted, bound from
/// <see cref="SectionName"/>. With none configured the headers are ignored, so a client cannot choose the address the
/// rate limiter and the logs see.
/// </summary>
public sealed class HttpForwardedHeadersOptions : IValidatableObject
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Http:ForwardedHeaders";

    /// <summary>
    /// The widest IPv4 network that may be trusted, /8: the largest private block (10.0.0.0/8); anything wider spans
    /// networks no single deployment owns, up to the whole internet (0.0.0.0/0).
    /// </summary>
    public const int MinimumIPv4PrefixLength = 8;

    /// <summary>
    /// The widest IPv6 network that may be trusted, /32: the largest block a regional registry allocates to one
    /// operator; anything wider spans unrelated operators, up to the whole internet (::/0).
    /// </summary>
    public const int MinimumIPv6PrefixLength = 32;

    /// <summary>
    /// The IP addresses of trusted proxies, for example <c>10.0.0.5</c> or <c>fd00::5</c>. Empty by default. An IPv4
    /// address is four plain decimal octets; an IPv4-mapped IPv6 address (<c>::ffff:10.0.0.5</c>) is rejected in
    /// favour of its IPv4 form.
    /// </summary>
    public IReadOnlyList<string> KnownProxies { get; set; } = [];

    /// <summary>
    /// The networks of trusted proxies in CIDR notation, for example <c>10.0.0.0/8</c>. Empty by default. An IPv4
    /// network may not be wider than <see cref="MinimumIPv4PrefixLength"/> and an IPv6 one than
    /// <see cref="MinimumIPv6PrefixLength"/>, and an IPv6 network may not overlap any of
    /// <see cref="ForbiddenIPv6Networks"/>.
    /// </summary>
    public IReadOnlyList<string> KnownNetworks { get; set; } = [];

    /// <summary>
    /// IPv6 ranges a trusted network may not overlap, because anyone can appear with an address inside them:
    /// <c>::/96</c> (IPv4-compatible: every IPv4 address written as IPv6), <c>::ffff:0:0/96</c> (IPv4-mapped: a
    /// dual-stack listener reports every IPv4 client as one, and the platform also checks those clients against IPv6
    /// networks), <c>2001::/32</c> (Teredo: any IPv4 host can obtain an address in it) and <c>2002::/16</c> (6to4: any
    /// public IPv4 host has a block in it).
    /// </summary>
    public static IReadOnlyList<(IPNetwork Network, string Reason)> ForbiddenIPv6Networks { get; } =
    [
        (IPNetwork.Parse("::/96"), "the IPv4-compatible range, which contains every IPv4 address"),
        (IPNetwork.Parse("::ffff:0:0/96"), "the IPv4-mapped range, as which a dual-stack listener reports every IPv4 client"),
        (IPNetwork.Parse("2001::/32"), "the Teredo range, in which any IPv4 host can obtain an address"),
        (IPNetwork.Parse("2002::/16"), "the 6to4 range, in which any public IPv4 host has addresses"),
    ];

    /// <summary>Whether any proxy is trusted, so the forwarded headers are processed at all.</summary>
    public bool IsEnabled => KnownProxies.Count > 0 || KnownNetworks.Count > 0;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var proxy in KnownProxies)
        {
            var problem = !TryParseAddress(proxy, out var parsed)
                ? "is not an IPv4 address written as four plain decimal octets (no leading zeros, no hexadecimal) or an IPv6 address"
                : ProxyProblem(parsed);
            if (problem is not null)
            {
                yield return new ValidationResult(
                    $"{SectionName}:{nameof(KnownProxies)} contains '{proxy}', which {problem}.",
                    [nameof(KnownProxies)]);
            }
        }

        foreach (var network in KnownNetworks)
        {
            var problem = !TryParseNetwork(network, out var parsed)
                ? "is not a network in CIDR notation such as 10.0.0.0/8, with the host bits zero and IPv4 written as four plain decimal octets"
                : NetworkProblem(parsed);
            if (problem is not null)
            {
                yield return new ValidationResult(
                    $"{SectionName}:{nameof(KnownNetworks)} contains '{network}', which {problem}.",
                    [nameof(KnownNetworks)]);
            }
        }
    }

    /// <summary>
    /// Parses an address written in full. IPv4 must be four plain decimal octets from 0 to 255 without leading zeros:
    /// the platform parser reads <c>010</c> as octal (8) and <c>0x0A</c> as hexadecimal and accepts short forms such
    /// as <c>10</c>, so <c>010.0.0.5</c> would silently mean 8.0.0.5. IPv6 is accepted in any standard form.
    /// </summary>
    internal static bool TryParseAddress(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.Contains(':', StringComparison.Ordinal) && !IsPlainDecimalIPv4(trimmed))
        {
            return false;
        }

        if (!IPAddress.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// Parses a network in CIDR notation whose host bits are zero: <c>10.0.0.1/8</c> is rejected rather than read as
    /// <c>10.0.0.0/8</c>, because the author meant either a single proxy or a network, and guessing widens the trust.
    /// </summary>
    internal static bool TryParseNetwork(string? text, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        return slash > 0
            && TryParseAddress(trimmed[..slash], out var address)
            && IPNetwork.TryParse(trimmed, out network)
            && network.BaseAddress.Equals(address);
    }

    /// <summary>Returns why a well-formed proxy address may not be trusted, or null when it may.</summary>
    private static string? ProxyProblem(IPAddress address) =>
        address.IsIPv4MappedToIPv6
            ? $"is an IPv4-mapped IPv6 address; write it in IPv4 form, {address.MapToIPv4()}"
            : null;

    /// <summary>Returns why a well-formed network may not be trusted, or null when it may.</summary>
    private static string? NetworkProblem(IPNetwork network)
    {
        if (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            return network.PrefixLength < MinimumIPv4PrefixLength
                ? $"is wider than /{MinimumIPv4PrefixLength}; trust only the networks the proxies are in"
                : null;
        }

        foreach (var (forbidden, reason) in ForbiddenIPv6Networks)
        {
            if (Overlaps(network, forbidden))
            {
                return $"overlaps {forbidden}, {reason}; trusting it would let clients that are not proxies forward headers";
            }
        }

        return network.PrefixLength < MinimumIPv6PrefixLength
            ? $"is wider than /{MinimumIPv6PrefixLength}; trust only the networks the proxies are in"
            : null;
    }

    /// <summary>
    /// Whether two IPv6 networks share any address: exactly when their first <c>min(prefix)</c> bits are equal.
    /// Computed on the bits rather than with <see cref="IPNetwork.Contains"/>, which on .NET 10 gives wrong answers
    /// for IPv4-mapped addresses (it reports <c>2001:db8::/32</c> as containing <c>::ffff:0:0</c>).
    /// </summary>
    private static bool Overlaps(IPNetwork first, IPNetwork second)
    {
        var length = Math.Min(first.PrefixLength, second.PrefixLength);
        if (length == 0)
        {
            return true;
        }

        var mask = UInt128.MaxValue << (128 - length);
        return (Bits(first.BaseAddress) & mask) == (Bits(second.BaseAddress) & mask);
    }

    private static UInt128 Bits(IPAddress address) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(address.GetAddressBytes());

    private static bool IsPlainDecimalIPv4(string text)
    {
        var octets = text.Split('.');
        return octets.Length == 4 && octets.All(o =>
            o.Length is >= 1 and <= 3
            && o.All(char.IsAsciiDigit)
            && (o.Length == 1 || o[0] != '0')
            && int.Parse(o, CultureInfo.InvariantCulture) <= 255);
    }
}
