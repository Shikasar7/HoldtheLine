namespace HoldTheLine.Rules.Tests;

/// <summary>Locates the repository root from the test output directory (looks for the .sln).</summary>
public static class RepoPaths
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.EnumerateFiles("*.sln").Any())
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repository root (containing a .sln) not found above " + AppContext.BaseDirectory);
    }
}
