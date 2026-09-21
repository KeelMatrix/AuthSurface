namespace AuthSurface.FixtureApp;

public static class Program
{
    /// <summary>Starts the fixture application.</summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        using WebApplication app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        app.Run();
    }
}
