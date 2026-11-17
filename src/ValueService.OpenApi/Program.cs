using System.Reflection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Services.Core.ServiceDiscovery;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Logging
  .ClearProviders()
  .AddConsole()
  .AddDebug();

builder.Services.AddConsulServiceDiscovery();
builder.Services.AddHealthChecks();
builder.Services.AddControllers();
builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
  options.SwaggerDoc("v1", new OpenApiInfo
  {
    Version = "v1",
    Title = "ValueService.OpenAPI",
    Description = "An ASP.NET Core Web API for demonstrating Consul service discovery.",
    TermsOfService = new Uri("https://example.com/terms"),
    Contact = new OpenApiContact
    {
      Name = "Example Contact",
      Url = new Uri("https://example.com/contact")
    },
    License = new OpenApiLicense
    {
      Name = "Example License",
      Url = new Uri("https://example.com/license")
    }
  });

  string xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
  options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
});

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
  app.UseSwagger();
  app.UseSwaggerUI(options =>
  {
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
    options.RoutePrefix = string.Empty;
  });
}

ServiceConfig serviceConfig = app.Services.GetRequiredService<IOptions<ServiceConfig>>().Value;

app.MapControllers();
app.MapHealthChecks(serviceConfig.HealthCheckEndPoint);
app.MapGet("/", () => "ValueService.OpenApi");

await app.RunAsync();
