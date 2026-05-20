using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Phipes.Assistant.DdnsWorker;
using Phipes.Assistant.DdnsWorker.Configuration;
using Phipes.Assistant.DdnsWorker.Services;

var builder = Host.CreateApplicationBuilder(args);

// Carga User Secrets explícitamente para que también estén disponibles cuando el
// servicio corre como Windows Service (donde DOTNET_ENVIRONMENT no es Development y
// el host no los carga automáticamente). El archivo secrets.json vive fuera del
// repositorio, en %APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json, por
// lo que nunca se versiona ni se publica. Sólo puede leerlo el usuario Windows que lo
// creó. Para producción, asegúrese de que el servicio corra como ese mismo usuario.
builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: true);

// Permite correr como Windows Service en producción
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Phipes.Assistant.DdnsWorker";
});

// Configuración
builder.Services.AddOptions<DdnsOptions>()
    .Bind(builder.Configuration.GetSection(DdnsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// HttpClient para UDM: ignora TLS self-signed (cert local del UDM)
builder.Services.AddHttpClient<UnifiUdmIpProvider>((sp, client) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DdnsOptions>>().Value.Unifi;
    client.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds);
})
.ConfigurePrimaryHttpMessageHandler(sp =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DdnsOptions>>().Value.Unifi;
    var handler = new HttpClientHandler();
    if (!opt.VerifyTls)
    {
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
    }
    return handler;
})
.AddStandardResilienceHandler();

// HttpClient para ipify
builder.Services.AddHttpClient<IpifyIpProvider>((sp, client) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DdnsOptions>>().Value.Ipify;
    client.BaseAddress = new Uri(opt.Endpoint);
    client.Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds);
})
.AddStandardResilienceHandler();

// HttpClient para Cloudflare
builder.Services.AddHttpClient<CloudflareDnsClient>(client =>
{
    client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
    client.Timeout = TimeSpan.FromSeconds(15);
})
.AddStandardResilienceHandler();

// Registro de IPublicIpProvider en orden: UDM primero, ipify fallback
builder.Services.AddTransient<IPublicIpProvider>(sp => sp.GetRequiredService<UnifiUdmIpProvider>());
builder.Services.AddTransient<IPublicIpProvider>(sp => sp.GetRequiredService<IpifyIpProvider>());

builder.Services.AddSingleton<PublicIpResolver>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
