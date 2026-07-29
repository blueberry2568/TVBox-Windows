using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TVBoxForWindows;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureStructuredNativeDependencySearch();
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(initializationCallbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = initializationCallbackParams;
            new App();
        });
    }

    static void ConfigureStructuredNativeDependencySearch()
    {
        var executablePath = Environment.ProcessPath;
        var executableDirectory = string.IsNullOrWhiteSpace(executablePath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
            throw new DirectoryNotFoundException("Could not resolve the application directory.");

        var nativeDirectories = new[]
        {
            Path.GetFullPath(Path.Combine(executableDirectory, "libs")),
            Path.GetFullPath(Path.Combine(executableDirectory, "locales", "winui")),
        };

        foreach (var nativeDirectory in nativeDirectories.Where(Directory.Exists))
            PrependProcessPath(nativeDirectory);
    }

    static void PrependProcessPath(string directory)
    {
        var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;
        var normalizedDirectory = NormalizePath(directory);
        var alreadyPresent = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePath)
            .Any(entry => string.Equals(entry, normalizedDirectory, StringComparison.OrdinalIgnoreCase));

        if (alreadyPresent) return;

        var updatedPath = string.IsNullOrWhiteSpace(currentPath)
            ? directory
            : directory + Path.PathSeparator + currentPath;
        Environment.SetEnvironmentVariable("PATH", updatedPath, EnvironmentVariableTarget.Process);
    }

    static string NormalizePath(string path)
    {
        var value = path.Trim().Trim('"');
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception) when (value.Length > 0)
        {
            return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
