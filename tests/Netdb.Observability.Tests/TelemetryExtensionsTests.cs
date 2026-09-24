using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netdb.Observability;
using Serilog.Extensions.Hosting;

namespace Netdb.Observability.Tests;

public class TelemetryExtensionsTests
{
    // UseSerilogRequestLogging fails at startup without it.
    [Fact]
    public void RegistersDiagnosticContext()
    {
        Environment.SetEnvironmentVariable("OTEL_SDK_DISABLED", "true");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.AddNetdbTelemetry();

            using var provider = builder.Services.BuildServiceProvider();
            Assert.NotNull(provider.GetService<DiagnosticContext>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_SDK_DISABLED", null);
        }
    }
}
