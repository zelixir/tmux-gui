using System.Text.Json;
using System.Text.Json.Serialization;

namespace TmuxGui.Host.Services;

public sealed class ServerConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("authType")] public string AuthType { get; set; } = "key";
    [JsonPropertyName("keyPath")] public string? KeyPath { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
}

public sealed class FavoriteConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "local";
}

public sealed class AppConfig
{
    [JsonPropertyName("servers")] public List<ServerConfig> Servers { get; set; } = [];
    [JsonPropertyName("favorites")] public List<FavoriteConfig> Favorites { get; set; } = [];
}

/// <summary>
/// %APPDATA%\tmux-gui\config.json 独占读写：线程安全（锁）+ 原子写（先写 tmp 再 move）。
/// </summary>
public sealed class ConfigStore
{
    public static ConfigStore Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private readonly string _filePath;
    private AppConfig _config = new();

    private ConfigStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tmux-gui");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "config.json");
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                    _config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_filePath), JsonOpts) ?? new AppConfig();
            }
            catch
            {
                // 配置损坏时从空配置开始，避免启动崩溃
                _config = new AppConfig();
            }
        }
    }

    private void SaveCore()
    {
        var tmpPath = _filePath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(_config, JsonOpts));
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    public List<ServerConfig> GetServers()
    {
        lock (_lock) return _config.Servers.Select(s => Clone(s)).ToList();
    }

    public ServerConfig? GetServer(string id)
    {
        lock (_lock) return _config.Servers.FirstOrDefault(s => s.Id == id) is { } s ? Clone(s) : null;
    }

    /// <summary>有 id 更新、无 id 新增，返回最终 id。</summary>
    public string UpsertServer(ServerConfig server)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(server.Id))
                server.Id = Guid.NewGuid().ToString("N");
            var idx = _config.Servers.FindIndex(s => s.Id == server.Id);
            if (idx >= 0) _config.Servers[idx] = server;
            else _config.Servers.Add(server);
            SaveCore();
            return server.Id;
        }
    }

    public bool DeleteServer(string id)
    {
        lock (_lock)
        {
            var removed = _config.Servers.RemoveAll(s => s.Id == id) > 0;
            if (removed) SaveCore();
            return removed;
        }
    }

    public void SetServerError(string id, string? error)
    {
        lock (_lock)
        {
            var s = _config.Servers.FirstOrDefault(x => x.Id == id);
            if (s is null) return;
            s.LastError = error;
            SaveCore();
        }
    }

    public List<FavoriteConfig> GetFavorites()
    {
        lock (_lock) return _config.Favorites.Select(Clone).ToList();
    }

    public string UpsertFavorite(FavoriteConfig favorite)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(favorite.Id))
                favorite.Id = Guid.NewGuid().ToString("N");
            var idx = _config.Favorites.FindIndex(f => f.Id == favorite.Id);
            if (idx >= 0) _config.Favorites[idx] = favorite;
            else _config.Favorites.Add(favorite);
            SaveCore();
            return favorite.Id;
        }
    }

    public bool DeleteFavorite(string id)
    {
        lock (_lock)
        {
            var removed = _config.Favorites.RemoveAll(f => f.Id == id) > 0;
            if (removed) SaveCore();
            return removed;
        }
    }

    private static ServerConfig Clone(ServerConfig s) => new()
    {
        Id = s.Id, Name = s.Name, Host = s.Host, Port = s.Port,
        Username = s.Username, AuthType = s.AuthType, KeyPath = s.KeyPath, LastError = s.LastError,
    };

    private static FavoriteConfig Clone(FavoriteConfig f) => new()
    {
        Id = f.Id, Name = f.Name, Command = f.Command, Scope = f.Scope,
    };
}
