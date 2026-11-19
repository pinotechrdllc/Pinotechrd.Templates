namespace MyApp.Modules.Access.Application.Tenants;

/// <summary>
/// Error codes of tenant provisioning. Like every <c>*ErrorCodes</c> class they are snake_case, unique and only ever
/// added, and listed in <c>contract/problem-codes.v1.md</c>.
/// </summary>
public static class TenantErrorCodes
{
    /// <summary>The slug belongs to a tenant with another name or subscription.</summary>
    public const string SlugTaken = "tenant_slug_taken";

    /// <summary>The subscription the tenant would belong to does not exist.</summary>
    public const string SubscriptionNotFound = "tenant_subscription_not_found";

    /// <summary>The tenant is suspended: it is not provisioned again until it is activated.</summary>
    public const string Suspended = "tenant_suspended";

    /// <summary>The tenant is cancelled: it is never provisioned again.</summary>
    public const string Cancelled = "tenant_cancelled";

    /// <summary>Creating or migrating the tenant's schema failed; the tenant stays provisioning and a new attempt resumes it.</summary>
    public const string ProvisioningFailed = "tenant_provisioning_failed";

    /// <summary>The subscription is reserved for the operator tenant, which only the migrator's bootstrap command provisions.</summary>
    public const string SubscriptionReserved = "tenant_subscription_reserved";

    /// <summary>The tenant of the token is not active (provisioning, suspended or cancelled): the request is refused.</summary>
    public const string Unavailable = "tenant_unavailable";
}
