using System.Collections.ObjectModel;

namespace MyApp.BuildingBlocks.Domain;

/// <summary>
/// The entity through which an aggregate is changed. It records the domain events raised by those changes
/// until they are dispatched. An aggregate is not thread-safe.
/// </summary>
/// <typeparam name="TId">The type of the id.</typeparam>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>Initializes a new instance of the <see cref="AggregateRoot{TId}"/> class.</summary>
    /// <param name="id">The id, or the default value when the store assigns it.</param>
    protected AggregateRoot(TId id)
        : base(id) => DomainEvents = new ReadOnlyCollection<IDomainEvent>(_domainEvents);

    /// <summary>
    /// Gets the events raised since the last call to <see cref="ClearDomainEvents"/>, in the order raised.
    /// The list is a live view: a dispatcher must copy it and then clear it, because raising an event while
    /// iterating the view throws.
    /// </summary>
    public IReadOnlyList<IDomainEvent> DomainEvents { get; }

    /// <summary>Removes the recorded events, typically after they have been copied for dispatch.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>Records an event raised by a change of this aggregate.</summary>
    /// <param name="domainEvent">The event.</param>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }
}
