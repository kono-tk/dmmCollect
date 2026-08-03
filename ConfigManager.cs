using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;

namespace dmmCollect
{
    public class ModeConfig
    {
        public string base_directory { get; set; } = ".";
        public string image_directory { get; set; } = "images";
        public string? library_url { get; set; }
        public string? detail_page_base { get; set; }
        public string? login_url { get; set; }
    }

    public class VideoConfig
    {
        public string? download_directory { get; set; }
    }

    public class ViewerConfig
    {
        public string font_family { get; set; } = "Yu Gothic UI";
        public int font_size { get; set; } = 14;
        public string? window_geometry_pyside { get; set; }
    }

    public class SearchConfig
    {
        public List<string> target_roots { get; set; } = new();
        public int max_alt_length { get; set; } = 100;
        public int max_shortcut_filename_length { get; set; } = 100;
    }

    public class AudioTargetConfig
    {
        public string key { get; set; } = "";
        public string display { get; set; } = "";
        public string? process_name { get; set; }
        public string? executable_path { get; set; }
        public string? aumid { get; set; }
    }

    public class LoginUrlsConfig
    {
        public string? age_check { get; set; }
        public string? top { get; set; }
        public string? login_page { get; set; }
        public string? login_pattern { get; set; }
        public string? my_library_search { get; set; }
        public string? detail_page_base { get; set; }
    }

    public class LoginConfig
    {
        public LoginUrlsConfig urls { get; set; } = new();
    }

    public class DmmConfig
    {
        public string active_audio_target_key { get; set; } = "dmm_game_player";
        public LoginConfig login { get; set; } = new();
        public ModeConfig common { get; set; } = new();
        public VideoConfig video { get; set; } = new();
        public ViewerConfig viewer { get; set; } = new();
        public SearchConfig search { get; set; } = new();
        public List<AudioTargetConfig> app_audio_targets { get; set; } = new();
        public ModeConfig books { get; set; } = new();
        public ModeConfig dojin { get; set; } = new();
        public ModeConfig dlsite { get; set; } = new();
    }

    public class ConfigManager
    {
        public string ConfigPath { get; private set; }
        public string Mode { get; private set; }
        public DmmConfig Config { get; private set; }

        public string BaseDirectory { get; private set; } = ".";
        public string DataPath { get; private set; } = "";
        public string ImageDirectory { get; private set; } = "images";

        // 出演者エイリアスの共有ファイル。dmmConfig.json と同じディレクトリに、モード別で配置する。
        // 例: <config dir>/performer_aliases_video.json
        public string PerformerAliasesPath =>
            Path.Combine(Path.GetDirectoryName(ConfigPath) ?? ".", $"performer_aliases_{Mode}.json");

        public List<string> TargetRoots { get; private set; } = new();
        public int MaxAltLength { get; private set; } = 100;
        public int MaxShortcutFilenameLength { get; private set; } = 100;
        public string VideoDownloadDirectory { get; private set; } = "";

        public ConfigManager(string mode)
        {
            Mode = mode.ToLower();
            ConfigPath = FindConfigPath();
            Config = LoadOrCreateDefault();
            ParseConfig();
        }

        private string FindConfigPath()
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var possiblePaths = new List<string>
            {
                Path.Combine(userHome, ".dmmconfig.json"),
                Path.Combine(userHome, "dmmConfig.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppConstants.CONFIG_FILENAME),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../mydmmapp", AppConstants.CONFIG_FILENAME)),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../mydmmapp", AppConstants.CONFIG_FILENAME)),
                Path.Combine(@"C:\Users\mocha\work\mydmmapp", AppConstants.CONFIG_FILENAME)
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    Console.WriteLine($"ConfigManager: Config file found at: {path}");
                    return path;
                }
            }

            Console.WriteLine($"ConfigManager: Config file not found. Defaulting to user home directory.");
            return possiblePaths[0]; // fallback
        }

        private DmmConfig LoadOrCreateDefault()
        {
            if (!File.Exists(ConfigPath))
            {
                Console.WriteLine($"Config file not found. Creating default: {ConfigPath}");
                var defaultConfig = GetDefaultConfig();
                SaveConfig(defaultConfig);
                return defaultConfig;
            }

            try
            {
                string jsonString = File.ReadAllText(ConfigPath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<DmmConfig>(jsonString, options);
                return parsed ?? GetDefaultConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}. Using default.");
                return GetDefaultConfig();
            }
        }

        private DmmConfig GetDefaultConfig()
        {
            return new DmmConfig
            {
                common = new ModeConfig { base_directory = ".", image_directory = "images" },
                books = new ModeConfig { base_directory = ".", image_directory = "images" },
                viewer = new ViewerConfig { font_family = "Yu Gothic UI", font_size = 14 }
            };
        }

        private void ParseConfig()
        {
            ModeConfig modeConfig;
            if (Mode == "books")
            {
                modeConfig = Config.books;
            }
            else if (Mode == "dojin")
            {
                modeConfig = Config.dojin;
            }
            else if (Mode == "dlsite")
            {
                modeConfig = Config.dlsite;
            }
            else // video
            {
                modeConfig = Config.common;
            }

            BaseDirectory = Path.GetFullPath(modeConfig.base_directory ?? ".");
            DataPath = Path.Combine(BaseDirectory, AppConstants.DATA_JSON_FILENAME);

            string imageDirStr = modeConfig.image_directory ?? "images";
            if (Path.IsPathRooted(imageDirStr))
            {
                ImageDirectory = imageDirStr;
            }
            else
            {
                ImageDirectory = Path.GetFullPath(Path.Combine(BaseDirectory, imageDirStr));
            }

            TargetRoots.Clear();
            foreach (var root in Config.search.target_roots)
            {
                if (Directory.Exists(root))
                {
                    TargetRoots.Add(Path.GetFullPath(root));
                }
                else
                {
                    Console.WriteLine($"ConfigWarning: target_root directory does not exist: {root}");
                }
            }
            MaxAltLength = Config.search.max_alt_length != 0 ? Config.search.max_alt_length : 100;
            MaxShortcutFilenameLength = Config.search.max_shortcut_filename_length != 0 ? Config.search.max_shortcut_filename_length : 100;

            string? downloadDir = Config.video?.download_directory;
            if (string.IsNullOrEmpty(downloadDir))
            {
                if (Config.search.target_roots != null && Config.search.target_roots.Count > 0)
                {
                    downloadDir = Config.search.target_roots[Config.search.target_roots.Count - 1];
                }
            }
            VideoDownloadDirectory = string.IsNullOrEmpty(downloadDir) ? "" : Path.GetFullPath(downloadDir);
        }

        public void SaveConfig(DmmConfig config)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                string jsonString = JsonSerializer.Serialize(config, options);
                File.WriteAllText(ConfigPath, jsonString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }
}
