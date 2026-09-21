namespace AuthSurface.FixtureApp;

public static class Program
{
    public static void Main(string[] args)
    {
        using WebApplication app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        app.Run();
    }
}
