using Consul;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Services.Core.ServiceDiscovery;

/// <summary>
/// Registers Consul service discovery in the dependency injection container.
/// </summary>
public static class ServiceDiscoveryExtensions
{
  /// <summary>
  /// Binds and validates <see cref="ServiceConfig"/>, registers the Consul client and
  /// the hosted service that keeps this instance registered in Consul.
  /// </summary>
  public static IServiceCollection AddConsulServiceDiscovery(this IServiceCollection services)
  {
    ArgumentNullException.ThrowIfNull(services);

    services
      .AddOptions<ServiceConfig>()
      .BindConfiguration(ServiceConfig.SectionName)
      .ValidateDataAnnotations()
      .ValidateOnStart();

    services.AddSingleton<IConsulClient>(provider =>
    {
      ServiceConfig serviceConfig = provider.GetRequiredService<IOptions<ServiceConfig>>().Value;
      return new ConsulClient(config => config.Address = serviceConfig.ConsulUrl);
    });

    services.AddHostedService<ServiceDiscoveryHostedService>();

    return services;
  }
}
