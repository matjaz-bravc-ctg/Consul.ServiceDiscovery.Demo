using System.ComponentModel.DataAnnotations;

namespace Services.Core.ServiceDiscovery;

/// <summary>
/// Settings used to register this service instance in Consul.
/// Bound from the <see cref="SectionName"/> configuration section and validated at startup.
/// </summary>
public sealed record ServiceConfig
{
  /// <summary>
  /// Name of the configuration section the settings are bound from.
  /// </summary>
  public const string SectionName = "ServiceConfig";

  /// <summary>
  /// Unique ID of this service instance in Consul.
  /// </summary>
  [Required]
  public string Id { get; init; } = string.Empty;

  /// <summary>
  /// Logical service name; instances with the same name are load balanced by the gateway.
  /// </summary>
  [Required]
  public string Name { get; init; } = string.Empty;

  /// <summary>
  /// Host name or IP address under which Consul and the gateway reach this instance.
  /// </summary>
  [Required]
  public string ApiUrl { get; init; } = string.Empty;

  /// <summary>
  /// Port under which Consul and the gateway reach this instance.
  /// </summary>
  [Range(1, 65535)]
  public int Port { get; init; }

  /// <summary>
  /// Address of the Consul agent HTTP API.
  /// </summary>
  [Required]
  public Uri ConsulUrl { get; init; } = null!;

  /// <summary>
  /// Path of the health endpoint Consul calls to check this instance.
  /// </summary>
  [Required]
  public string HealthCheckEndPoint { get; init; } = "/health";
}
