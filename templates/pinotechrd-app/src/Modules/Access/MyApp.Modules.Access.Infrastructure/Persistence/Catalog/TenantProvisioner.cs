using Microsoft.EntityFrameworkCore;
using MyApp.BuildingBlocks.Domain;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Application.Tenants;
using Npgsql;
using Pinotechrd.Security.Entities;
using Pinotechrd.Security.Enums;

namespace MyApp.Modules.Access.Infrastructure.Persistence.Catalog;

/// <summary>
/// The <see cref="ITenantProvisioner"/> of the catalog (ADR 0011). The row comes first, as the record of intent: a schema
/// never exists without its row. Then, while the row is provisioning: record the start, create and migrate the schema
/// with <see cref="ITenantMigrator"/> (under its per-tenant lock, so each migration runs once), and mark the row active or
/// record the failure, counting the attempt in that same update.
/// <para>
/// Concurrency rests on the database, not on a lock of its own: the unique index on the identifier lets one insert win
/// and the others read the winner's row; every later change is an update conditioned on the status still being
/// provisioning, so a late failure never overwrites a success and a suspended tenant is never activated. The schema name
/// comes only from <see cref="TenantSchemaName.For"/> and the tenant's id; the slug reaches SQL only as a parameter.
/// </para>
/// </summary>
/// <param name="catalog">The catalog context of the scope.</param>
/// <param name="migrator">Creates and migrates the tenant's schema.</param>
public sealed class TenantProvisioner(CatalogDbContext catalog, ITenantMigrator migrator) : ITenantProvisioner
{
    // The unique index on catalog.tenants.identifier and the foreign key to the subscription, as the catalog migration names them.
    private const string IdentifierIndex = "ix_tenants_identifier";
    private const string SubscriptionForeignKey = "fk_tenants_system_subscriptions_subscription_id";
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    /// <inheritdoc />
    public async Task<Result<ProvisionedTenant>> ProvisionAsync(ProvisionTenantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return Error.Validation(CommonErrorCodes.ValidationFailed, "The tenant request is not valid.", errors);
        }

        if (!await catalog.SystemSubscriptions.AnyAsync(s => s.Id == request.SubscriptionId, cancellationToken).ConfigureAwait(false))
        {
            return SubscriptionNotFound();
        }

        var tenant = await FindAsync(request.Slug, cancellationToken).ConfigureAwait(false)
                     ?? await InsertAsync(request, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return SubscriptionNotFound();
        }

        if (!string.Equals(tenant.Name, request.Name, StringComparison.Ordinal) || tenant.SubscriptionId != request.SubscriptionId)
        {
            return Error.Conflict(TenantErrorCodes.SlugTaken, "The slug belongs to a tenant with another name or subscription.");
        }

        if (tenant.Status != TenantStatus.Provisioning)
        {
            return Settled(tenant, request.Slug, alreadyProvisioned: true);
        }

        var now = DateTime.UtcNow;
        var started = await Provisioning(tenant.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ProvisioningStartedAt, now).SetProperty(t => t.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (started == 0)
        {
            // Settled by a concurrent request since it was read.
            return await SettledAsync(tenant.Id, request.Slug, cancellationToken).ConfigureAwait(false);
        }

        var migration = await migrator.MigrateAsync(tenant.Id, cancellationToken).ConfigureAwait(false);
        now = DateTime.UtcNow;

        // The attempt is counted with its outcome, by the same update conditioned on the row still provisioning: a request
        // that waited on the tenant lock while another one migrated and activated it updates nothing, counts nothing and
        // reports the tenant as already provisioned.
        if (!migration.Succeeded)
        {
            var cause = Describe(migration.Error!);
            var recorded = await Provisioning(tenant.Id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.ProvisioningAttempts, t => t.ProvisioningAttempts + 1)
                        .SetProperty(t => t.ProvisioningError, cause)
                        .SetProperty(t => t.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
            if (recorded == 0)
            {
                return await SettledAsync(tenant.Id, request.Slug, cancellationToken).ConfigureAwait(false);
            }

            return Error.Failure(
                TenantErrorCodes.ProvisioningFailed,
                $"The tenant's schema could not be created or migrated ({cause}); the tenant stays provisioning and a new request resumes it.");
        }

        var activated = await Provisioning(tenant.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ProvisioningAttempts, t => t.ProvisioningAttempts + 1)
                    .SetProperty(t => t.Status, TenantStatus.Active)
                    .SetProperty(t => t.ProvisioningCompletedAt, now)
                    .SetProperty(t => t.ProvisioningError, (string?)null)
                    .SetProperty(t => t.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
        return activated == 1
            ? new ProvisionedTenant(tenant.Id, request.Slug, AlreadyProvisioned: false)
            : await SettledAsync(tenant.Id, request.Slug, cancellationToken).ConfigureAwait(false);
    }

    private IQueryable<Tenant> Provisioning(Guid tenantId) =>
        catalog.Tenants.Where(t => t.Id == tenantId && t.Status == TenantStatus.Provisioning);

    private Task<Tenant?> FindAsync(string slug, CancellationToken cancellationToken) =>
        catalog.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Identifier == slug, cancellationToken);

    // Inserts the row; when a concurrent request inserted the slug first, returns that row. Null when the subscription
    // was deleted since it was checked. Not Tenant.Create, which rewrites the identifier it is given.
    private async Task<Tenant?> InsertAsync(ProvisionTenantRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var tenant = new Tenant
        {
            Id = id,
            Identifier = request.Slug,
            Name = request.Name,
            SubscriptionId = request.SubscriptionId,
            DbSchema = TenantSchemaName.For(id),
            Status = TenantStatus.Provisioning,
            ProvisioningAttempts = 0,
            CreatedAt = DateTime.UtcNow,
        };
        catalog.Tenants.Add(tenant);
        try
        {
            await catalog.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return tenant;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: UniqueViolation, ConstraintName: IdentifierIndex,
        })
        {
            catalog.ChangeTracker.Clear();
            return await FindAsync(request.Slug, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("The tenant's slug was taken by a row that is gone; try again.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: ForeignKeyViolation, ConstraintName: SubscriptionForeignKey,
        })
        {
            catalog.ChangeTracker.Clear();
            return null;
        }
    }

    // The row was settled by a concurrent request: an active tenant was provisioned by that request, not this one.
    private async Task<Result<ProvisionedTenant>> SettledAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        var tenant = await catalog.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, cancellationToken).ConfigureAwait(false);
        return Settled(tenant, slug, alreadyProvisioned: true);
    }

    // A tenant that is no longer provisioning: active is a success, any other status refuses the request.
    private static Result<ProvisionedTenant> Settled(Tenant tenant, string slug, bool alreadyProvisioned) => tenant.Status switch
    {
        TenantStatus.Active => new ProvisionedTenant(tenant.Id, slug, alreadyProvisioned),
        TenantStatus.Suspended => Error.Failure(TenantErrorCodes.Suspended, "The tenant is suspended; activate it before provisioning it."),
        TenantStatus.Cancelled => Error.Failure(TenantErrorCodes.Cancelled, "The tenant is cancelled and is not provisioned again."),
        _ => throw new InvalidOperationException("The tenant is still provisioning."),
    };

    private static Error SubscriptionNotFound() =>
        Error.NotFound(TenantErrorCodes.SubscriptionNotFound, "The subscription does not exist.");

    // What the row keeps of a failure: the SQLSTATE of a server error, otherwise the exception's type. Never a message,
    // which can name the host, the user or a value.
    private static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState;
            }
        }

        return exception.GetType().Name;
    }
}
