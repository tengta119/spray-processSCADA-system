using YR.FrameWork.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using SqlSugar;
using System.Reflection;
using Yale.BLL;
using Yale.DAL;

namespace Yale.SprayProcessSCADASystem;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Resolve configuration, SQLite and logs beside the executable for every launch method.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        ApplicationConfiguration.Initialize();
        var services = new ServiceCollection();
        ConfigureService(services);
        using var serviceProvider = services.BuildServiceProvider();
        Globals.ServiceProvider = serviceProvider;
        try
        {
            DatabaseInitializer.Initialize(serviceProvider.GetRequiredService<ISqlSugarClient>());
            Application.Run(serviceProvider.GetRequiredService<FrmMain>());
        }
        catch (Exception ex)
        {
            LogManager.GetCurrentClassLogger().Error(ex, "启动失败");
            MessageBox.Show("启动失败：" + ex.Message, "喷涂工艺SCADA系统", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    private static void ConfigureService(ServiceCollection services)
    {
        services.AddDependencyInjection(new List<Assembly>
        {
            typeof(Program).Assembly,
            typeof(BaseDto).Assembly,
            typeof(DB).Assembly
        });
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();
        services.AddSingleton(configuration)
            .AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                builder.AddNLog();
            });
        var dbType = Enum.Parse<DbType>(configuration["DbContexts:DbType"]!);
        services.AddSqlSugarSetup(dbType, configuration[$"DbContexts:{dbType}:ConnectionString"]!);
        LogManager.Configuration = new NLogLoggingConfiguration(configuration.GetSection("NLog"));
    }
}
