using Microsoft.Extensions.Hosting;

// .NET 8 isolated worker host for the "Claude" Alexa custom skill.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .Build();

host.Run();
