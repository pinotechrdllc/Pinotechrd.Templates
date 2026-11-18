namespace MyApp.BuildingBlocks.Domain;

/// <summary>Classifies an <see cref="Error"/> so the boundary can map it to a protocol status.</summary>
public enum ErrorKind
{
    /// <summary>A business rule rejected the operation; the expected failure that fits no other kind.</summary>
    Failure = 0,

    /// <summary>The input is invalid; <see cref="Error.Details"/> may hold the messages per field.</summary>
    Validation,

    /// <summary>The requested resource does not exist.</summary>
    NotFound,

    /// <summary>The request conflicts with the current state, such as a duplicate or a stale version.</summary>
    Conflict,

    /// <summary>The caller is not authenticated, or the credentials are not valid.</summary>
    Unauthorized,

    /// <summary>The caller is authenticated but not allowed to perform the operation.</summary>
    Forbidden,
}
