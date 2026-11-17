using Consul;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Services.Core.ServiceDiscovery;

/// <summary>
/// Keeps the service registered in the Consul registry and deregisters it on shutdown.
/// Registration is retried with exponential backoff until Consul becomes reachable, and the
/// registration is checked periodically so the service registers again when Consul loses it
/// (for example after a restart of a Consul agent running in dev mode).
/// </summary>
public sealed class ServiceDiscoveryHostedService(
  ILogger<ServiceDiscoveryHostedService> logger,
  IConsulClient client,
  IOptions<ServiceConfig> options)
  : BackgroundService
{
  private static readonly TimeSpan _initialRetryDelay = TimeSpan.FromSeconds(1);
  private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan _registrationCheckInterval = TimeSpan.FromSeconds(30);

  private readonly AgentServiceRegistration _serviceRegistration = CreateRegistration(options.Value);

  private volatile bool _isRegistered;

  /// <summary>
  /// Registers the service to the Consul registry and re-registers it whenever the registration is lost.
  /// </summary>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    TimeSpan retryDelay = _initialRetryDelay;
    int failedAttempts = 0;

    while (!stoppingToken.IsCancellationRequested)
    {
      TimeSpan nextCheck;

      try
      {
        await EnsureRegisteredAsync(failedAttempts + 1, stoppingToken).ConfigureAwait(false);

        failedAttempts = 0;
        retryDelay = _initialRetryDelay;
        nextCheck = _registrationCheckInterval;
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        failedAttempts++;

        logger.LogWarning(
          "Consul registration attempt {Attempt} for service {ServiceId} failed: {Error}. Retrying in {Delay}s",
          failedAttempts,
          _serviceRegistration.ID,
          ex.Message,
          retryDelay.TotalSeconds);

        nextCheck = retryDelay;
        retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, _maxRetryDelay.Ticks));
      }

      try
      {
        await Task.Delay(nextCheck, stoppingToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }
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

  private async Task EnsureRegisteredAsync(int attempt, CancellationToken cancellationToken)
  {
    if (_isRegistered)
    {
      QueryResult<Dictionary<string, AgentService>> services =
        await client.Agent.Services(cancellationToken).ConfigureAwait(false);

      if (services.Response.ContainsKey(_serviceRegistration.ID))
      {
        return;
      }

      logger.LogWarning(
        "Service {ServiceId} is no longer registered in Consul, registering it again",
        _serviceRegistration.ID);
    }

    // ServiceRegister replaces an existing registration with the same ID, so no deregistration is needed first.
    await client.Agent.ServiceRegister(_serviceRegistration, cancellationToken).ConfigureAwait(false);
    _isRegistered = true;

    logger.LogInformation(
      "Service {ServiceId} registered in Consul after {Attempt} attempt(s)",
      _serviceRegistration.ID,
      attempt);
  }

  private static AgentServiceRegistration CreateRegistration(ServiceConfig config) => new()
  {
    ID = config.Id,
    Name = config.Name,
    Address = config.ApiUrl,
    Port = config.Port,
    Check = new AgentServiceCheck
    {
      // Consul enforces a minimum of one minute for automatic deregistration of critical services.
      DeregisterCriticalServiceAfter = TimeSpan.FromMinutes(1),
      Interval = TimeSpan.FromSeconds(15),
      HTTP = new UriBuilder(Uri.UriSchemeHttp, config.ApiUrl, config.Port, config.HealthCheckEndPoint).Uri.ToString(),
      Timeout = TimeSpan.FromSeconds(5)
    }
  };
}
