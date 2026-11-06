namespace SlaTracker.Api.Endpoints;

/// <summary>Collects field errors; Throw() raises them as one 400 response.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, string[]> _errors = new();

    public Validator Required(string field, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(field, "is required");
        else if (value.Trim().Length > max) Add(field, $"must be at most {max} characters");
        return this;
    }

    public Validator Optional(string field, string? value, int max)
    {
        if (value is not null && value.Length > max) Add(field, $"must be at most {max} characters");
        return this;
    }

    public Validator Check(bool ok, string field, string message)
    {
        if (!ok) Add(field, message);
        return this;
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0) throw new RequestValidationException(_errors);
    }

    private void Add(string field, string msg) =>
        _errors[field] = _errors.TryGetValue(field, out var m) ? m.Append(msg).ToArray() : new[] { msg };
}

public sealed class RequestValidationException(IDictionary<string, string[]> errors) : Exception("Validation failed")
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}
