using System.Text.Json.Serialization;
using Xrpl.PaymentGateway;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.SampleApi;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// PaymentMonitorState reaches the page as "Streaming" rather than 3. A number would force every consumer
// to keep its own copy of the enum's ordering, and that copy goes stale silently.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The store is the host's choice, and this sample makes that concrete: set Xrpl:StorePath and payments
// survive a restart in a plain file; leave it unset and everything lives in memory. A database is the
// same swap — implement IPaymentStore, or take Xrpl.PaymentGateway.Postgres — with no change below.
// Note that tag allocation belongs to the store, so the store is where the first tag is configured;
// PaymentGatewayOptions.FirstDestinationTag is the value to hand it, not a setting the library applies
// behind your back.
uint firstTag = builder.Configuration.GetValue<uint?>("Xrpl:FirstDestinationTag") ?? 1;
string? storePath = builder.Configuration["Xrpl:StorePath"];

if (string.IsNullOrWhiteSpace(storePath))
{
    builder.Services.AddSingleton<InMemoryPaymentStore>(_ => new InMemoryPaymentStore(firstTag));
    builder.Services.AddSingleton<IPaymentStore>(services => services.GetRequiredService<InMemoryPaymentStore>());
}
else
{
    builder.Services.AddSingleton<IPaymentStore>(_ => new FilePaymentStore(storePath, firstTag));
}

// The X-address carries the network it is for, so this has to be told which one. The documented demo
// runs against the standalone stand, hence the default; point the sample at mainnet and set it to false.
builder.Services.AddSingleton(new CheckoutPresentation(
    builder.Configuration.GetValue<bool?>("Xrpl:IsTestNetwork") ?? true));

// Read regardless of whether any pair is configured: SamplePaymentHandler asks it, for every payment,
// whether that payment is already the asset valuations are expressed in — a question with a definite
// answer (always "no") even when quotes are off entirely.
QuoteConfiguration quoteConfiguration = QuoteConfiguration.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(quoteConfiguration);

builder.Services.AddSingleton<SamplePaymentHandler>();
builder.Services.AddSingleton<IPaymentReceivedHandler>(services => services.GetRequiredService<SamplePaymentHandler>());

string[] nodes = builder.Configuration.GetSection("Xrpl:Nodes").Get<string[]>() ?? ["ws://localhost:6006"];

builder.Services.AddXrplPaymentGateway(options =>
{
    options.Address = builder.Configuration["Xrpl:Address"]
        ?? throw new InvalidOperationException("configure Xrpl:Address with the receiving r-address");
    options.Nodes = nodes.Select(node => new Uri(node)).ToArray();
    options.FirstDestinationTag = firstTag;
});

// One connection to the node for everything in this sample that needs to talk to one — the demo payer and,
// when configured, the AMM quote source. Not the gateway's connection: that one belongs to the gateway.
builder.Services.AddSingleton(_ => new NodeConnection(nodes[0]));

// A wallet the sample can pay itself from, so the demonstration does not need a second terminal. Registered
// only when a seed is configured; without one the endpoints below answer 404 and the page hides the buttons.
if (DemoPayer.IsConfigured(builder.Configuration))
{
    builder.Services.AddSingleton(services => DemoPayer.Create(
        builder.Configuration,
        services.GetRequiredService<NodeConnection>(),
        services.GetRequiredService<ILogger<DemoPayer>>()));
}

// Quotes are optional in the library, and stay optional here: with no pairs configured (the shipped
// default), AddXrplPaymentQuotes is never called, so a host that ignores this section gets exactly what
// it got before 1.1.0 — no new background service, no new network traffic.
if (quoteConfiguration.IsEnabled)
{
    // The quote store follows the same in-memory/file switch as the payment store, so the two halves of
    // the gateway's state can never end up split across different backends. FileQuoteStore deliberately
    // keeps its own file rather than sharing the payment store's — see its remarks — hence the suffix.
    if (string.IsNullOrWhiteSpace(storePath))
    {
        builder.Services.AddSingleton<IQuoteStore, InMemoryQuoteStore>();
    }
    else
    {
        builder.Services.AddSingleton<IQuoteStore>(_ => new FileQuoteStore(storePath + ".quotes.json"));
    }

    // Where prices come from. "amm" reads them off the ledger; anything else, including the shipped
    // default, uses the fixed rates in configuration. Two sources rather than one because they demonstrate
    // different things: the fixed one shows the shape of the integration with nothing else running, and the
    // AMM one shows what a reading off a real ledger does to it — a marginal price that is not the price
    // for your size, a number that moves between refreshes, and a ledger index that is a ledger index.
    if (string.Equals(builder.Configuration["Xrpl:Quotes:Source"], "amm", StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<IQuoteSource>(services => new AmmQuoteSource(
            services.GetRequiredService<NodeConnection>()));
    }
    else
    {
        builder.Services.AddSingleton<IQuoteSource>(_ => new FixedRateQuoteSource(
            quoteConfiguration.RatesByPairKey, quoteConfiguration.RefusedCurrencies));
    }

    builder.Services.AddSingleton<SampleValuedHandler>();
    builder.Services.AddSingleton<IPaymentValuedHandler>(services => services.GetRequiredService<SampleValuedHandler>());

    builder.Services.AddXrplPaymentQuotes(options => options.Pairs = quoteConfiguration.Pairs);
}

WebApplication app = builder.Build();

// Resolved once at startup rather than injected per request: with no pairs configured these are simply
// null, and every quote endpoint below treats that as "this section has nothing to show" instead of
// failing to bind a service nothing registered.
IQuoteReader? quoteReader = app.Services.GetService<IQuoteReader>();
IQuoteHealth? quoteHealth = app.Services.GetService<IQuoteHealth>();
IUnresolvedValuationAdmin? unresolvedValuationAdmin = app.Services.GetService<IUnresolvedValuationAdmin>();
SampleValuedHandler? valuedHandler = app.Services.GetService<SampleValuedHandler>();
DemoPayer? demoPayer = app.Services.GetService<DemoPayer>();

// What the demo payer is allowed to send. With quotes configured it is exactly what the sample can name —
// every priced asset plus the one they are priced into. With quotes off there is no such list, and XRP is
// the only asset the sample can describe without one.
IReadOnlyList<AcceptedAsset> demoAssets = quoteConfiguration.IsEnabled
    ? quoteConfiguration.AcceptedAssets
    : [new AcceptedAsset("XRP", null, false)];

// A ceiling on one demo payment. The payer holds nothing but stand money, so this guards against a
// mistyped amount emptying it mid-demonstration, not against loss.
const decimal MaxDemoPayment = 1_000_000m;

// Maps from pair key to readable currency codes. Pairs are configured at startup, so these
// mappings are stable for the lifetime of the application.
Dictionary<string, (string Currency, string QuoteCurrency)> pairCurrenciesByKey =
    new Dictionary<string, (string, string)>(StringComparer.Ordinal);
foreach (QuotePair pair in quoteConfiguration.Pairs)
{
    pairCurrenciesByKey[pair.Key] = (pair.Currency, pair.QuoteCurrency);
}

// What the sample's one fictional item costs, in USD — the asset every accepted currency here prices
// into. A real host reads this from its own catalog; a demo just needs one fixed number to ask
// ExactOutput about. A buyer paying in USD itself needs none of this — see QuoteConfiguration.IsQuoteAsset,
// which is what lets SamplePaymentHandler mark such a payment as already priced, at its own amount.
const decimal DemoItemQuotePrice = 25m;

// The checkout page. Plain files, no build step: `dotnet run` is the whole toolchain.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/checkout/{buyerId}", async (
    string buyerId,
    IPaymentGateway gateway,
    CheckoutPresentation presentation,
    CancellationToken cancellationToken) =>
{
    PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync(buyerId, cancellationToken);

    return Results.Ok(new
    {
        instructions.Address,
        instructions.DestinationTag,

        // One string carrying both, which is what a scanner can be given without losing the tag.
        XAddress = presentation.ToXAddress(instructions),
    });
});

// The same X-address as a picture. Recomputed from the buyer id rather than taken from the query, so the
// image cannot be pointed at somebody else's tag.
app.MapGet("/api/checkout/{buyerId}/qr.svg", async (
    string buyerId,
    IPaymentGateway gateway,
    CheckoutPresentation presentation,
    CancellationToken cancellationToken) =>
{
    PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync(buyerId, cancellationToken);
    string svg = presentation.ToQrSvg(presentation.ToXAddress(instructions));

    return Results.Content(svg, "image/svg+xml");
});

// What the checkout page polls: has this particular buyer's money arrived yet?
app.MapGet("/api/checkout/{buyerId}/payments", (string buyerId, SamplePaymentHandler handler) =>
    Results.Ok(handler.Delivered.Where(p => p.BuyerId == buyerId)));

app.MapGet("/api/payments", (SamplePaymentHandler handler) => Results.Ok(handler.Delivered));

// Reading back everything ever recorded is not part of IPaymentStore — the gateway never needs it — so
// this endpoint only exists when the sample is running on the in-memory store that offers a snapshot.
app.MapGet("/api/recorded", (IPaymentStore store) => store is InMemoryPaymentStore inMemory
    ? Results.Ok(inMemory.Snapshot())
    : Results.NotFound(new { message = "the configured store does not expose a snapshot" }));

app.MapGet("/api/health", async (IPaymentMonitorHealth health, CancellationToken cancellationToken) =>
{
    PaymentMonitorHealthReport report = await health.CheckAsync(cancellationToken);

    // A monitor that is merely catching up is not yet healthy but is not broken either, so the page gets
    // the full report in both cases and decides what to show.
    return report.IsHealthy ? Results.Ok(report) : Results.Json(report, statusCode: 503);
});

app.MapPost("/api/reconcile", async (IPaymentMonitorHealth health, CancellationToken cancellationToken) =>
    Results.Ok(await health.ReconcileAsync(cancellationToken)));

// What the buyer is being asked for, before any money moves: ExactOutput against the fictional item's
// price, one entry per pair that currently holds a usable reading. A refused pair never captures a
// snapshot, so it never appears here — the same reason it never appears priced anywhere else. Empty when
// quotes are not configured at all, which is what lets the page hide the section rather than show nothing.
app.MapGet("/api/checkout/{buyerId}/price", async (string buyerId, CancellationToken cancellationToken) =>
{
    if (quoteReader is null)
    {
        return Results.Ok(Array.Empty<object>());
    }

    List<object> prices = new List<object>();
    foreach (QuotePair pair in quoteConfiguration.Pairs)
    {
        QuoteView? view = await quoteReader.QuoteAsync(
            pair.Currency, pair.Issuer, DemoItemQuotePrice, QuoteDirection.ExactOutput, cancellationToken);

        if (view is null)
        {
            continue;
        }

        // This is an ask, not a report: it is what the buyer is about to be told to send, so it is rounded
        // up to the pair's own asset — XRP to the drop, an issued currency to fifteen significant digits —
        // rather than to the nearest one. Rounding down here would ask for less than the invoice actually
        // costs, and a buyer who sends exactly the figure shown would then be short. Done here, once,
        // server-side, so the JSON the page renders and the JSON a future copy button would read are the
        // same rounded number by construction, never two different amounts split across a display format
        // and a raw value.
        decimal? inputAmount = view.Result?.InputAmount is decimal rawInputAmount
            ? AssetPrecision.RoundUpForAsk(rawInputAmount, pair.Currency)
            : null;

        prices.Add(new
        {
            pair.Currency,
            pair.Issuer,
            pair.QuoteCurrency,
            pair.QuoteIssuer,
            QuotePrice = DemoItemQuotePrice,
            InputAmount = inputAmount,

            // What this size costs against what one unit costs. The two are the same number only for a
            // source that ignores size — the shipped fixed-rate one does — and differ for any reading of
            // real liquidity, which is the whole reason a quote takes an amount at all.
            view.Result?.MarginalPrice,
            view.Result?.EffectivePrice,
            view.Result?.SlippagePercent,
            view.CapturedAt,
            view.Age,
            view.LedgerIndex,
            view.IsStale,
        });
    }

    return Results.Ok(prices);
});

// What this buyer's payments turned out to be worth. Deliberately a separate poll from
// /api/checkout/{buyerId}/payments rather than one merged row: a payment is announced the moment it
// arrives, and its valuation is a second, later signal — collapsing them into one row that shows up
// already priced would hide the ordering the whole feature rests on.
app.MapGet("/api/checkout/{buyerId}/valuations", (string buyerId) =>
{
    IEnumerable<ValuedPayment> valuations = valuedHandler?.Valued.Where(v => v.BuyerId == buyerId)
        ?? Enumerable.Empty<ValuedPayment>();

    // Add readable currency codes to each valuation for display
    List<object> result = new List<object>();
    foreach (ValuedPayment valuation in valuations)
    {
        string? quoteCurrency = valuation.QuoteCurrency;
        if (pairCurrenciesByKey.TryGetValue(valuation.PairKey, out var currencies))
        {
            quoteCurrency = currencies.QuoteCurrency;
        }

        result.Add(new
        {
            valuation.TransactionHash,
            valuation.BuyerId,
            valuation.PairKey,
            ReadableQuoteCurrency = quoteCurrency,
            valuation.State,
            valuation.Amount,
            QuoteAmount = RoundQuoteAmountForReport(valuation.QuoteAmount, quoteCurrency),
            valuation.EffectivePrice,
            valuation.FailureReason,
            valuation.WriteOffReason,
            valuation.ResolvedAt,
        });
    }

    return Results.Ok(result);
});

app.MapGet("/api/valuations", () =>
{
    IReadOnlyCollection<ValuedPayment> valuations = valuedHandler?.Valued
        ?? (IReadOnlyCollection<ValuedPayment>)Array.Empty<ValuedPayment>();

    List<object> result = new List<object>();
    foreach (ValuedPayment valuation in valuations)
    {
        string? quoteCurrency = valuation.QuoteCurrency;
        if (pairCurrenciesByKey.TryGetValue(valuation.PairKey, out var currencies))
        {
            quoteCurrency = currencies.QuoteCurrency;
        }

        result.Add(new
        {
            valuation.TransactionHash,
            valuation.BuyerId,
            valuation.PairKey,
            ReadableQuoteCurrency = quoteCurrency,
            valuation.State,
            valuation.Amount,
            QuoteAmount = RoundQuoteAmountForReport(valuation.QuoteAmount, quoteCurrency),
            valuation.EffectivePrice,
            valuation.FailureReason,
            valuation.WriteOffReason,
            valuation.ResolvedAt,
        });
    }

    return Results.Ok(result);
});

app.MapGet("/api/quotes/health", async (CancellationToken cancellationToken) =>
{
    if (quoteHealth is null)
    {
        return Results.NotFound(new { message = "no quote pairs are configured" });
    }

    QuoteHealthReport report = await quoteHealth.CheckAsync(cancellationToken);
    return report.IsHealthy ? Results.Ok(report) : Results.Json(report, statusCode: 503);
});

// The operator's queue: payments the automatic pipeline has not resolved, oldest first. minAge is zero
// rather than the library's own 15-minute default — a demo should show a stuck entry the moment it exists,
// not a quarter of an hour later.
app.MapGet("/api/quotes/unresolved", async (int? limit, int? offset, CancellationToken cancellationToken) =>
{
    if (unresolvedValuationAdmin is null)
    {
        return Results.NotFound(new { message = "no quote pairs are configured" });
    }

    UnresolvedValuationPage page = await unresolvedValuationAdmin
        .ListUnresolvedAsync(limit ?? 50, offset ?? 0, TimeSpan.Zero, cancellationToken)
        .ConfigureAwait(false);

    // Map valuations to include readable currency codes for display
    List<object> items = new List<object>();
    foreach (PaymentValuation valuation in page.Items)
    {
        string? readableCurrency = null;
        string? readableQuoteCurrency = null;

        if (pairCurrenciesByKey.TryGetValue(valuation.PairKey, out var currencies))
        {
            readableCurrency = currencies.Currency;
            readableQuoteCurrency = currencies.QuoteCurrency;
        }

        items.Add(new
        {
            valuation.TransactionHash,
            valuation.PairKey,
            ReadableCurrency = readableCurrency,
            ReadableQuoteCurrency = readableQuoteCurrency,
            Amount = RoundReceivedAmountForReport(valuation.Amount, readableCurrency),
            valuation.State,
            valuation.FailureReason,
            valuation.EnqueuedAt,
        });
    }

    return Results.Ok(new
    {
        Items = items,
        page.TotalCount,
    });
});

// An operator found a real price some other way. Runs through IUnresolvedValuationAdmin, which leaves the
// entry undelivered exactly as an automatic valuation does — the same ValuationWorker pass that already
// retries a stuck delivery is what hands this to SampleValuedHandler, within one poll.
app.MapPost("/api/quotes/unresolved/{transactionHash}/settle", async (
    string transactionHash, SettleRequest request, CancellationToken cancellationToken) =>
{
    if (unresolvedValuationAdmin is null)
    {
        return Results.NotFound(new { message = "no quote pairs are configured" });
    }

    try
    {
        await unresolvedValuationAdmin.ValueManuallyAsync(transactionHash, request.Rate, cancellationToken);
        return Results.Ok();
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        // Not currently Pending or Failed — an operator (maybe this same one, twice) already resolved it.
        return Results.Conflict(new { message = ex.Message });
    }
});

// An operator decided this one will never be credited — dust, a spam token, a mistaken transfer.
app.MapPost("/api/quotes/unresolved/{transactionHash}/write-off", async (
    string transactionHash, WriteOffRequest request, CancellationToken cancellationToken) =>
{
    if (unresolvedValuationAdmin is null)
    {
        return Results.NotFound(new { message = "no quote pairs are configured" });
    }

    try
    {
        await unresolvedValuationAdmin.WriteOffAsync(transactionHash, request.Reason, cancellationToken);
        return Results.Ok();
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { message = ex.Message });
    }
});

// What the page needs to draw the pay buttons: who is paying, and which assets it may be asked for. 404
// when no seed is configured, which is what hides that whole block rather than showing dead buttons.
app.MapGet("/api/demo", () => demoPayer is null
    ? Results.NotFound(new { message = "the demo payer is not configured" })
    : Results.Ok(new { Payer = demoPayer.Address, Assets = demoAssets }));

// Pays this checkout from the demo wallet. The destination is not a parameter: it is the address and tag
// the gateway itself issues for this buyer, fetched here the same way the page fetched them, so the button
// cannot pay anywhere else. The response is the node's provisional verdict — the payment reaches the page
// through the gateway's monitor, like any other.
app.MapPost("/api/checkout/{buyerId}/pay", async (
    string buyerId,
    DemoPaymentRequest request,
    IPaymentGateway gateway,
    CancellationToken cancellationToken) =>
{
    if (demoPayer is null)
    {
        return Results.NotFound(new { message = "the demo payer is not configured" });
    }

    if (request.Amount <= 0m || request.Amount > MaxDemoPayment)
    {
        return Results.BadRequest(new { message = $"amount must be above zero and at most {MaxDemoPayment}" });
    }

    AcceptedAsset? asset = FindAcceptedAsset(demoAssets, request.Currency, request.Issuer);
    if (asset is null)
    {
        return Results.BadRequest(new { message = $"{request.Currency} is not an asset this sample accepts" });
    }

    PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync(buyerId, cancellationToken);
    decimal amount = AssetPrecision.RoundNearestForSending(request.Amount, asset.Currency);

    try
    {
        DemoPaymentResult result = await demoPayer.PayAsync(
            instructions.Address, instructions.DestinationTag, asset.Currency, asset.Issuer, amount, cancellationToken);

        object body = new
        {
            result.EngineResult,
            result.TransactionHash,
            Amount = amount,
            asset.Currency,
            instructions.DestinationTag,
        };

        // Anything but tesSUCCESS is the node refusing the payment — a missing trust line, an unfunded
        // payer. Reported as a failure rather than dressed up as an accepted one.
        return result.EngineResult == "tesSUCCESS" ? Results.Ok(body) : Results.Json(body, statusCode: 502);
    }
    catch (Exception problem)
    {
        // The node was unreachable, or refused the submission outright. A demo button should say so on the
        // page rather than leave the browser waiting on a request that already failed.
        return Results.Json(new { message = problem.Message }, statusCode: 502);
    }
});

app.Run();

// Both helpers below back a report, not an ask — a valuation and the unresolved queue describe money that
// has already moved, so they round to the nearest expressible unit rather than up. See
// AssetPrecision.RoundNearestForReport for why that direction is the deliberate choice here, and the
// /api/checkout/{buyerId}/price handler above for the ask side, which rounds up instead.

// The quote currency is only unknown when a valuation's PairKey no longer matches any configured pair —
// not reachable with this sample's fixed startup configuration, but AssetPrecision still needs some
// currency to round against, so it falls through to the issued-currency rule rather than throwing.
decimal? RoundQuoteAmountForReport(decimal? quoteAmount, string? quoteCurrency) =>
    quoteAmount is decimal amount ? AssetPrecision.RoundNearestForReport(amount, quoteCurrency ?? string.Empty) : null;

decimal RoundReceivedAmountForReport(decimal amount, string? currency) =>
    AssetPrecision.RoundNearestForReport(amount, currency ?? string.Empty);

// Currency codes are compared the way the library compares them — canonically, so "USD" and its 40-character
// hex form are one asset — and the issuer exactly, because two issuers of the same code are two assets.
static AcceptedAsset? FindAcceptedAsset(IReadOnlyList<AcceptedAsset> assets, string currency, string? issuer)
{
    string canonical;
    try
    {
        canonical = CurrencyKey.Canonical(currency);
    }
    catch (ArgumentException)
    {
        // Not a currency code at all. No asset can match it.
        return null;
    }

    string? normalizedIssuer = string.IsNullOrWhiteSpace(issuer) ? null : issuer;

    return assets.FirstOrDefault(asset =>
        string.Equals(CurrencyKey.Canonical(asset.Currency), canonical, StringComparison.Ordinal)
        && string.Equals(asset.Issuer, normalizedIssuer, StringComparison.Ordinal));
}

// What the unresolved operator endpoints above bind their request bodies to.
internal sealed record SettleRequest(decimal Rate);

// What the demo payer's endpoint binds to. Amount is what the page put in the box, before this sample rounds
// it to something the asset can actually express.
internal sealed record DemoPaymentRequest(string Currency, string? Issuer, decimal Amount);

internal sealed record WriteOffRequest(string Reason);
