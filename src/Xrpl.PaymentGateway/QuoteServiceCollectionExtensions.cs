using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;

namespace Xrpl.PaymentGateway;

/// <summary>Registration for the optional quote collector.</summary>
public static class QuoteServiceCollectionExtensions
{
    /// <summary>
    /// Registers the quote collector, the valuation worker, the reader and the health service.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ServiceCollectionExtensions.AddXrplPaymentGateway"/> so that a host which
    /// upgrades and changes nothing else gets no new background service and no new network traffic.
    /// The host must separately register <see cref="IQuoteSource"/>, <see cref="IQuoteStore"/> and
    /// <see cref="IPaymentValuedHandler"/>.
    /// </remarks>
    public static IServiceCollection AddXrplPaymentQuotes(
        this IServiceCollection services,
        Action<QuoteOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<QuoteOptions>, QuoteOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider =>
            new QuoteRegistry(provider.GetRequiredService<IOptions<QuoteOptions>>().Value.Pairs));

        services.TryAddSingleton(provider => new ValuationEnqueuer(
            provider.GetRequiredService<IQuoteStore>(),
            provider.GetRequiredService<QuoteRegistry>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<ValuationEnqueuer>()));

        services.TryAddSingleton<IQuoteReader, QuoteReader>();
        services.TryAddSingleton<IQuoteHealth, QuoteHealth>();

        // TryAddEnumerable, as with the monitor: calling this twice must not start two collectors
        // hitting the node at double the configured rate.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, QuoteCollector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ValuationWorker>());

        return services;
    }
}
