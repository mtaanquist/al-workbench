namespace ALDevToolbox.Tests.Migration;

/// <summary>A folder under the system temp folder, removed again on dispose.</summary>
internal sealed class MigrationTempDir : IDisposable
{
    public MigrationTempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mig-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A sub-folder, created.</summary>
    public string Sub(string relative)
    {
        var dir = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Writes the files a migration test reads.</summary>
internal static class MigrationTestFiles
{
    /// <summary>Writes an app.json into <paramref name="dir"/> (creating it), with optional dependencies as (id, name, publisher).</summary>
    public static void WriteApp(string dir, string id, string name, string publisher = "CRONUS",
        string version = "1.0.0.0", params (string Id, string Name, string Publisher)[] dependencies)
    {
        Directory.CreateDirectory(dir);
        var deps = string.Join(",", dependencies.Select(d =>
            $$"""{ "id": "{{d.Id}}", "name": "{{d.Name}}", "publisher": "{{d.Publisher}}", "version": "1.0.0.0" }"""));
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            {
              // comments and trailing commas are allowed in app.json
              "id": "{{id}}",
              "name": "{{name}}",
              "publisher": "{{publisher}}",
              "version": "{{version}}",
              "dependencies": [ {{deps}} ],
            }
            """);
    }

    public static void WriteFile(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
