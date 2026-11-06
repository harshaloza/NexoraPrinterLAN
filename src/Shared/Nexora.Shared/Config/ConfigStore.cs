using System.Text.Json;
using Nexora.Shared.IO;

namespace Nexora.Shared.Config;

/// <summary>Loads and atomically saves a JSON config file. A corrupt file is preserved as *.bad and defaults are used.</summary>
public sealed class ConfigStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly object _gate = new();

    public ConfigStore(string path) => _path = path;

    public string Path => _path;

    public T Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                var fresh = new T();
                Save(fresh);
                return fresh;
            }
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Json.Indented) ?? new T();
            }
            catch (JsonException)
            {
                File.Copy(_path, _path + ".bad", overwrite: true);
                var fresh = new T();
                Save(fresh);
                return fresh;
            }
        }
    }

    public void Save(T config)
    {
        lock (_gate)
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(config, Json.Indented));
    }
}
