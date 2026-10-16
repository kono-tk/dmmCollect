using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                        CommentStore.Save(folder, reviews);
                        withComments++;
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

                Console.WriteLine($"=== コメント収集を終了: 完了 {done} 件 / コメントあり {withComments} 件 / 失敗 {failed} 件 / 所要 {Format(stopwatch.Elapsed)} ===");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}時間{t.Minutes:00}分" : $"{t.Minutes}分{t.Seconds:00}秒";
    }
}
