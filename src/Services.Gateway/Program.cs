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
