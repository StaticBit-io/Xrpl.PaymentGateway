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

WebApplication app = builder.Build();

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

app.Run();
