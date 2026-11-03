using RIoT2.Connector.HomeAssistant.Services;
using RIoT2.Connector.Sdk;

var builder = WebApplication.CreateBuilder(args);
builder.AddRIoT2Connector<HomeAssistantSink, HomeAssistantOptions>(HomeAssistantOptions.Component, HomeAssistantOptions.Read, HomeAssistantOptions.Configure);
builder.Services.AddSingleton<HomeAssistantConnection>();
builder.Services.AddSingleton<IHomeAssistantMqtt>(services => services.GetRequiredService<HomeAssistantConnection>());
builder.Services.AddSingleton<HomeAssistantEntityMap>();
builder.Services.AddSingleton<DiscoveryPublisher>();
builder.Services.AddSingleton<IConnectorTemplateObserver>(services => services.GetRequiredService<DiscoveryPublisher>());
builder.Services.AddSingleton<CommandBridge>();
builder.Services.AddHostedService<HomeAssistantBridge>();

var app = builder.Build();
app.MapRIoT2Connector();
app.Run();
