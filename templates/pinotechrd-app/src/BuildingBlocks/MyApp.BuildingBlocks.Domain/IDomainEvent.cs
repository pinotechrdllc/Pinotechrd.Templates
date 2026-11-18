namespace MyApp.BuildingBlocks.Domain;

/// <summary>Something that happened in the domain and that other parts of the system may react to.</summary>
public interface IDomainEvent
{
    /// <summary>Gets the moment the event occurred, in UTC.</summary>
    DateTimeOffset OccurredAt { get; }
}
