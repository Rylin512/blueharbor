using System.Text.Json;
using BlueHarbor.Models;

namespace BlueHarbor.Services;

public sealed class SettingsService
{
    private readonly string _path;
    private readonly Logger _log;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsService(string directory, Logger log)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _log = log;
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    return Sanitize(settings);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取设置失败，使用默认设置");
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "保存设置失败");
        }
    }

    private static AppSettings Sanitize(AppSettings settings)
    {
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 900, 2400);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 620, 1600);
        return settings;
    }
}
