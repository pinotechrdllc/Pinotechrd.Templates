namespace MyApp.Modules.Access.Infrastructure.Seeding;

/// <summary>What <see cref="OperatorAdminSeeder"/> did for the operator tenant's first administrator.</summary>
public enum BootstrapAdminOutcome
{
    /// <summary>The seeder has not run for the target tenant.</summary>
    NotRun,

    /// <summary>The administrator was created with the password given.</summary>
    Created,

    /// <summary>A user with the administrator's email already existed: kept as it is, its password untouched.</summary>
    AlreadyPresent,

    /// <summary>The administrator does not exist and no password was given: nothing was written.</summary>
    PasswordRequired,

    /// <summary>Identity refused the new user; <see cref="BootstrapTarget.ErrorCodes"/> holds its error codes.</summary>
    Failed,

    /// <summary>The <c>Administrator</c> system role already has members, none with the administrator's email: nothing was written.</summary>
    AnotherAdministrator,
}

/// <summary>
/// The operator tenant the migrator's <c>bootstrap</c> command targets and its first administrator, set by the command
/// once the tenant is provisioned and read by <see cref="OperatorAdminSeeder"/> when the tenant migrator seeds that
/// tenant. A singleton of the bootstrap command only. The password is held only until the seeder has run, and nothing
/// here formats the email or the password: <see cref="ToString"/> is the type's name.
/// </summary>
public sealed class BootstrapTarget
{
    private readonly Lock _gate = new();
    private string? _adminEmail;
    private string? _adminPassword;

    /// <summary>Gets the operator tenant's id; <see cref="Guid.Empty"/> until <see cref="Set"/> is called.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Gets what the seeder did for the administrator.</summary>
    public BootstrapAdminOutcome Outcome { get; private set; }

    /// <summary>Gets the codes of the Identity errors when <see cref="Outcome"/> is <see cref="BootstrapAdminOutcome.Failed"/>.</summary>
    public IReadOnlyList<string> ErrorCodes { get; private set; } = [];

    /// <summary>Targets the tenant <paramref name="tenantId"/> and its administrator.</summary>
    /// <param name="tenantId">The operator tenant's id.</param>
    /// <param name="adminEmail">The administrator's email, also its user name.</param>
    /// <param name="adminPassword">The administrator's password; <see langword="null"/> when none was given.</param>
    public void Set(Guid tenantId, string adminEmail, string? adminPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminEmail);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant id must not be the empty GUID.", nameof(tenantId));
        }

        lock (_gate)
        {
            TenantId = tenantId;
            _adminEmail = adminEmail;
            _adminPassword = adminPassword;
            Outcome = BootstrapAdminOutcome.NotRun;
            ErrorCodes = [];
        }
    }

    /// <inheritdoc />
    public override string ToString() => nameof(BootstrapTarget);

    // The email and password for the seeder, when the tenant is the target.
    internal bool TryGetAdmin(Guid tenantId, out string email, out string? password)
    {
        lock (_gate)
        {
            email = _adminEmail ?? string.Empty;
            password = _adminPassword;
            return TenantId != Guid.Empty && TenantId == tenantId && _adminEmail is not null;
        }
    }

    // Records the outcome and forgets the password.
    internal void Complete(BootstrapAdminOutcome outcome, IReadOnlyList<string>? errorCodes = null)
    {
        lock (_gate)
        {
            Outcome = outcome;
            ErrorCodes = errorCodes ?? [];
            _adminPassword = null;
        }
    }
}
