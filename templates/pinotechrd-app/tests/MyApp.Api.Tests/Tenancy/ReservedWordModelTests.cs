using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Tenancy;

// No false positives: a real model whose columns are named like the words the statement check looks for, in snake_case and
// in quoted PascalCase, with values that look like statements, and every kind of EF write and read. Needs Docker.
[Collection(PostgresContainer.Name)]
public class ReservedWordModelTests(PostgresFixture postgres)
{
    private const string Hostile = "x; RESET ALL; -- $$ $1 $tag$ SET search_path TO other; DISCARD ALL";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Reserved(IServiceCollection services)
    {
        services.AddTenantDbContext<ReservedContext>("Reserved");
        services.AddTenantMigrator();
    }

    private static ReservedWord Word(string value) => new()
    {
        SearchPath = value,
        Role = value,
        Schema = value,
        Reset = value,
        Discard = value,
        SetConfig = value,
        Prepare = value,
        Do = value,
    };

    private static ReservedPascal Pascal(string value) => new()
    {
        SearchPath = value,
        Role = value,
        Schema = value,
        Reset = value,
        Discard = value,
        SetConfig = value,
        Prepare = value,
        Do = value,
    };

    // Breaks when a column named search_path, role, schema, reset, discard, set_config, prepare or do (snake_case or quoted
    // PascalCase) makes EF's own SQL refused: inserts, updates, deletes, filters on these columns, ExecuteUpdate, ExecuteDelete,
    // ON CONFLICT DO UPDATE SET role = ..., and values that look like statements, outside and inside a transaction.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColumnsNamedLikeSessionWordsWorkInEveryKindOfStatement(bool inTransaction)
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2, register: Reserved);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<ReservedContext>();
        await using var transaction = inTransaction ? await context.Database.BeginTransactionAsync(Token) : null;

        var word = Word(Hostile);
        var pascal = Pascal(Hostile);
        context.Words.Add(word);
        context.Pascal.Add(pascal);
        await context.SaveChangesAsync(Token);
        word.Role = "changed";
        word.SearchPath = "changed; RESET ALL";
        pascal.Schema = "changed";
        await context.SaveChangesAsync(Token);

        Assert.Single(await context.Words.Where(w => w.Role == "changed" && w.SetConfig == Hostile && w.Do == Hostile).ToListAsync(Token));
        Assert.Single(await context.Pascal.Where(w => w.Schema == "changed" && w.Reset == Hostile).ToListAsync(Token));
        Assert.Equal(1, await context.Words.Where(w => w.Id == word.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.Role, "bulk").SetProperty(w => w.Discard, "d"), Token));
        Assert.Equal(1, await context.Pascal.Where(w => w.Id == pascal.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.Role, "bulk"), Token));
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO reserved_words (id, role, search_path) VALUES ({0}, 'upserted; RESET ALL; --', 'p') "
            + "ON CONFLICT (id) DO UPDATE SET role = EXCLUDED.role, search_path = EXCLUDED.search_path",
            new object[] { word.Id },
            Token);
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO reserved_words (id, role, \"do\") VALUES ({id}, {Hostile}, 'do; SET ROLE x')", Token);
        var stored = await context.Words.Where(w => w.Id == word.Id).Select(w => new { w.Role, w.SearchPath, w.Discard }).SingleAsync(Token);
        Assert.Equal(("upserted; RESET ALL; --", "p", "d"), (stored.Role, stored.SearchPath, stored.Discard));
        Assert.Equal(Hostile, await context.Words.Where(w => w.Id == id).Select(w => w.Role).SingleAsync(Token));

        Assert.Equal(2, await context.Words.ExecuteDeleteAsync(Token));
        context.Pascal.Remove(pascal);
        await context.SaveChangesAsync(Token);
        Assert.Empty(await context.Pascal.ToListAsync(Token));
        if (transaction is not null)
        {
            await transaction.CommitAsync(Token);
        }

        Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(Token));
    }
}
