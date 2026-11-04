using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using dmmCollect.Scrapers;

namespace dmmCollect
{
    /// <summary>
    /// dojin のゲーム作品（format が「ゲーム」）をダウンロードして BaseDirectory\games\&lt;作品名_cid&gt;\ に展開する。
    /// dojin の他の形式は作品ごとに提供形態がまちまちなのでメタデータだけを集めるが、ゲームは {cid}.zip が1つなので落とせる。
    ///
    /// ・展開できた作品は data.json に game_folder（games 配下のフォルダ名）と game_downloaded_at を記録し、
    ///   フォルダがある限り次からは取りに行かない。dmmWin はタイトルのクリックでこのフォルダを開く。
    /// ・zip は一時フォルダに展開してから所定の名前に移すので、途中で止まっても中途半端なフォルダは残らない。
    ///   展開できた zip は消す（DMM のライブラリからいつでも落とし直せる）。
    /// ・zip でない、または展開できない（暗号化など）ファイルは、そのまま作品フォルダに置いて記録する。
    /// ・停止は Ctrl+C か、BaseDirectory に stop_games.txt を置く。どちらも作品の切れ目で止まる。
    /// </summary>
    public static class DojinGameCollector
    {
        public const string GamesFolderName = "games";
        public const string FolderKey = "game_folder";
        public const string DownloadedAtKey = "game_downloaded_at";
        public const string StopFileName = "stop_games.txt";
        public const string GameFormat = "ゲーム";

        /// <summary>作業用のフォルダ（games 配下）。ダウンロード中の zip と展開中のファイルを置く。</summary>
        private const string WorkFolderName = "_work";

        /// <summary>フォルダ名に使う作品名の長さ。ゲームは深い階層を持つことが多いので、パスが長くなりすぎないようにする。</summary>
        private const int MaxTitleLength = 40;

        public static string GamesDirectory(ConfigManager config) => Path.Combine(config.BaseDirectory, GamesFolderName);

        public static bool IsGame(JsonObject entry) => entry["format"]?.ToString() == GameFormat;

        public static async Task<int> RunAsync(DojinScraper scraper, ConfigManager config, DataManager dataManager)
        {
            Console.WriteLine("=== ゲームのダウンロード（dojin） ===");

            string gamesDir = GamesDirectory(config);
            string workDir = Path.Combine(gamesDir, WorkFolderName);
            string stopFile = Path.Combine(config.BaseDirectory, StopFileName);
            if (File.Exists(stopFile)) File.Delete(stopFile);

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                if (!cts.IsCancellationRequested)
                {
                    Console.WriteLine("\n[STOP] 停止要求を受け付けました。実行中の作品が終わり次第、終了します。");
                    cts.Cancel();
                }
            };
            Console.CancelKeyPress += onCancel;

            try
            {
                var games = dataManager.Data.Where(IsGame).ToList();
                var targets = games
                    .Select(entry => (entry, cid: ItemIdentity.Of(entry)))
                    .Where(t => t.cid.Length > 0 && !IsDownloaded(t.entry, gamesDir))
                    .ToList();

                Console.WriteLine($"保存先: {gamesDir}\\<作品名_cid>\\");
                Console.WriteLine($"ゲーム {games.Count} 件のうち、ダウンロード対象: {targets.Count} 件（ダウンロード済み: {games.Count - targets.Count} 件）");
                if (targets.Count == 0) return 0;
                Console.WriteLine($"停止するには Ctrl+C、または {stopFile} を作成してください（再実行で続きから再開できます）。");

                Directory.CreateDirectory(workDir);
                var stopwatch = Stopwatch.StartNew();
                int done = 0, failed = 0;

                for (int i = 0; i < targets.Count; i++)
                {
                    if (cts.IsCancellationRequested || File.Exists(stopFile))
                    {
                        Console.WriteLine("[STOP] 停止しました。再実行すると続きから再開します。");
                        break;
                    }

                    var (entry, cid) = targets[i];
                    string title = entry["title"]?.ToString() ?? "";
                    Console.WriteLine($"[{i + 1}/{targets.Count}] {cid} {title}（{entry["file_size"]?.ToString() ?? "サイズ不明"}）");

                    try
                    {
                        string folderName = FolderNameFor(title, cid);
                        var itemWatch = Stopwatch.StartNew();
                        string downloaded = await scraper.DownloadProductAsync(cid, Path.Combine(workDir, cid));
                        Console.WriteLine($"  -> ダウンロード完了: {Path.GetFileName(downloaded)}（{new FileInfo(downloaded).Length / 1024.0 / 1024.0:F1}MB / {itemWatch.Elapsed.TotalSeconds:F0}秒）");

                        string note = Install(downloaded, Path.Combine(gamesDir, folderName), Path.Combine(workDir, cid + "_extract"));
                        Console.WriteLine($"  -> {note}: {folderName}");

                        entry[FolderKey] = folderName;
                        entry[DownloadedAtKey] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                        if (!dataManager.SaveData(createBackup: false))
                        {
                            Console.WriteLine("[FATAL] data.json の保存に失敗したため中断します。");
                            return 1;
                        }
                        done++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine($"  -> 失敗（次回の実行で再試行します）: {ex.Message}");
                    }
                    finally
                    {
                        TryDelete(Path.Combine(workDir, cid));
                        TryDelete(Path.Combine(workDir, cid + "_extract"));
                    }
                }

                TryDeleteIfEmpty(workDir);
                Console.WriteLine($"=== ゲームのダウンロードを終了: 完了 {done} 件 / 失敗 {failed} 件 / 所要 {stopwatch.Elapsed:hh\\:mm\\:ss} ===");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        /// <summary>記録された作品フォルダが実在すればダウンロード済み（フォルダを消せば落とし直す）。</summary>
        public static bool IsDownloaded(JsonObject entry, string gamesDir)
        {
            string folder = entry[FolderKey]?.ToString() ?? "";
            return folder.Length > 0 && Directory.Exists(Path.Combine(gamesDir, folder));
        }

        /// <summary>作品フォルダの名前。「作品名_cid」（作品名は長すぎれば切る）。</summary>
        public static string FolderNameFor(string title, string cid)
        {
            string name = FileSanitizer.SanitizeFilename(title).Trim().TrimEnd('.');
            if (name.Length > MaxTitleLength) name = name[..MaxTitleLength].TrimEnd().TrimEnd('.');
            return name.Length > 0 ? $"{name}_{cid}" : cid;
        }

        /// <summary>
        /// ダウンロードしたファイルを作品フォルダ（destDir）に置く。zip なら展開し、展開できたら zip を消す。
        /// zip の中身が1つのフォルダだけなら、その中身を作品フォルダの直下に置く（開いてすぐ中身が見えるように）。
        /// 戻り値はログ用の説明。
        /// </summary>
        public static string Install(string downloaded, string destDir, string extractDir)
        {
            if (Directory.Exists(destDir)) throw new IOException($"作品フォルダが既にあります（記録が無いので消してから再実行してください）: {destDir}");

            if (string.Equals(Path.GetExtension(downloaded), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    TryDelete(extractDir);
                    ExtractZip(downloaded, extractDir);
                    Directory.Move(SingleTopFolder(extractDir) ?? extractDir, destDir);
                    TryDelete(extractDir);
                    File.Delete(downloaded);
                    return "展開しました";
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // 暗号化された zip など。手で展開できるよう、zip のまま置く
                    TryDelete(extractDir);
                    Directory.CreateDirectory(destDir);
                    File.Move(downloaded, Path.Combine(destDir, Path.GetFileName(downloaded)));
                    return $"[WARNING] 展開できなかったため zip のまま置きました（{ex.Message}）";
                }
            }

            Directory.CreateDirectory(destDir);
            File.Move(downloaded, Path.Combine(destDir, Path.GetFileName(downloaded)));
            return $"zip ではないため（{Path.GetExtension(downloaded)}）そのまま置きました";
        }

        /// <summary>
        /// zip を展開する。日本語のファイル名は、UTF-8 の印が無ければ Shift_JIS（日本の Windows で作った zip の多く）として読む。
        /// zip の外に書き出すパス（../ など）は .NET が拒否する。
        /// </summary>
        public static void ExtractZip(string zipPath, string destDir)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ZipFile.ExtractToDirectory(zipPath, destDir, Encoding.GetEncoding(932), overwriteFiles: true);
        }

        /// <summary>dir の中身がフォルダ1つだけ（ファイルが無い）なら、そのフォルダ。</summary>
        public static string? SingleTopFolder(string dir)
        {
            if (Directory.EnumerateFiles(dir).Any()) return null;
            var dirs = Directory.GetDirectories(dir);
            return dirs.Length == 1 ? dirs[0] : null;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARNING] 作業用ファイルを消せませんでした: {path}（{ex.Message}）");
            }
        }

        private static void TryDeleteIfEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch
            {
                // 残っても害はない
            }
        }
    }
}
