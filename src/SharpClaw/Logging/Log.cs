using Microsoft.Extensions.Logging;

namespace SharpClaw.Logging;

/// <summary>
/// Application-wide logger factory. Initialized once in Program.Main,
/// then used by components that don't participate in DI.
/// </summary>
public static class Log
{
    private static ILoggerFactory _factory = LoggerFactory.Create(b => { });

    public static void Init(ILoggerFactory factory) => _factory = factory;

    public static ILogger<T> For<T>() => _factory.CreateLogger<T>();
    public static ILogger ForName(string name) => _factory.CreateLogger(name);
}
