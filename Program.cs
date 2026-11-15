namespace KoVideo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        using var window = new PlayerWindow(args.FirstOrDefault()) { Icon = icon };
        Application.Run(window);
    }
}
