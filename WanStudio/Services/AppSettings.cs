using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;

namespace WanStudio.Services;

/// <summary>
/// 设置写盘失败。<b>把原始 Access denied 翻成用户能看懂、能自己动手解决的话。</b>
/// 最常见的成因不是磁盘满，而是应用被放在受限目录里跑（例如被沙箱/杀软/只读目录拦住），
/// 所以提示里要直接给出「换到可写位置运行」这条出路。
/// </summary>
public sealed class SettingsWriteException : Exception
{
    public SettingsWriteException(string path, Exception inner)
        : base(BuildMessage(path, inner), inner)
    {
        SettingsPath = path;
    }

    public string SettingsPath { get; }

    private static string BuildMessage(string path, Exception inner)
    {
        var reason = inner switch
        {
            UnauthorizedAccessException => "系统拒绝写入（Access denied）",
            IOException io when io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                => "文件被另一个进程占用",
            IOException => "磁盘写入失败",
            _ => inner.GetType().Name
        };

        return $"设置保存失败：{reason}。\n" +
               $"目标文件：{path}\n" +
               "常见原因：应用所在的目录不允许写入。\n" +
               "可以这样处理：把应用整体复制到一个普通用户目录（例如桌面或文档）再运行，" +
               "不要放在受限制的目录里。\n" +
               $"原始错误：{inner.Message}";
    }
}

/// <summary>File-backed settings. ApplicationData.Current needs package identity and white-screens unpackaged.</summary>
public static class AppSettings
{
    public const string DefaultEndpoint = "https://cn-beijing.maas.qianwenaiapi.com";

    /// <summary>
    /// 一个 API 平台预设。两家的接入域名不同，模型清单也不完全重合，
    /// 所以给一个统一切换点，省得用户手抄 Endpoint。
    /// </summary>
    /// <param name="Name">界面上显示的名字。</param>
    /// <param name="Endpoint">只到主机名；客户端自己拼 /api/v1/...。
    /// 阿里云百炼的工作空间域名每个账号都不一样，所以这里是模板。</param>
    /// <param name="Note">这个平台上通常有哪些视频模型。</param>
    public sealed record ApiPlatform(string Name, string Endpoint, string Note)
    {
        /// <summary>地址里还留着 &lt;...&gt; 占位符，说明需要用户替换。</summary>
        public bool IsTemplate => Endpoint.Contains('<');
    }

    /// <summary>
    /// 百炼工作空间域名的模板。把 &lt;你的工作空间ID&gt; 换成自己的那段即可。
    /// 之所以不在源码里写死具体 ID：那是能定位到具体账号的信息，不该出现在仓库里。
    /// </summary>
    public const string BailianEndpointTemplate = "https://<你的工作空间ID>.cn-beijing.maas.aliyuncs.com";

    /// <summary>内置平台预设。顺序即界面顺序，第一个是默认。</summary>
    public static IReadOnlyList<ApiPlatform> Platforms => new List<ApiPlatform>
    {
        new("千问AI平台", "https://cn-beijing.maas.qianwenaiapi.com",
            "happyhorse 系列 / wan3.0-video / wan2.7-videoedit / wan2.7-r2v"),
        new("阿里云百炼", BailianEndpointTemplate,
            "wan3.0-video / wan2.7-i2v / wan2.7-r2v。工作空间域名每家不同，把 <你的工作空间ID> 换成自己的。")
    };

    /// <summary>
    /// 当前 Endpoint 落在哪个预设上；自定义地址返回 null。
    /// 百炼的工作空间域名每个账号都不一样，所以按域名后缀识别，不做全等比较。
    /// </summary>
    public static ApiPlatform? CurrentPlatform
    {
        get
        {
            var cur = Endpoint.TrimEnd('/');

            foreach (var pf in Platforms)
            {
                if (!pf.IsTemplate &&
                    string.Equals(pf.Endpoint.TrimEnd('/'), cur, StringComparison.OrdinalIgnoreCase))
                {
                    return pf;
                }
            }

            // 百炼：ws-xxxx.cn-beijing.maas.aliyuncs.com 这类，各账号前缀不同。
            if (cur.Contains("maas.aliyuncs.com", StringComparison.OrdinalIgnoreCase))
                return Platforms.FirstOrDefault(p => p.Name.Contains("百炼"));

            return null;
        }
    }

    /// <summary>地址还是模板（没填工作空间 ID）时为真，界面要给提示。</summary>
    public static bool EndpointNeedsWorkspaceId
        => Endpoint.Contains('<') || Endpoint.Contains("你的工作空间ID");

    
    public const string DefaultModel = "wan3.0-video-prime";
    public const string DefaultProfile = "default";

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    private static string SettingsPath =>
        Path.Combine(SettingsDir, "settings.json");

    /// <summary>
    /// 设置目录。默认 <c>%LOCALAPPDATA%\WanStudio</c>；
    /// 如果那里写不进去（受限目录、只读、被策略锁住），自动回退到用户目录下的
    /// <c>%USERPROFILE%\.wanstudio</c>，下次启动沿用（回退结果记在 <see cref="FallbackMarkerPath"/>）。
    /// 这样「设置存不下来」不会把整个应用卡死。
    /// </summary>
    private static string SettingsDir
    {
        get
        {
            if (_settingsDir is not null) return _settingsDir;
            lock (Gate)
            {
                if (_settingsDir is not null) return _settingsDir;

                var preferred = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WanStudio");

                if (IsWritable(preferred))
                {
                    _settingsDir = preferred;
                }
                else
                {
                    var fallback = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".wanstudio");
                    _settingsDir = fallback;
                }
                return _settingsDir;
            }
        }
    }

    private static string? _settingsDir;

    /// <summary>记录当前是否已经用到回退目录（供 UI 提示，不含敏感信息）。</summary>
    public static bool UsingFallbackSettingsDir
    {
        get
        {
            var preferred = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WanStudio");
            return !string.Equals(SettingsDir, preferred, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string CurrentSettingsDirectory => SettingsDir;

    /// <summary>探测目录可写：建目录并写一个探针文件再删掉。</summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;
        lock (Gate)
        {
            if (_cache is not null) return _cache;
            try
            {
                var path = SettingsPath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                             ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // ignore corrupt file
            }

            _cache ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return _cache;
        }
    }

    private static void Save()
    {
        lock (Gate)
        {
            var path = SettingsPath;
            var dir = Path.GetDirectoryName(path)!;
            var json = JsonSerializer.Serialize(Load(), new JsonSerializerOptions { WriteIndented = true });

            try
            {
                Directory.CreateDirectory(dir);
                // 先写临时文件再原子替换：写一半崩掉也不会留下损坏的 settings.json。
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new SettingsWriteException(path, ex);
            }
            catch (IOException ex)
            {
                throw new SettingsWriteException(path, ex);
            }
        }
    }

    private static string Get(string key, string fallback)
    {
        var map = Load();
        return map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
    }

    private static void Set(string key, string value)
    {
        lock (Gate)
        {
            var map = Load();
            var had = map.TryGetValue(key, out var previous);

            map[key] = value;
            try
            {
                Save();
            }
            catch
            {
                // 写盘失败必须把内存改回去，否则界面显示的是「已保存」而磁盘上还是旧值，
                // 下次启动就悄悄回退，排查起来极难。
                if (had) map[key] = previous!;
                else map.Remove(key);
                throw;
            }
        }
    }

    public static string Endpoint
    {
        get => Get(nameof(Endpoint), DefaultEndpoint);
        set => Set(nameof(Endpoint), value);
    }

    public static string Model
    {
        get => Get(nameof(Model), DefaultModel);
        set => Set(nameof(Model), value);
    }

    public static string KeyProfile
    {
        get => Get(nameof(KeyProfile), DefaultProfile);
        set => Set(nameof(KeyProfile), value);
    }

    public static string OutputDirectory
    {
        get => Get(
            nameof(OutputDirectory),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Wan3.0",
                "outputs"));
        set => Set(nameof(OutputDirectory), value);
    }
}
