using AwsSsmPortForwarder.App;
using AwsSsmPortForwarder.Core.Services;
using AwsSsmPortForwarder.Infrastructure.AwsCli;
using AwsSsmPortForwarder.Infrastructure.Configuration;
using AwsSsmPortForwarder.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AwsSsmPortForwarder;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AwsSsmPortForwarder", "logs")));

        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<ILocalPortChecker, LocalPortChecker>();
        builder.Services.AddSingleton<IUserSettingsStore, FileUserSettingsStore>();
        builder.Services.AddSingleton<IAppConfigStore, FileAppConfigStore>();
        builder.Services.AddSingleton<IAwsCliClient, AwsCliClient>();
        builder.Services.AddSingleton<TargetResolver>();
        builder.Services.AddSingleton<ConnectionOrchestrator>();
        builder.Services.AddTransient<MainForm>();

        using var host = builder.Build();
        // Ensure default appsettings exists beside the EXE
        _ = host.Services.GetRequiredService<IAppConfigStore>().Load();
        System.Windows.Forms.Application.Run(host.Services.GetRequiredService<MainForm>());
    }
}

internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(directory, categoryName);
    public void Dispose() { }
}

internal sealed class FileLogger(string directory, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd}.log");
        try { File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)}{Environment.NewLine}"); }
        catch { }
    }
}
