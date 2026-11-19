using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.BuildingBlocks.Infrastructure.DataProtection;

/// <summary>
/// The rows of the Data Protection key ring, as text and signature: the API's key repository checks the signatures and turns
/// the rows into XML elements. A singleton that opens a scope of its own for each call, because the key manager calls it from
/// wherever it needs the ring.
/// <para>
/// <see cref="DataProtectionKeysDbContext"/> is a shared context: its connections assume no tenant role, so these calls
/// run as the application's login, which the migrator grants <c>SELECT</c> on <see cref="ReadColumns"/> and
/// <c>INSERT</c> on <see cref="InsertColumns"/> of the table, nothing else. A query here touches no other column, and
/// nothing here updates or deletes a row.
/// </para>
/// </summary>
/// <param name="scopes">Creates the scope of each call.</param>
public sealed class DataProtectionKeyStore(IServiceScopeFactory scopes)
{
    /// <summary>The only columns the reads use (the id is also read back after an insert).</summary>
    public static IReadOnlyList<string> ReadColumns { get; } = ["id", "friendly_name", "xml", "signature", "signer"];

    /// <summary>The only columns an insert gives values for; the id is filled by the database.</summary>
    public static IReadOnlyList<string> InsertColumns { get; } = ["friendly_name", "xml", "signature", "signer"];

    /// <summary>
    /// Reads the rows of the key ring a reader has not processed yet, in id order, unchecked: every row after
    /// <paramref name="lastId"/>, and the rows of the <paramref name="window"/> ids below it that are not in
    /// <paramref name="processed"/> (a row whose transaction committed after a higher id was read). Rows are only appended,
    /// so a reader that keeps that state only fetches what is new.
    /// </summary>
    /// <param name="lastId">The highest id already read; 0 reads every row.</param>
    /// <param name="window">How many ids below <paramref name="lastId"/> are read again when not processed.</param>
    /// <param name="processed">The ids already processed inside the window.</param>
    /// <returns>The rows, not tracked.</returns>
    public IReadOnlyList<DataProtectionKey> ReadUnprocessed(int lastId, int window, IReadOnlyCollection<int> processed)
    {
        ArgumentNullException.ThrowIfNull(processed);

        var floor = lastId - window;
        int[] seen = [.. processed];
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataProtectionKeysDbContext>();
        return context.Keys.AsNoTracking()
            .Where(k => k.Id > lastId || (k.Id > floor && !seen.Contains(k.Id)))
            .OrderBy(k => k.Id)
            .ToList();
    }

    /// <summary>Adds an element to the key ring.</summary>
    /// <param name="friendlyName">The name the key manager gave the element.</param>
    /// <param name="xml">The element's XML.</param>
    /// <param name="signature">The signature of the name and the XML, or <see langword="null"/> without a certificate.</param>
    /// <param name="signer">The SHA-256 thumbprint of the signing certificate, or <see langword="null"/>.</param>
    public void Append(string? friendlyName, string xml, byte[]? signature, string? signer)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);

        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataProtectionKeysDbContext>();
        context.Keys.Add(new DataProtectionKey { FriendlyName = friendlyName, Xml = xml, Signature = signature, Signer = signer });
        context.SaveChanges();
    }
}
