using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using MyApp.BuildingBlocks.Infrastructure.DataProtection;

namespace MyApp.Api;

/// <summary>
/// The Data Protection key repository of the API: the key ring in the shared database, through
/// <see cref="DataProtectionKeyStore"/>, so every instance of the API uses the same keys. It only reads and appends; the
/// key manager encrypts each key before it gets here (see <see cref="DataProtectionComposition"/>).
/// </summary>
/// <param name="store">The rows of the key ring.</param>
public sealed class DatabaseXmlRepository(DataProtectionKeyStore store) : IXmlRepository
{
    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements() => [.. store.ReadAll().Select(xml => XElement.Parse(xml))];

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        store.Append(friendlyName, element.ToString(SaveOptions.DisableFormatting));
    }
}
