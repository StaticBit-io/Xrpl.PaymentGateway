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

builder.Services.AddSingleton<SamplePaymentHandler>();
builder.Services.AddSingleton<IPaymentReceivedHandler>(services => services.GetRequiredService<SamplePaymentHandler>());

builder.Services.AddXrplPaymentGateway(options =>
{
    options.Address = builder.Configuration["Xrpl:Address"]
        ?? throw new InvalidOperationException("configure Xrpl:Address with the receiving r-address");
    options.Nodes = (builder.Configuration.GetSection("Xrpl:Nodes").Get<string[]>() ?? ["ws://localhost:6006"])
        .Select(node => new Uri(node))
        .ToArray();
    options.FirstDestinationTag = firstTag;
});

// Quotes are optional in the library, and stay optional here: with no pairs configured (the shipped
// default), AddXrplPaymentQuotes is never called, so a host that ignores this section gets exactly what
// it got before 1.1.0 — no new background service, no new network traffic.
QuoteConfiguration quoteConfiguration = QuoteConfiguration.FromConfiguration(builder.Configuration);

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

    // A deliberate stand-in, not a pricing engine: see FixedRateQuoteSource's own remarks for what a real
    // IQuoteSource would do instead.
    builder.Services.AddSingleton<IQuoteSource>(_ => new FixedRateQuoteSource(
        quoteConfiguration.RatesByPairKey, quoteConfiguration.RefusedCurrencies));

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

// What the sample's one fictional item costs, in the quote asset. A real host reads this from its own
// catalog; a demo just needs one fixed number to ask ExactOutput about.
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

        prices.Add(new
        {
            pair.Currency,
            pair.Issuer,
            pair.QuoteCurrency,
            pair.QuoteIssuer,
            QuotePrice = DemoItemQuotePrice,
            InputAmount = view.Result?.InputAmount,
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
app.MapGet("/api/checkout/{buyerId}/valuations", (string buyerId) => Results.Ok(
    valuedHandler?.Valued.Where(v => v.BuyerId == buyerId) ?? Enumerable.Empty<ValuedPayment>()));

app.MapGet("/api/valuations", () =>
    Results.Ok(valuedHandler?.Valued ?? (IReadOnlyCollection<ValuedPayment>)Array.Empty<ValuedPayment>()));

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

    return Results.Ok(page);
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

app.Run();

// What the unresolved operator endpoints above bind their request bodies to.
internal sealed record SettleRequest(decimal Rate);

internal sealed record WriteOffRequest(string Reason);
