namespace paprika;

public static class AppPaths
{
    public static string DataRoot(IConfiguration configuration) =>
        configuration["paprika:DataDirectory"]
        ?? configuration["DataDirectory"]
        ?? DefaultDataRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        );

    public static string DefaultDataRoot(string localApplicationData)
    {
        var current = Path.Combine(localApplicationData, "paprika");
        var legacy = Path.Combine(localApplicationData, "PaperworkClassifier");
        // Reuse existing records in place; never move or merge an installation's data implicitly.
        return
            !File.Exists(Path.Combine(current, "jobs.sqlite"))
            && File.Exists(Path.Combine(legacy, "jobs.sqlite"))
            ? legacy
            : current;
    }
}
