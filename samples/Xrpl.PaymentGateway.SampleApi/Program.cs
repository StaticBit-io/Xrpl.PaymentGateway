using Xrpl.PaymentGateway;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.SampleApi;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

app.MapPost("/checkout/{buyerId}", async (
    string buyerId,
    IPaymentGateway gateway,
    CancellationToken cancellationToken) =>
{
    PaymentInstructions instructions = await gateway.GetPaymentInstructionsAsync(buyerId, cancellationToken);
    return Results.Ok(new { instructions.Address, instructions.DestinationTag });
});

app.MapGet("/payments", (SamplePaymentHandler handler) => Results.Ok(handler.Delivered));

// Reading back everything ever recorded is not part of IPaymentStore — the gateway never needs it — so
// this endpoint only exists when the sample is running on the in-memory store that offers a snapshot.
app.MapGet("/recorded", (IPaymentStore store) => store is InMemoryPaymentStore inMemory
    ? Results.Ok(inMemory.Snapshot())
    : Results.NotFound(new { message = "the configured store does not expose a snapshot" }));

app.MapGet("/health", async (IPaymentMonitorHealth health, CancellationToken cancellationToken) =>
{
    PaymentMonitorHealthReport report = await health.CheckAsync(cancellationToken);
    return report.IsHealthy ? Results.Ok(report) : Results.Json(report, statusCode: 503);
});

app.MapPost("/reconcile", async (IPaymentMonitorHealth health, CancellationToken cancellationToken) =>
    Results.Ok(await health.ReconcileAsync(cancellationToken)));

app.Run();
