namespace Xrpl.PaymentGateway.Abstractions;

/// <summary>
/// Which side of a quote the caller pinned.
/// </summary>
/// <remarks>
/// Whichever direction is asked, <see cref="QuoteResult"/>'s amount fields always describe the trade in
/// pay-to-get terms: <see cref="QuoteResult.InputAmount"/> is the amount of the received asset the trade
/// requires, <see cref="QuoteResult.FilledInput"/> is how much of it the venues could actually absorb, and
/// <see cref="QuoteResult.OutputAmount"/> is the quote-asset amount that produces. The direction only says
/// which of <see cref="QuoteResult.InputAmount"/> or <see cref="QuoteResult.OutputAmount"/> was the
/// caller's ask and which was computed to satisfy it.
/// </remarks>
public enum QuoteDirection
{
    /// <summary>
    /// The caller pinned <see cref="QuoteResult.InputAmount"/>: "this much arrived — what is it worth."
    /// The valuation path.
    /// </summary>
    ExactInput,

    /// <summary>
    /// The caller pinned <see cref="QuoteResult.OutputAmount"/>: "I need this much of the quote asset —
    /// how much do I ask for." The implementation computes the <see cref="QuoteResult.InputAmount"/> that
    /// yields it. The checkout path.
    /// </summary>
    ExactOutput,
}
