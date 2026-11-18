using MyApp.BuildingBlocks.Domain;

namespace MyApp.BuildingBlocks.Tests.Domain;

public class AggregateRootTests
{
    private sealed record SomethingHappened(string Name) : IDomainEvent
    {
        public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class Account(Guid id) : AggregateRoot<Guid>(id)
    {
        public void Do(IDomainEvent domainEvent) => Raise(domainEvent);
    }

    [Fact]
    public void NewAggregateHasNoDomainEvents()
    {
        var account = new Account(Guid.NewGuid());

        Assert.Empty(account.DomainEvents);
    }

    [Fact]
    public void RaisedEventsAreRecordedInOrder()
    {
        var account = new Account(Guid.NewGuid());
        var first = new SomethingHappened("first");
        var second = new SomethingHappened("second");
        var third = new SomethingHappened("third");

        account.Do(first);
        account.Do(second);
        account.Do(third);

        Assert.Equal(3, account.DomainEvents.Count);
        Assert.Same(first, account.DomainEvents[0]);
        Assert.Same(second, account.DomainEvents[1]);
        Assert.Same(third, account.DomainEvents[2]);
    }

    [Fact]
    public void ExposedEventCollectionIsReadOnly()
    {
        var account = new Account(Guid.NewGuid());
        account.Do(new SomethingHappened("first"));

        Assert.IsAssignableFrom<IReadOnlyList<IDomainEvent>>(account.DomainEvents);
        Assert.False(account.DomainEvents is ICollection<IDomainEvent> { IsReadOnly: false });
        Assert.False(account.DomainEvents is IList<IDomainEvent> { IsReadOnly: false });
    }

    [Fact]
    public void ExposedEventCollectionReflectsEventsRaisedAfterItWasRead()
    {
        var account = new Account(Guid.NewGuid());
        var events = account.DomainEvents;

        account.Do(new SomethingHappened("late"));

        Assert.Single(events);
    }

    [Fact]
    public void ClearingEmptiesTheEvents()
    {
        var account = new Account(Guid.NewGuid());
        account.Do(new SomethingHappened("first"));
        account.Do(new SomethingHappened("second"));

        account.ClearDomainEvents();

        Assert.Empty(account.DomainEvents);
    }

    [Fact]
    public void EventsRaisedAfterClearingAreRecordedAgain()
    {
        var account = new Account(Guid.NewGuid());
        account.Do(new SomethingHappened("before"));
        account.ClearDomainEvents();
        var after = new SomethingHappened("after");

        account.Do(after);

        Assert.Same(after, Assert.Single(account.DomainEvents));
    }

    [Fact]
    public void AggregateRootIsAnEntityComparedById()
    {
        var id = Guid.NewGuid();

        Assert.True(new Account(id) == new Account(id));
    }
}
