using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xrpl.PaymentGateway.Abstractions;
using Xrpl.PaymentGateway.Internal;

namespace Xrpl.PaymentGateway;

/// <summary>Registration for the payment gateway.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gateway, the health service and the background monitor. The host must separately
    /// register its own <see cref="IPaymentStore"/> and <see cref="IPaymentReceivedHandler"/>.
    /// </summary>
    public static IServiceCollection AddXrplPaymentGateway(
        this IServiceCollection services,
        Action<PaymentGatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PaymentGatewayOptions>, PaymentGatewayOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MonitorSnapshot>();
        services.TryAddSingleton<IXrplNodeConnectionFactory, XrplNodeConnectionFactory>();
        services.TryAddSingleton<IPaymentGateway, XrplPaymentGateway>();
        services.TryAddSingleton<IPaymentMonitorHealth, PaymentMonitorHealth>();

        // TryAddEnumerable rather than AddHostedService: calling this twice would otherwise start two
        // monitors against one account and one cursor.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, XrplPaymentMonitor>());

        return services;
    }
}
