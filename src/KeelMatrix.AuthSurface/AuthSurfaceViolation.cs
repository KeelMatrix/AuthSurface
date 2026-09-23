namespace KeelMatrix.AuthSurface;

/// <summary>Describes one actionable authorization-surface or baseline violation.</summary>
public sealed class AuthSurfaceViolation
{
    internal AuthSurfaceViolation(
        AuthSurfaceDiagnosticCode code,
        string message,
        string? route = null,
        string? method = null,
        string? expected = null,
        string? actual = null)
    {
        Code = code.GetValue();
        Message = message;
        Route = route;
        Method = method;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Gets a stable machine-readable violation code.</summary>
    public string Code { get; }

    /// <summary>Gets a deterministic human-readable message.</summary>
    public string Message { get; }

    /// <summary>Gets the affected normalized route, when applicable.</summary>
    public string? Route { get; }

    /// <summary>Gets the affected HTTP method, when applicable.</summary>
    public string? Method { get; }

    /// <summary>Gets the expected canonical value, when applicable.</summary>
    public string? Expected { get; }

    /// <summary>Gets the observed canonical value, when applicable.</summary>
    public string? Actual { get; }

    /// <summary>Returns the deterministic diagnostic message.</summary>
    public override string ToString() => Message;
}
