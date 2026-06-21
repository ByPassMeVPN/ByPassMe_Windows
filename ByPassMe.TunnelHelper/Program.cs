using ByPassMe.TunnelHelper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "--install-service":
            return ServiceInstaller.Install();
        case "--uninstall-service":
            return ServiceInstaller.Uninstall();
    }
}

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

Host.CreateDefaultBuilder(args)
    .UseWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName)
    .ConfigureServices(s => s.AddHostedService<TunnelWorker>())
    .Build()
    .Run();

return 0;
