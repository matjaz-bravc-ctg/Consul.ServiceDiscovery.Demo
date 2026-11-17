using Consul;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Services.Core.ServiceDiscovery;

/// <summary>
/// Registers the service in the Consul registry and deregisters it on shutdown.
/// Registration is retried with exponential backoff until Consul becomes reachable,
/// so the service still registers when it starts before the Consul agent is ready.
/// </summary>
public class ServiceDiscoveryHostedService(
  ILogger<ServiceDiscoveryHostedService> logger,
  IConsulClient client,
  ServiceConfig config)
  : BackgroundService
{
  private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
  private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

  private readonly AgentServiceRegistration _serviceRegistration = new()
  {
    ID = config.Id,
    Name = config.Name,
    Address = config.ApiUrl,
    Port = config.Port,
    Check = new AgentServiceCheck
    {
      DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(5),
      Interval = TimeSpan.FromSeconds(15),
      HTTP = $"http://{config.ApiUrl}:{config.Port}/api/values/{config.HealthCheckEndPoint}",
      Timeout = TimeSpan.FromSeconds(5)
    }
  };

  private volatile bool _isRegistered;

  /// <summary>
  /// Registers the service to the Consul registry, retrying until it succeeds or the host stops.
  /// </summary>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    TimeSpan retryDelay = InitialRetryDelay;
    int attempt = 0;

    while (!stoppingToken.IsCancellationRequested)
    {
      attempt++;

      try
      {
        await client.Agent.ServiceDeregister(_serviceRegistration.ID, stoppingToken).ConfigureAwait(false);
        await client.Agent.ServiceRegister(_serviceRegistration, stoppingToken).ConfigureAwait(false);

        _isRegistered = true;
        logger.LogInformation(
          "Service {ServiceId} registered in Consul after {Attempt} attempt(s)",
          _serviceRegistration.ID,
          attempt);

        return;
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        logger.LogWarning(
          "Consul registration attempt {Attempt} for service {ServiceId} failed: {Error}. Retrying in {Delay}s",
          attempt,
          _serviceRegistration.ID,
          ex.Message,
          retryDelay.TotalSeconds);
      }

      try
      {
        await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }

      retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
    }
  }

  /// <summary>
  /// If the service is shutting down it deregisters service from Consul registry
  /// </summary>
  public override async Task StopAsync(CancellationToken cancellationToken)
  {
    await base.StopAsync(cancellationToken).ConfigureAwait(false);

    if (!_isRegistered)
    {
      return;
    }

    try
    {
      await client.Agent.ServiceDeregister(_serviceRegistration.ID, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Error while trying to deregister service {ServiceId}", _serviceRegistration.ID);
    }
  }
}
