namespace KeelMatrix.AuthSurface;

/// <summary>Contains the structured result of policy or baseline verification.</summary>
public sealed class AuthSurfaceVerificationResult
{
    internal AuthSurfaceVerificationResult(IEnumerable<AuthSurfaceViolation> violations, bool usedBaseline)
    {
        Violations = violations.ToArray();
        UsedBaseline = usedBaseline;
    }

    /// <summary>Gets every ordered violation.</summary>
    public IReadOnlyList<AuthSurfaceViolation> Violations { get; }

    /// <summary>Gets a value indicating whether the verification passed.</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>Gets a value indicating whether a baseline participated in verification.</summary>
    public bool UsedBaseline { get; }

    /// <summary>Throws when one or more violations exist.</summary>
    public void AssertValid()
    {
        if (!IsValid)
        {
            throw new AuthSurfaceVerificationException(this);
        }
    }
}
