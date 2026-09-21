namespace KeelMatrix.AuthSurface;

/// <summary>Describes how a runtime endpoint is authorized.</summary>
public enum AuthSurfaceAuthorizationKind
{
    /// <summary>The endpoint has explicit <c>IAllowAnonymous</c> metadata.</summary>
    ExplicitAnonymous = 0,

    /// <summary>The endpoint has endpoint-specific authorization metadata.</summary>
    ExplicitProtected = 1,

    /// <summary>The endpoint is protected by the application's fallback policy.</summary>
    FallbackProtected = 2,

    /// <summary>The endpoint has no explicit anonymous metadata or effective policy.</summary>
    Unprotected = 3,
}
