namespace Dotnet.CLI.Plus.Tests;

public sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dotnet-cli-plus-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}
