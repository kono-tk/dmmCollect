using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace dmmCollect.Gui
{
    public partial class MainWindow : Window
    {
        private Process? _process;
        private readonly string _settingsPath;
        private StreamWriter? _logFileWriter;
        private string? _logFilePath;

        public MainWindow()
        {
            InitializeComponent();

            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "dmmCollectGui", "settings.json");

            LoadSettings();
            Closing += (_, _) => { SaveSettings(); TryKillProcess(); CloseLogFile(); };
        }

        // ===== 実行 =====

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (_process != null && !_process.HasExited)
            {
                return;
            }

            string? exePath = ResolveExePath();
            if (exePath == null)
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "dmmCollect.exe を選択してください",
                    Filter = "dmmCollect.exe|dmmCollect.exe|実行ファイル (*.exe)|*.exe"
                };
                if (dlg.ShowDialog() != true) return;
                exePath = dlg.FileName;
                _savedExePath = exePath;
            }

            SaveSettings();

            var args = BuildArgs();
            OpenLogFile();
            AppendLog($"=== 実行: {Path.GetFileName(exePath)} {string.Join(' ', args)} ===");
            if (_logFilePath != null)
            {
                AppendLog($"=== ログファイル: {_logFilePath} ===");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,       // 親の環境変数(DMM_LOGIN_ID等)を継承する
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (_, ev) => { if (ev.Data != null) AppendLog(ev.Data); };
                _process.ErrorDataReceived += (_, ev) => { if (ev.Data != null) AppendLog(ev.Data); };
                _process.Exited += Process_Exited;

                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                SetRunningState(true);
            }
            catch (Exception ex)
            {
                AppendLog($"[GUI-ERROR] 起動に失敗しました: {ex.Message}");
                SetRunningState(false);
            }

            await System.Threading.Tasks.Task.CompletedTask;
        }

        private void Process_Exited(object? sender, EventArgs e)
        {
            int code = -1;
            try { code = _process?.ExitCode ?? -1; } catch { /* ignore */ }
            Dispatcher.BeginInvoke(() =>
            {
                AppendLog($"=== プロセス終了 (exit code: {code}) ===");
                SetRunningState(false);
            });
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("=== 停止を要求しました（プロセスツリーを終了します） ===");
            TryKillProcess();
        }

        private void TryKillProcess()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);   // Playwright が起動する Chromium も含めて終了
                }
            }
            catch { /* すでに終了 */ }
        }

        // ===== 引数組み立て =====

        private List<string> BuildArgs()
        {
            var args = new List<string>();

            string mode = ModeBooks.IsChecked == true ? "books"
                        : ModeDojin.IsChecked == true ? "dojin"
                        : ModeDlsite.IsChecked == true ? "dlsite"
                        : "video";
            args.Add("--mode");
            args.Add(mode);

            string kw = KeywordBox.Text.Trim();
            if (!string.IsNullOrEmpty(kw))
            {
                args.Add("-k");
                foreach (var token in kw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    args.Add(token);
                }
            }

            string pages = PagesBox.Text.Trim();
            if (!string.IsNullOrEmpty(pages))
            {
                args.Add("-p");
                args.Add(pages);
            }

            if (ChkHeadful.IsChecked == true) args.Add("--headful");
            if (ChkDebug.IsChecked == true) args.Add("--debug");
            if (ChkSyncOnly.IsChecked == true) args.Add("--sync-only");
            if (ChkRenewLnk.IsChecked == true) args.Add("--renew-lnk");
            if (ChkLostChild.IsChecked == true) args.Add("--lost-child");

            return args;
        }

        // ===== exe パス解決 =====

        private string? _savedExePath;

        private string? ResolveExePath()
        {
            if (!string.IsNullOrEmpty(_savedExePath) && File.Exists(_savedExePath))
            {
                return _savedExePath;
            }

            string guiDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(guiDir, "dmmCollect.exe"),
                // dmmCollect.Gui/bin/<cfg>/net10.0-windows/ から見た本体の既定出力先
                Path.GetFullPath(Path.Combine(guiDir, "..", "..", "..", "..", "bin", "Debug", "net10.0", "dmmCollect.exe")),
                Path.GetFullPath(Path.Combine(guiDir, "..", "..", "..", "..", "bin", "Release", "net10.0", "dmmCollect.exe")),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        // ===== ログ =====

        private const int LogMaxChars = 800_000;

        private void OpenLogFile()
        {
            CloseLogFile();
            try
            {
                string logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "dmmCollectGui", "logs");
                Directory.CreateDirectory(logsDir);

                _logFilePath = Path.Combine(logsDir, $"dmmCollect_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _logFileWriter = new StreamWriter(_logFilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                _logFileWriter = null;
                _logFilePath = null;
                Dispatcher.BeginInvoke(() => AppendLog($"[GUI-ERROR] ログファイルを開けませんでした: {ex.Message}"));
            }
        }

        private void CloseLogFile()
        {
            try { _logFileWriter?.Dispose(); } catch { /* ignore */ }
            _logFileWriter = null;
        }

        // dmmCollect側はダウンロード進捗行を Console.Write("\r...") で書いており、リダイレクト経由では
        // \r 自体が行区切りとして扱われるため、GUI側には「空行」「受信中: ...」がそのまま別々の行として届く
        // （本来は同じ行を上書きする想定のもの）。ここでは連続する「受信中: 」行だけを1行に畳み込み、
        // 直後に届く [COMPLETED]/[ERROR] 行でその1行を確定表示に置き換えて「上書き表示」を再現する。
        // 空行はこの畳み込みの対象にしない（別の\r系出力＝「進行状況: ...」等と混ざって、直前の
        // [COMPLETED]/[ERROR] 行を誤って消してしまうことがあるため）。
        private static bool IsProgressTickLine(string line) => line.Trim().StartsWith("受信中: ");

        private static bool IsDownloadResultLine(string line)
        {
            string t = line.Trim();
            return t.StartsWith("[COMPLETED] ダウンロード完了") || t.StartsWith("[ERROR] ダウンロード失敗");
        }

        private int _progressTickStart = -1;

        private void AppendLog(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => AppendLog(line));
                return;
            }

            try { _logFileWriter?.WriteLine(line); } catch { /* ignore */ }

            bool isTick = IsProgressTickLine(line);
            bool isResult = IsDownloadResultLine(line);

            if ((isTick || isResult) && _progressTickStart >= 0 && _progressTickStart <= LogBox.Text.Length)
            {
                // 直前が進捗tickの行だった場合のみ、その行を今回の内容で置き換える
                LogBox.Text = LogBox.Text.Substring(0, _progressTickStart);
            }

            if (LogBox.Text.Length > LogMaxChars)
            {
                LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - LogMaxChars / 2);
                _progressTickStart = -1;
            }

            if (isTick)
            {
                if (_progressTickStart < 0 || _progressTickStart > LogBox.Text.Length)
                {
                    _progressTickStart = LogBox.Text.Length;
                }
            }
            else
            {
                // [COMPLETED]/[ERROR] で確定表示にした後は、以降の行（空行や次のダウンロード開始行）が
                // 誤ってこの行を上書きしないよう畳み込みを終了する
                _progressTickStart = -1;
            }

            LogBox.AppendText(line + Environment.NewLine);
            if (ChkAutoScroll.IsChecked == true)
            {
                LogBox.ScrollToEnd();
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Clear();
        }

        private void SetRunningState(bool running)
        {
            RunButton.IsEnabled = !running;
            StopButton.IsEnabled = running;
            StatusText.Text = running ? "実行中..." : "待機中";
        }

        // ===== 設定の保存/復元 =====

        private class Settings
        {
            public string Mode { get; set; } = "video";
            public string Keywords { get; set; } = "";
            public string Pages { get; set; } = "1";
            public bool Headful { get; set; }
            public bool Debug { get; set; }
            public bool SyncOnly { get; set; }
            public bool RenewLnk { get; set; }
            public bool LostChild { get; set; }
            public bool AutoScroll { get; set; } = true;
            public string? ExePath { get; set; }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
                if (s == null) return;

                ModeVideo.IsChecked = s.Mode == "video";
                ModeBooks.IsChecked = s.Mode == "books";
                ModeDojin.IsChecked = s.Mode == "dojin";
                ModeDlsite.IsChecked = s.Mode == "dlsite";
                if (s.Mode is not ("video" or "books" or "dojin" or "dlsite")) ModeVideo.IsChecked = true;

                KeywordBox.Text = s.Keywords;
                PagesBox.Text = s.Pages;
                ChkHeadful.IsChecked = s.Headful;
                ChkDebug.IsChecked = s.Debug;
                ChkSyncOnly.IsChecked = s.SyncOnly;
                ChkRenewLnk.IsChecked = s.RenewLnk;
                ChkLostChild.IsChecked = s.LostChild;
                ChkAutoScroll.IsChecked = s.AutoScroll;
                _savedExePath = s.ExePath;
            }
            catch { /* 破損時は既定値のまま */ }
        }

        private void SaveSettings()
        {
            try
            {
                string mode = ModeBooks.IsChecked == true ? "books"
                            : ModeDojin.IsChecked == true ? "dojin"
                            : ModeDlsite.IsChecked == true ? "dlsite"
                            : "video";

                var s = new Settings
                {
                    Mode = mode,
                    Keywords = KeywordBox.Text,
                    Pages = PagesBox.Text,
                    Headful = ChkHeadful.IsChecked == true,
                    Debug = ChkDebug.IsChecked == true,
                    SyncOnly = ChkSyncOnly.IsChecked == true,
                    RenewLnk = ChkRenewLnk.IsChecked == true,
                    LostChild = ChkLostChild.IsChecked == true,
                    AutoScroll = ChkAutoScroll.IsChecked == true,
                    ExePath = _savedExePath,
                };

                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 保存失敗は無視 */ }
        }
    }
}
