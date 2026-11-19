using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.BuildingBlocks.Infrastructure.DataProtection;

/// <summary>
/// The rows of the Data Protection key ring, as text: the API's key repository turns them into XML elements. A singleton
/// that opens a scope of its own for each call, because the key manager calls it from wherever it needs the ring.
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
    public static IReadOnlyList<string> ReadColumns { get; } = ["id", "friendly_name", "xml"];

    /// <summary>The only columns an insert gives values for; the id is filled by the database.</summary>
    public static IReadOnlyList<string> InsertColumns { get; } = ["friendly_name", "xml"];

    /// <summary>Reads every element of the key ring, in the order they were stored.</summary>
    /// <returns>The XML of each element.</returns>
    public IReadOnlyList<string> ReadAll()
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataProtectionKeysDbContext>();
        return context.Keys.AsNoTracking().OrderBy(k => k.Id).Select(k => k.Xml).ToList();
    }

    /// <summary>Adds an element to the key ring.</summary>
    /// <param name="friendlyName">The name the key manager gave the element.</param>
    /// <param name="xml">The element's XML.</param>
    public void Append(string? friendlyName, string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);

        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataProtectionKeysDbContext>();
        context.Keys.Add(new DataProtectionKey { FriendlyName = friendlyName, Xml = xml });
        context.SaveChanges();
    }
}
