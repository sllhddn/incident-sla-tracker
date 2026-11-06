namespace SlaTracker.Api.Domain;

/// <summary>Response and resolution targets for one severity. Rows live in dbo.SlaPolicy so they can be tuned without a release.</summary>
public class SlaPolicy
{
    public Severity Severity { get; private set; }
    public int ResponseMinutes { get; private set; }
    public int ResolveMinutes { get; private set; }

    private SlaPolicy() { }

    public SlaPolicy(Severity severity, int responseMinutes, int resolveMinutes)
    {
        if (responseMinutes <= 0 || resolveMinutes < responseMinutes)
            throw new ArgumentException("Resolve target must be at least the response target, and both positive.");
        Severity = severity;
        ResponseMinutes = responseMinutes;
        ResolveMinutes = resolveMinutes;
    }
}
