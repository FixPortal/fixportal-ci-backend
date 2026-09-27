using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FixPortal.Ci.Backend.Api.Tests.Hosting;

/// <summary>
/// WebApplicationFactory that still throws the startup <see cref="OptionsValidationException"/>
/// when the entry-point thread loses the race and disposes the host first.
/// </summary>
/// <remarks>
/// <see cref="DeferredHostBuilder"/> waits for the app entry point by resolving
/// <c>IHostApplicationLifetime</c> from the built host. ValidateOnStart runs on the
/// entry-point thread, logs the failure, and disposes that provider before the test
/// thread reaches the resolve. The resolve then throws <see cref="ObjectDisposedException"/>
/// and the validation exception only remains on the host log. CI hit this on
/// <c>A_blank_required_GitHub_setting_is_rejected_at_startup</c> (run 36309060507).
/// </remarks>
public class CiWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var capture = new StartupFailureCapture();
        _ = builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(capture));

        var host = builder.Build();
        OnHostBuilt(host, capture);
        StartHost(host, capture);
        return host;
    }

    /// <summary>
    /// Runs after the host exists and before <see cref="IHost.Start"/>. Tests override this
    /// to wait until a startup failure has already disposed the provider.
    /// </summary>
    protected virtual void OnHostBuilt(IHost host, StartupFailureCapture capture) { }

    private static void StartHost(IHost host, StartupFailureCapture capture)
    {
        try
        {
            host.Start();
        }
        catch (ObjectDisposedException) when (capture.Failure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    protected sealed class StartupFailureCapture : ILoggerProvider, ILogger
    {
        private OptionsValidationException? _failure;

        public OptionsValidationException? Failure => Volatile.Read(ref _failure);

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose() { }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (exception is OptionsValidationException validation)
            {
                Volatile.Write(ref _failure, validation);
            }
        }
    }
}
