namespace KeelMatrix.AuthSurface;

/// <summary>Reports an invalid or ambiguous runtime authorization surface.</summary>
public sealed class AuthSurfaceAnalysisException : InvalidOperationException
{
    internal AuthSurfaceAnalysisException(AuthSurfaceDiagnosticCode code, string message)
        : base(message)
    {
        Code = code.GetValue();
    }

    /// <summary>Gets the stable analysis error code.</summary>
    public string Code { get; }
}

/// <summary>Reports a malformed, unsupported, or otherwise invalid baseline.</summary>
public sealed class AuthSurfaceBaselineException : Exception
{
    internal AuthSurfaceBaselineException(
        AuthSurfaceDiagnosticCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code.GetValue();
    }

    /// <summary>Gets the stable baseline error code.</summary>
    public string Code { get; }
}

/// <summary>Reports failed policy or baseline verification.</summary>
public sealed class AuthSurfaceVerificationException : InvalidOperationException
{
    internal AuthSurfaceVerificationException(AuthSurfaceVerificationResult result)
        : base(CreateMessage(result))
    {
        Result = result;
    }

    /// <summary>Gets the structured failed verification result.</summary>
    public AuthSurfaceVerificationResult Result { get; }

    private static string CreateMessage(AuthSurfaceVerificationResult result) =>
        string.Join(Environment.NewLine, result.Violations.Select(static violation => violation.Message));
}
