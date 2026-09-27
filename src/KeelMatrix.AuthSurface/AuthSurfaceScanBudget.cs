namespace KeelMatrix.AuthSurface;

internal sealed class AuthSurfaceScanBudget
{
    private int routePolicyWork;
    private int metadataItemCount;
    private int requirementDataRequirementCount;
    private int nestedValueCount;
    private int nestedValueCharacters;
    private int effectiveRequirementCount;
    private int canonicalRequirementCharacters;

    internal void ConsumeRoutePolicyWork(int amount)
    {
        if (amount < 0 || routePolicyWork > AuthSurfaceCanonicalizer.MaximumScanRoutePolicyWork - amount)
        {
            throw AuthSurfaceCanonicalizer.ScanRoutePolicyTooComplex();
        }

        routePolicyWork += amount;
    }

    internal void ConsumeMetadataItems(int count)
    {
        if (count < 0 || metadataItemCount > AuthSurfaceCanonicalizer.MaximumScanMetadataItems - count)
        {
            throw AuthSurfaceCanonicalizer.ResourceLimit(
                $"The scan exceeds the supported cumulative authorization metadata bound of {AuthSurfaceCanonicalizer.MaximumScanMetadataItems:N0} items.");
        }

        metadataItemCount += count;
    }

    internal void ConsumeRequirementDataRequirements(int count)
    {
        if (count < 0 || requirementDataRequirementCount > AuthSurfaceCanonicalizer.MaximumScanRequirementDataRequirements - count)
        {
            throw AuthSurfaceCanonicalizer.ResourceLimit(
                $"The scan exceeds the supported cumulative requirement-data expansion bound of {AuthSurfaceCanonicalizer.MaximumScanRequirementDataRequirements:N0} requirements.");
        }

        requirementDataRequirementCount += count;
    }

    internal void ConsumeNestedValue(int characters)
    {
        if (characters < 0 || nestedValueCount >= AuthSurfaceCanonicalizer.MaximumNestedValueCount)
        {
            throw AuthSurfaceCanonicalizer.ResourceLimit(
                $"The scan exceeds the supported {AuthSurfaceCanonicalizer.MaximumNestedValueCount:N0}-nested-value bound.");
        }

        nestedValueCharacters = checked(nestedValueCharacters + characters);
        if (nestedValueCharacters > AuthSurfaceCanonicalizer.MaximumNestedValueCharacters)
        {
            throw AuthSurfaceCanonicalizer.ResourceLimit(
                $"The scan exceeds the supported {AuthSurfaceCanonicalizer.MaximumNestedValueCharacters:N0}-nested-value-character bound.");
        }

        nestedValueCount++;
    }

    internal void ConsumeEffectiveRequirements(int count)
    {
        if (count < 0 || effectiveRequirementCount > AuthSurfaceCanonicalizer.MaximumEffectiveRequirementCount - count)
        {
            throw AuthSurfaceCanonicalizer.ResourceLimit(
                $"The scan exceeds the supported {AuthSurfaceCanonicalizer.MaximumEffectiveRequirementCount:N0}-effective-requirement bound.");
        }

        effectiveRequirementCount += count;
    }

    internal void ConsumeCanonicalRequirement(int characters)
    {
        if (characters < 0 || canonicalRequirementCharacters > AuthSurfaceCanonicalizer.MaximumCanonicalRequirementCharacters - characters)
        {
            throw AuthSurfaceCanonicalizer.ResourceLimit(
                $"The scan exceeds the supported {AuthSurfaceCanonicalizer.MaximumCanonicalRequirementCharacters:N0}-canonical-requirement-character bound.");
        }

        canonicalRequirementCharacters += characters;
    }

    internal void ConsumeMethods(IEnumerable<string> methods, CancellationToken cancellationToken)
    {
        foreach (string method in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsumeNestedValue(method.Length);
        }
    }
}
