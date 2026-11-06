using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class Program
{
    public static void Main()
    {
        new HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .ConfigureServices(services =>
            {
                services.AddHttpClient();
                services.AddSingleton(provider => CapivaradaOptions.From(provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
                services.AddSingleton<PlayFabAdminClient>();
                services.AddSingleton<PlayFabServerClient>();
                services.AddSingleton<PlayFabEntityClient>();
                services.AddSingleton<PlayFabTitleEntityClient>();
                services.AddSingleton<PlayerSessionService>();
                services.AddSingleton<NicknameIndexService>();
                services.AddSingleton<PageTemplateService>();
            })
            .Build()
            .Run();
    }
}
