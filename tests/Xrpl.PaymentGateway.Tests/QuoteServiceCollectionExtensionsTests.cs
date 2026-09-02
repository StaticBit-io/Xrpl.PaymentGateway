using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Tests.Fakes;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class QuoteServiceCollectionExtensionsTests
{
    private const string XpmIssuer = "rXPMxBeefHGxx2K7g5qmmWq3gFsgawkoa";
    private const string RlusdIssuer = "rMxCKbEDwqr76QuheSUMdEGf4B9xJ8m5De";

    private static ServiceCollection GatewayServices()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPaymentStore>(new InMemoryPaymentStore());
        services.AddSingleton<IPaymentReceivedHandler>(new RecordingHandler());
        services.AddXrplPaymentGateway(options =>
        {
            options.Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";
            options.Nodes = new[] { new Uri("ws://localhost:6006") };
        });

        return services;
    }

    private static void AddQuotes(ServiceCollection services)
    {
        services.AddSingleton<IQuoteStore>(new InMemoryQuoteStore());
        services.AddSingleton<IQuoteSource>(new ScriptedQuoteSource());
        services.AddSingleton<IPaymentValuedHandler>(new RecordingValuedHandler());
        services.AddXrplPaymentQuotes(options =>
            options.Pairs = new[] { new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer) });
    }

    [Fact]
    public void WithoutQuotesTheGatewayStartsExactlyOneHostedService()
    {
        // The 1.0.0 shape must survive: one monitor, nothing else.
        ServiceProvider provider = GatewayServices().BuildServiceProvider();

        Assert.Single(provider.GetServices<IHostedService>());
        Assert.Null(provider.GetService<IQuoteReader>());
        Assert.Null(provider.GetService<IQuoteHealth>());
    }

    [Fact]
    public void WithQuotesTheCollectorAndTheValuationWorkerAreAdded()
    {
        ServiceCollection services = GatewayServices();
        AddQuotes(services);
        ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(3, provider.GetServices<IHostedService>().Count());
        Assert.NotNull(provider.GetService<IQuoteReader>());
        Assert.NotNull(provider.GetService<IQuoteHealth>());
    }

    [Fact]
    public void RegisteringQuotesTwiceStillStartsOneCollector()
    {
        ServiceCollection services = GatewayServices();
        AddQuotes(services);
        services.AddXrplPaymentQuotes(options =>
            options.Pairs = new[] { new QuotePair("XPM", XpmIssuer, "USD", RlusdIssuer) });
        ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(3, provider.GetServices<IHostedService>().Count());
    }

    [Fact]
    public void MisconfiguredQuotesFailWhenTheOptionsAreFirstRead()
    {
        ServiceCollection services = GatewayServices();
        services.AddSingleton<IQuoteStore>(new InMemoryQuoteStore());
        services.AddSingleton<IQuoteSource>(new ScriptedQuoteSource());
        services.AddSingleton<IPaymentValuedHandler>(new RecordingValuedHandler());
        services.AddXrplPaymentQuotes(options => options.Pairs = Array.Empty<QuotePair>());
        ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => provider.GetRequiredService<IQuoteHealth>());
    }
}
