namespace Betl.Core;

public class BetlException : Exception
{
    public BetlException(string message) : base(message) { }
    public BetlException(string message, Exception inner) : base(message, inner) { }
}

public sealed class PipelineLoadException : BetlException
{
    public PipelineLoadException(string message) : base(message) { }
    public PipelineLoadException(string message, Exception inner) : base(message, inner) { }
}

public sealed class PipelineValidationException : BetlException
{
    public IReadOnlyList<string> Errors { get; }

    public PipelineValidationException(IReadOnlyList<string> errors)
        : base("Pipeline failed validation:\n  - " + string.Join("\n  - ", errors))
    {
        Errors = errors;
    }
}
