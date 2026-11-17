![](res/Title.jpg)
# Building API Gateway with load balancer and service discovery
When designing solutions based on the micro service architecture, we often encounter the requirement for quick and easy management of the entire system, the highest possible automation, without the necessary adjustment of individual components.

This is a real challenge and that’s why I decided to prepare a tutorial that demonstrates how to establish a micro service architecture in the simplest possible way, which can be quickly and very easily scaled and adapted to client requirements.

I did not want to interfere with the code and settings of individual services, but to control the system only with orchestrating containers in Docker.

The result is a simple micro service architecture that can be easily scaled with just a few changes in container settings, everything else is provided by Ocelot as gateway/load balancer and Consul as service discovery agent.

Such an architecture allows us redeployment of a single service without coordinating the deployment within other services. Redeployed service is automatically registered at service discovery and imediatelly available through gateway. You can imagine how big a boost this is for every development team!

Sure, using a single gateway service becomes a single point of failure to our architecture, so we need to deploy at least two instances of it to have high availability. But I will leave that problem to you to play with.

In my [previous demo](https://github.com/matjazbravc/Ocelot.Gateway.Eureka.ServiceDiscovery.Demo) I showed how to implement [Ocelot](https://github.com/ThreeMammals/Ocelot) as service gateway and load balancer together with [Eureka](https://spring.io/projects/spring-cloud-netflix) for a service discovery. Instead Eureka this demo uses [Consul](https://www.consul.io/) for a service discovery.

## What is Consul?
Consul is a service mesh solution providing a full featured control plane with service discovery, configuration, and segmentation functionality. Each of these features can be used individually as needed, or they can be used together to build a full service mesh. Consul requires a data plane and supports both a proxy and native integration model. Consul ships with a simple built-in proxy so that everything works out of the box, but also supports 3rd party proxy integrations such as Envoy.

The key features of Consul are:

**Service Discovery**: Clients of Consul can register a service, such as api or mysql, and other clients can use Consul to discover providers of a given service. Using either DNS or HTTP, applications can easily find the services they depend upon.

**Health Checking**: Consul clients can provide any number of health checks, either associated with a given service ("is the webserver returning 200 OK"), or with the local node ("is memory utilization below 90%"). This information can be used by an operator to monitor cluster health, and it is used by the service discovery components to route traffic away from unhealthy hosts.

**KV Store**: Applications can make use of Consul's hierarchical key/value store for any number of purposes, including dynamic configuration, feature flagging, coordination, leader election, and more. The simple HTTP API makes it easy to use.

**Secure Service Communication**: Consul can generate and distribute TLS certificates for services to establish mutual TLS connections. Intentions can be used to define which services are allowed to communicate. Service segmentation can be easily managed with intentions that can be changed in real time instead of using complex network topologies and static firewall rules.

**Multi Datacenter**: Consul supports multiple datacenters out of the box. This means users of Consul do not have to worry about building additional layers of abstraction to grow to multiple regions.

Consul is designed to be friendly to both the DevOps community and application developers, making it perfect for modern, elastic infrastructures.

Source: [Consul introduction](https://www.consul.io/docs/intro)

## Service self-registration
A key part of the tutorial is the use of the Consul to dynamically discover service endpoints. Once a service is registered with Consul, it can be discovered using typical DNS or custom API.

Consul provides health-checks on these service instances. If one of the service instances or services itself is unhealthy or fails its health-check, the registry would then know about this and would avoid returning the service’s address. The work that the load-balancer would do is handled by the registry in this case.

Because we use **multiple instances of the same service**, Consul would randomly send traffic to different instances. It thus balances the load between instances of services.

Consul handles challenges of failure detection and load distribution across multiple instances of services without the necessity of deploying a centralized load balancer.

It automatically manages the registry, which is updated when any new instance of the service is registered and becomes available to receive traffic. This helps us to easily scale the services.

Before getting into implementation details how to implement self-registration to Consul, let’s look into how service discovery with self-registration really works.

In a first step a service instance registers itself to the service discovery service by providing its name, ID and address. After this gateway is able to get address of this service by querying the Consul service discovery by its name/ID.

**The key thing to note here** is that the service instances are registered with a **unique service ID** in order to disambiguate between instances of service which are running on the same Consul service agent. It is required that all services **have a unique ID per node**, so if names might conflict (our case) then unique IDs must be provided.

![](res/ServiceRegistration.jpg)

## Architecture
![](res/Architecture.jpg)

## Implementation

Let’s look at how we can implement self-registration in the .NET application. First, we need the configuration required for service discovery. It is bound from the **ServiceConfig** configuration section (in Docker it comes from environment variables passed through the **docker-compose.override.yml** file) and validated with data annotations when the service starts, so a misconfigured service fails fast instead of silently using defaults.

```csharp
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
```
The configuration, the Consul client and the registration service are added to the dependency injection container with a single extension method:

```csharp
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
```
In **Program.cs** of the service we call `builder.Services.AddConsulServiceDiscovery()` and expose the health endpoint Consul calls with `app.MapHealthChecks(...)`.

The code below is implemented as a **background service** that **registers** the service in **Consul**. If Consul is not reachable yet (for example when the service starts before the Consul agent is ready), registration is retried with exponential backoff. The registration is also checked periodically, so the service **registers again** if Consul loses it (for example after a restart of a Consul agent running in dev mode). If the service is shutting down, it is **automatically unregistered** from the Consul registry.

```csharp
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
```
Once we have registered our services in the service discovery service, we can start implementing the Gateway API.

## Creating API Gateway using Ocelot

**Ocelot** requires that you provide a configuration file that contains a list of **Routes** (configuration used to map upstream request) and a **GlobalConfiguration** (other configuration like QoS, Rate limiting, etc.).
In the ocelot.json file below, you can see how we forward HTTP requests. We have to specify which type of load balancer we will use, in our case this is a **“RoundRobin”** which loops through available services and sends requests to available services.
It is important to set the Consul as a service discovery service in **GlobalConfiguration** for **ServiceDiscoveryProvider**.
```json
{
  "Routes": [
    {
      "Servicename": "ValueService",
      "DownstreamPathTemplate": "/{everything}",
      "DownstreamScheme": "http",
      "UpstreamPathTemplate": "/{everything}",
      "UpstreamHttpMethod": [ "GET" ],
      "UseServiceDiscovery": true,
      "RouteIsCaseSensitive": false,
      "LoadBalancerOptions": {
        "Type": "RoundRobin"
      },
      "QoSOptions": {
        "ExceptionsAllowedBeforeBreaking": 3,
        "DurationOfBreak": 5000,
        "TimeoutValue": 2000
      }
    }
  ],
  "GlobalConfiguration": {
    "RequestIdKey": "OcelotRequestId",
    "UseServiceDiscovery": true,
    "ServiceDiscoveryProvider": {
      "Host": "consul",
      "Port": 8500,
      "Type": "PollConsul",
      "PollingInterval": 1000
    }
  }
}
```
Here are some necessary explanations for **ServiceDiscoveryProvider** settings in the **GlobalConfiguration** section: 
 
* Host - the host of Consul
* Port - the port of Consul
* Type	
  * Consul, means that Ocelot will get service information from consul per request
  * PollConsul, means that Ocelot will poll Consul for latest service information
* PollingInterval - tells Ocelot how often (in milliseconds) to call Consul for changes in the service registry

After we have defined our configuration we can start to implement API Gateway. Below we can see the implementation of Ocelot API Gateway service, that uses our **ocelot.json** configuration file and **Consul** as a service registry. The Consul and Polly integrations come from the **Ocelot.Discovery.Consul** and **Ocelot.QualityOfService.Polly** packages.

```csharp
using Ocelot.Cache.CacheManager;
using Ocelot.DependencyInjection;
using Ocelot.Discovery.Consul;
using Ocelot.Middleware;
using Ocelot.QualityOfService.Polly;
using Services.Gateway.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);

builder.Logging
  .ClearProviders()
  .AddConsole()
  .AddDebug();

builder.Services
  .AddOcelot(builder.Configuration)
  .AddConsul<MyConsulServiceBuilder>()
  .AddCacheManager(x => x.WithDictionaryHandle())
  .AddPolly();

WebApplication app = builder.Build();

await app.UseOcelot();
await app.RunAsync();
```
## Running in Docker

As mention before, we will containerize all services with Docker, including **Consul**, using **Lightweight GNU/Linux distributions** for containers.

**docker-compose.yml** file with setup for all the containers looks like this. The **healthcheck** on the Consul container lets the other services wait until Consul is ready:
```yaml
services:
   consul:
        image: hashicorp/consul
        container_name: consul
        command: consul agent -dev -log-level=warn -ui -client=0.0.0.0
        hostname: consul
        healthcheck:
            test: ["CMD", "consul", "members"]
            interval: 2s
            timeout: 3s
            retries: 30
        networks:
            - common_network

   valueservice1.openapi:
        image: valueservice.openapi:latest
        container_name: valueservice1.openapi
        restart: on-failure
        hostname: valueservice1.openapi
        build:
            context: .
            dockerfile: src/ValueService.OpenApi/Dockerfile
        networks:
            - common_network

   valueservice2.openapi:
        image: valueservice.openapi:latest
        container_name: valueservice2.openapi
        restart: on-failure
        hostname: valueservice2.openapi
        build:
            context: .
            dockerfile: src/ValueService.OpenApi/Dockerfile
        networks:
            - common_network

   valueservice3.openapi:
        image: valueservice.openapi:latest
        container_name: valueservice3.openapi
        restart: on-failure
        hostname: valueservice3.openapi
        build:
            context: .
            dockerfile: src/ValueService.OpenApi/Dockerfile
        networks:
            - common_network

   services.gateway:
        image: services.gateway:latest
        container_name: services.gateway
        restart: on-failure
        hostname: services.gateway
        build:
            context: .
            dockerfile: src/Services.Gateway/Dockerfile
        networks:
            - common_network

networks:
    common_network:
        driver: bridge
```
Note that our services doesn’t contain any confuguration files, for that purpose we are going to use **Docker-compose.override.yml** file:
```yaml
services:
    consul:
        ports:
             - "8500:8500"

    valueservice1.openapi:
        # Swagger UI: http://localhost:9100/index.html
        # http://localhost:9100/api/values
        environment:
          - ASPNETCORE_ENVIRONMENT=Development
          - ServiceConfig__ApiUrl=valueservice1.openapi
          - ServiceConfig__ConsulUrl=http://consul:8500
          - ServiceConfig__HealthCheckEndPoint=/health
          - ServiceConfig__Id=ValueService.OpenApi-9100
          - ServiceConfig__Name=ValueService
          - ServiceConfig__Port=8080
        ports:
            - 9100:8080
        depends_on:
            consul:
                condition: service_healthy

    valueservice2.openapi:
        # Swagger UI: http://localhost:9200/index.html
        # http://localhost:9200/api/values
        environment:
          - ASPNETCORE_ENVIRONMENT=Development
          - ServiceConfig__ApiUrl=valueservice2.openapi
          - ServiceConfig__ConsulUrl=http://consul:8500
          - ServiceConfig__HealthCheckEndPoint=/health
          - ServiceConfig__Id=ValueService.OpenApi-9200
          - ServiceConfig__Name=ValueService
          - ServiceConfig__Port=8080
        ports:
            - 9200:8080
        depends_on:
            consul:
                condition: service_healthy

    valueservice3.openapi:
        # Swagger UI: http://localhost:9300/index.html
        # http://localhost:9300/api/values
        environment:
          - ASPNETCORE_ENVIRONMENT=Development
          - ServiceConfig__ApiUrl=valueservice3.openapi
          - ServiceConfig__ConsulUrl=http://consul:8500
          - ServiceConfig__HealthCheckEndPoint=/health
          - ServiceConfig__Id=ValueService.OpenApi-9300
          - ServiceConfig__Name=ValueService
          - ServiceConfig__Port=8080
        ports:
            - 9300:8080
        depends_on:
            consul:
                condition: service_healthy

    services.gateway:
        # Call first available service: http://localhost:9500/api/values
        environment:
            - ASPNETCORE_ENVIRONMENT=Development
        ports:
            - 9500:8080
        depends_on:
            consul:
                condition: service_healthy
            valueservice1.openapi:
                condition: service_started
            valueservice2.openapi:
                condition: service_started
            valueservice3.openapi:
                condition: service_started
```
## Setup the Containers

To execute compose file, open Powershell, and navigate to the compose file in the root folder. Then execute the following command: **docker-compose up -d --build --remove-orphans** which starts and runs all services. The **-d** parameter executes the command detached. This means that the containers run in the background and don’t block your Powershell window. To check all running containers use command **docker ps**.

![](res/Docker.jpg)

## Consul Web UI

The Consul offers a nice web user interface right out of the box. You can access it on port **8500**: [http://localhost:8500](http://localhost:8500). Let’s look at some of the screens.

The home page for the Consul UI services with all the relevant information related to a Consul agent and web service check.
![](res/consul1.jpg)

![](res/consul2.jpg)

![](res/consul3.jpg)

![](res/consul4.jpg)

## Check it out
Let’s make several calls through API Gateway: [http://localhost:9500/api/values](http://localhost:9500/api/values). Load balancer will loop through available services and send requests and return responses:

![](res/Gateway1.jpg)

![](res/Gateway2.jpg)

![](res/Gateway3.jpg)

## Conclusion
Micro service systems are not easy to build and maintain. But this tutorial showed how easy it is to develop and deploy an application with micro service architecture. HashiCorp Consul has first-class support for service discovery, health-check, key-value storage and multi-data centers. Ocelot as a gateway successfully communicates with Consul service registry and retrieves service registrations, load balancer loops through available services and sends requests. Using both makes life significantly easier for developers facing such challenges. Do you agree?

Enjoy!

## Prerequisites
- [Visual Studio](https://www.visualstudio.com/vs/community) 2026 or greater
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker](https://www.docker.com/resources/what-container)

## Tags & Technologies
- [.NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0)
- [Consul](https://www.consul.io/)
- [Docker](https://www.docker.com/resources/what-container)  
- [Ocelot](https://github.com/ThreeMammals/Ocelot)  
- [Ocelot Consul Service Builder](https://ocelot.readthedocs.io/en/latest/features/servicediscovery.html#consul-service-builder)

## Useful VS Extensions
- [Clean Bin and Obj](https://marketplace.visualstudio.com/items?itemName=dobrynin.cleanbinandobj)
- [CodeMaid 2022](https://marketplace.visualstudio.com/items?itemName=SteveCadwallader.CodeMaidVS2022)
- [Document Health](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.DocumentHealth)
- [Editor Info](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.DocumentMargin)
- [File Explorer](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.WorkflowBrowser)
- [Font Sizer 2.0](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.FontSizer2)
- [Image Optimizer](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.ImageOptimizer64bit)
- [Markdown Editor v2](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.MarkdownEditor2)
- [Open in Notepad++](https://marketplace.visualstudio.com/items?itemName=coding-with-calvin.OpeninNotepad)
- [Rainbow Braces](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.RainbowBraces)
- [Reset Zoom](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.ResetZoom)
- [Shortcut Presenter](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.ShortcutPresenter)
- [SQL Formatter](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.SqlFormatter)

YouTube: [Visual Studio Extensions That Make You Happier](https://www.youtube.com/watch?v=bHy8kkoGNHk&t=1105s)
## Licence
Licenced under [MIT](http://opensource.org/licenses/mit-license.php).
Contact me on [LinkedIn](https://si.linkedin.com/in/matjazbravc).
