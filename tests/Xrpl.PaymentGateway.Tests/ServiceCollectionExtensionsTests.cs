using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xunit;

namespace Xrpl.PaymentGateway.Tests;

public class ServiceCollectionExtensionsTests
{
    private static ServiceCollection BaseServices()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPaymentStore>(new InMemoryPaymentStore());
        services.AddSingleton<IPaymentReceivedHandler, Fakes.RecordingHandler>();
        return services;
    }

    [Fact]
    public void RegistrationResolvesTheGatewayTheHealthServiceAndTheHostedMonitor()
    {
        ServiceCollection services = BaseServices();
        services.AddXrplPaymentGateway(options =>
        {
            options.Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";
            options.Nodes = new[] { new Uri("ws://localhost:6006") };
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<XrplPaymentGateway>(provider.GetRequiredService<IPaymentGateway>());
        Assert.IsType<PaymentMonitorHealth>(provider.GetRequiredService<IPaymentMonitorHealth>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<XrplPaymentMonitor>());
    }

    [Fact]
    public void InvalidOptionsFailWhenTheOptionsAreFirstRead()
    {
        ServiceCollection services = BaseServices();
        services.AddXrplPaymentGateway(options => options.Address = string.Empty);

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<PaymentGatewayOptions>>().Value);
        Assert.Contains("Address", string.Join(" ", error.Failures));
    }

    [Fact]
    public void AHostSuppliedStoreIsNotReplaced()
    {
        InMemoryPaymentStore store = new InMemoryPaymentStore();
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPaymentStore>(store);
        services.AddSingleton<IPaymentReceivedHandler, Fakes.RecordingHandler>();
        services.AddXrplPaymentGateway(options =>
        {
            options.Address = "rLiooJRSKeiNfRJcDBUhu4rcjQjGLWqa4p";
            options.Nodes = new[] { new Uri("ws://localhost:6006") };
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(store, provider.GetRequiredService<IPaymentStore>());
    }
}
