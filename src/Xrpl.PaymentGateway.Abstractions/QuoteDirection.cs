namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>Which side of a quote the caller pinned.</summary>
public enum QuoteDirection
{
    /// <summary>"This much arrived — what is it worth." The valuation path.</summary>
    ExactInput,

    /// <summary>"I need this much of the quote asset — how much to ask for." The checkout path.</summary>
    ExactOutput,
}
