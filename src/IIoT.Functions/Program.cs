using IIoT.Core.Forwarders;
using IIoT.Core.Interfaces;
using IIoT.Core.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddHttpClient<GatewayV1Forwarder>();
builder.Services.AddSingleton<IDataForwarder, GatewayV1Forwarder>();
builder.Services.AddSingleton<SensorDataService>();

builder.Build().Run();
