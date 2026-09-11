using AwsSessionLauncher.Application;
using AwsSessionLauncher.Domain;
using AwsSessionLauncher.Domain.Enums;
using AwsSessionLauncher.Infrastructure;
using AwsSessionLauncher.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AwsSessionLauncher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddProvider(new FileLoggerProvider(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AwsSessionLauncher", "logs")));

        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IPortInspector, WindowsPortInspector>();
        builder.Services.AddSingleton<IFileSettingsRepository, FileSettingsRepository>();
        builder.Services.AddSingleton<IPrerequisiteDetector, PrerequisiteDetector>();
        builder.Services.AddSingleton<AwsConfigParser>();
        builder.Services.AddSingleton<IAwsCli, AwsCli>();
        builder.Services.AddSingleton<IAwsEnvironmentService, AwsEnvironmentService>();
        builder.Services.AddSingleton<IAwsProfileService, AwsProfileService>();
        builder.Services.AddSingleton<IAwsAuthenticationService, AwsAuthenticationService>();
        builder.Services.AddSingleton<IAwsInstanceDiscoveryService, AwsInstanceDiscoveryService>();
        builder.Services.AddSingleton<IPortValidationService, PortValidationService>();
        builder.Services.AddSingleton<ISsmSessionService, SsmSessionService>();
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddTransient<MainForm>();

        using var host = builder.Build();
        var form = host.Services.GetRequiredService<MainForm>();
        System.Windows.Forms.Application.Run(form);
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

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd}.log");
        var line = $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)}{Environment.NewLine}";
        try { File.AppendAllText(path, line); } catch { /* ignore IO races */ }
    }
}
