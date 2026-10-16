using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using dmmCollect.Scrapers;

namespace dmmCollect
{
    /// <summary>
    /// コンテンツ実体（.dcv を含むフォルダ）を持つ作品のユーザーレビューを順に収集する。
    ///
    /// ・1作品ごとに comments.json と data.json を書き込むため、途中で止まっても再実行で続きから進む。
    ///   確認済みの作品には data.json に comments_checked_at を記録し、通常は再確認しない
    ///   （コメントが0件だった作品も確認済みとして扱う）。
    /// ・recheck=true なら確認済みの作品も再収集する（コメントの更新用）。
    /// ・停止は Ctrl+C か、BaseDirectory に stop_comments.txt を置く。どちらも作品の切れ目で止まる。
    /// </summary>
    public static class CommentCollector
    {
        public const string StopFileName = "stop_comments.txt";
        private const string CheckedAtKey = "comments_checked_at";
        private const string CountKey = "comment_count";
        private const int DelayBetweenItemsMs = 1000;

        public static async Task<int> RunAsync(VideoScraper scraper, ConfigManager config, DataManager dataManager, string? onlyCid, bool recheck)
        {
            Console.WriteLine("=== コメント収集 ===");

            string stopFile = Path.Combine(config.BaseDirectory, StopFileName);
            if (File.Exists(stopFile))
            {
                File.Delete(stopFile);
            }

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
                var scanner = new DirectoryScanner(config.BaseDirectory, config.TargetRoots);
                var folderByCid = scanner.MapContentFoldersByCid(dataManager);
                Console.WriteLine($"コンテンツ実体を持つ作品: {folderByCid.Count} 件");

                // 全文検索用の索引。失敗しても収集（comments.json / data.json）は続ける。
                CommentIndex? commentIndex = OpenIndex(config);

                var targets = new List<(JsonObject entry, string cid, string folder)>();
                int skipped = 0;
                foreach (var entry in dataManager.Data)
                {
                    string cid = ItemIdentity.Of(entry);
                    if (cid.Length == 0 || !folderByCid.TryGetValue(cid, out var folder)) continue;
                    if (onlyCid != null && cid != onlyCid) continue;
                    if (!recheck && onlyCid == null && entry[CheckedAtKey] != null)
                    {
                        skipped++;
                        continue;
                    }
                    targets.Add((entry, cid, folder));
                }

                if (onlyCid != null && targets.Count == 0)
                {
                    Console.WriteLine($"[FATAL] cid '{onlyCid}' のコンテンツ実体が見つかりません。");
                    return 1;
                }

                Console.WriteLine($"収集対象: {targets.Count} 件（確認済みのためスキップ: {skipped} 件{(recheck ? "、再チェック指定" : "")}）");
                Console.WriteLine($"停止するには Ctrl+C、または {stopFile} を作成してください（再実行で続きから再開できます）。");

                var stopwatch = Stopwatch.StartNew();
                int done = 0, withComments = 0, failed = 0;

                foreach (var (entry, cid, folder) in targets)
                {
                    if (cts.IsCancellationRequested || File.Exists(stopFile))
                    {
                        Console.WriteLine("[STOP] 停止しました。再実行すると続きから再開します。");
                        break;
                    }

                    int index = done + failed + 1;
                    string title = entry["title"]?.ToString() ?? "";
                    Console.WriteLine($"[{index}/{targets.Count}] {cid} {Truncate(title, 40)}");

                    var reviews = await scraper.FetchReviewsForItemAsync(cid);
                    if (reviews == null)
                    {
                        failed++;
                        Console.WriteLine("  -> 取得失敗（次回の実行で再試行します）");
                        continue;
                    }

                    if (reviews.Comments.Count > 0)
                    {
                        string savedPath = CommentStore.Save(folder, reviews);
                        withComments++;
                        try { commentIndex?.Upsert(reviews, savedPath); }
                        catch (Exception ex) { Console.WriteLine($"[WARNING] コメント索引の更新に失敗しました: {ex.Message}"); }
                    }
                    entry[CountKey] = reviews.Comments.Count;
                    entry[CheckedAtKey] = reviews.CollectedAt;
                    if (!dataManager.SaveData(createBackup: false))
                    {
                        Console.WriteLine("[FATAL] data.json の保存に失敗したため中断します。");
                        return 1;
                    }
                    done++;

                    TimeSpan elapsed = stopwatch.Elapsed;
                    TimeSpan remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds / done * (targets.Count - done - failed));
                    Console.WriteLine($"  -> コメント {reviews.Comments.Count} 件 | 完了 {done}/{targets.Count}（コメントあり {withComments}、失敗 {failed}） | 経過 {Format(elapsed)} / 残り約 {Format(remaining)}");

                    try { await Task.Delay(DelayBetweenItemsMs, cts.Token); } catch (OperationCanceledException) { }
                }

                commentIndex?.Dispose();
                Console.WriteLine($"=== コメント収集を終了: 完了 {done} 件 / コメントあり {withComments} 件 / 失敗 {failed} 件 / 所要 {Format(stopwatch.Elapsed)} ===");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static CommentIndex? OpenIndex(ConfigManager config)
        {
            try
            {
                return new CommentIndex(config.BaseDirectory);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] コメント索引を開けません（収集は続行します）: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 実体フォルダの comments.json をすべて走査して全文検索索引へ取り込む。
        /// 索引済みで内容が変わっていないもの（更新日時・サイズ一致）はスキップするので、何度実行してもよい。
        /// </summary>
        public static int BuildIndex(ConfigManager config, DataManager dataManager)
        {
            Console.WriteLine("=== コメント索引の作成 ===");
            var scanner = new DirectoryScanner(config.BaseDirectory, config.TargetRoots);
            var folderByCid = scanner.MapContentFoldersByCid(dataManager);

            using var index = new CommentIndex(config.BaseDirectory);
            int total = 0, indexed = 0, upToDate = 0, failed = 0;
            var stopwatch = Stopwatch.StartNew();

            foreach (var (cid, folder) in folderByCid)
            {
                string path = Path.Combine(folder, CommentStore.FileName);
                if (!File.Exists(path)) continue;
                total++;

                try
                {
                    if (index.IsUpToDate(cid, path)) { upToDate++; continue; }

                    var collection = JsonSerializer.Deserialize<ReviewCollection>(File.ReadAllText(path));
                    if (collection == null) { failed++; continue; }
                    collection.Cid = cid;
                    index.Upsert(collection, path);
                    indexed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"[WARNING] 索引に取り込めません: {path} ({ex.Message})");
                }

                if ((indexed + upToDate + failed) % 200 == 0)
                {
                    Console.WriteLine($"  {indexed + upToDate + failed} 件処理 (取り込み {indexed} / 最新のためスキップ {upToDate} / 失敗 {failed})");
                }
            }

            Console.WriteLine($"=== 索引作成を終了: comments.json {total} 件 / 取り込み {indexed} / スキップ {upToDate} / 失敗 {failed} / 所要 {Format(stopwatch.Elapsed)} ===");
            return failed == 0 ? 0 : 1;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}時間{t.Minutes:00}分" : $"{t.Minutes}分{t.Seconds:00}秒";
    }
}
