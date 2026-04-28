using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kaijura.App.Models;

namespace Kaijura.App.Storage;

public sealed class LocalDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public LocalDataStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kaijura"))
    {
    }

    public LocalDataStore(string directory)
    {
        DirectoryPath = directory;
        StatePath = Path.Combine(directory, "state.json");
    }

    public string DirectoryPath { get; }
    public string StatePath { get; }

    public async Task<AppState> LoadAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);

        if (!File.Exists(StatePath))
        {
            return new AppState();
        }

        try
        {
            var json = await File.ReadAllTextAsync(StatePath, cancellationToken);
            return JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
        }
        catch (JsonException)
        {
            BackupCorruptState();
            return new AppState();
        }
    }

    public async Task SaveAsync(AppState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);

        var tempPath = StatePath + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, StatePath, overwrite: true);
    }

    private void BackupCorruptState()
    {
        if (!File.Exists(StatePath))
        {
            return;
        }

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(StatePath, Path.Combine(DirectoryPath, $"state.corrupt-{stamp}.json"), overwrite: true);
    }
}
