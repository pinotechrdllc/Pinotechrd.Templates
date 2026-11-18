using MyApp.BuildingBlocks.Domain;

namespace MyApp.BuildingBlocks.Tests.Domain;

public class EntityTests
{
    private sealed class Order(Guid id) : Entity<Guid>(id);

    private sealed class Invoice(Guid id) : Entity<Guid>(id);

    private sealed class Counter(int id) : Entity<int>(id);

    [Fact]
    public void EntitiesOfTheSameTypeWithTheSameIdAreEqual()
    {
        var id = Guid.NewGuid();
        var first = new Order(id);
        var second = new Order(id);

        Assert.True(first.Equals(second));
        Assert.True(first.Equals((object)second));
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void EntitiesOfTheSameTypeWithDifferentIdsAreNotEqual()
    {
        var first = new Order(Guid.NewGuid());
        var second = new Order(Guid.NewGuid());

        Assert.False(first.Equals(second));
        Assert.False(first == second);
        Assert.True(first != second);
    }

    [Fact]
    public void EntitiesOfDifferentTypesWithTheSameIdAreNotEqual()
    {
        var id = Guid.NewGuid();
        var order = new Order(id);
        var invoice = new Invoice(id);

        Assert.False(order.Equals(invoice));
        Assert.False(order.Equals((object)invoice));
        Assert.False(((Entity<Guid>)order) == invoice);
        Assert.True(((Entity<Guid>)order) != invoice);
    }

    [Fact]
    public void EntityWithADefaultIdIsEqualOnlyToItself()
    {
        var first = new Order(Guid.Empty);
        var second = new Order(Guid.Empty);
        var sameReference = first;

        Assert.False(first.Equals(second));
        Assert.False(first == second);
        Assert.True(first.Equals(sameReference));
        Assert.True(first == sameReference);
    }

    [Fact]
    public void EntityWithADefaultValueTypeIdIsNotEqualToAnotherWithTheDefault()
    {
        var first = new Counter(0);
        var second = new Counter(0);

        Assert.False(first.Equals(second));
        Assert.False(first == second);
    }

    [Fact]
    public void EntityWithADefaultIdIsNotEqualToOneWithARealId()
    {
        var transient = new Order(Guid.Empty);
        var persisted = new Order(Guid.NewGuid());

        Assert.False(transient.Equals(persisted));
        Assert.False(persisted.Equals(transient));
    }

    [Fact]
    public void ComparisonWithNullIsFalse()
    {
        var order = new Order(Guid.NewGuid());
        Order? none = null;

        Assert.False(order.Equals(null));
        Assert.False(order.Equals((object?)null));
        Assert.False(order == none);
        Assert.True(order != none);
        Assert.False(none == order);
        Assert.True(none != order);
    }

    [Fact]
    public void TwoNullEntitiesAreEqualByOperator()
    {
        Order? left = null;
        Order? right = null;

        Assert.True(left == right);
    }

    [Fact]
    public void EntityIsNotEqualToAnObjectOfAnotherKind()
    {
        var order = new Order(Guid.NewGuid());

        Assert.False(order.Equals("not an entity"));
    }
}
