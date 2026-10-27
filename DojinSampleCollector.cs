using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using dmmCollect.Scrapers;

namespace dmmCollect
{
    /// <summary>
    /// dojin の全作品のサンプル画像を、作品ページ（HTTP。ブラウザ・ログイン不要）から集めて
    /// ImageDirectory\&lt;cid&gt;\ に保存する（video と同じ置き方。dmmWin はここから表示する）。
    ///
    /// ・確認した作品には data.json に sample_images_checked_at と sample_image_count を記録し、次からは取りに行かない。
    ///   販売終了で作品ページが無い（404）作品や、サンプル画像の無い作品も 0 枚として記録する。
    /// ・一部の画像が取れなかった作品は記録せず、次の実行で取り直す（保存済みの画像は取り直さない）。
    /// ・data.json は一定件数・一定時間ごと（と終了時）にまとめて書く。GUI の停止（プロセス終了）でも、
    ///   最後の書き込み以降の作品を取り直すだけで、画像は一時ファイル経由で書くので壊れたファイルは残らない。
    /// ・停止は Ctrl+C か、BaseDirectory に stop_samples.txt を置く。どちらも作品の切れ目で止まる。
    /// </summary>
    public static class DojinSampleCollector
    {
        public const string StopFileName = "stop_samples.txt";
        public const string CheckedAtKey = "sample_images_checked_at";
        public const string CountKey = "sample_image_count";
        private const int DelayBetweenItemsMs = 500;

        // data.json をまとめて書く間隔（どちらかに達したら書く）
        private const int SaveEveryItems = 20;
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

        public static async Task<int> RunAsync(ConfigManager config, DataManager dataManager)
        {
            Console.WriteLine("=== サンプル画像の収集（dojin） ===");

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
                var targets = dataManager.Data
                    .Select(entry => (entry, cid: ItemIdentity.Of(entry)))
                    .Where(t => t.cid.Length > 0 && t.entry[CheckedAtKey] == null)
                    .ToList();
                int skipped = dataManager.Data.Count(e => e[CheckedAtKey] != null);

                Console.WriteLine($"保存先: {config.ImageDirectory}\\<cid>\\");
                Console.WriteLine($"収集対象: {targets.Count} 件（確認済みのためスキップ: {skipped} 件）");
                Console.WriteLine($"停止するには Ctrl+C、または {stopFile} を作成してください（再実行で続きから再開できます）。");

                var stopwatch = Stopwatch.StartNew();
                int done = 0, withImages = 0, notFound = 0, failed = 0, totalImages = 0;

                int unsaved = 0;
                var sinceSave = Stopwatch.StartNew();
                bool Flush()
                {
                    if (unsaved == 0) return true;
                    if (!dataManager.SaveData(createBackup: false)) return false;
                    unsaved = 0;
                    sinceSave.Restart();
                    return true;
                }

                foreach (var (entry, cid) in targets)
                {
                    if (cts.IsCancellationRequested || File.Exists(stopFile))
                    {
                        Console.WriteLine("[STOP] 停止しました。再実行すると続きから再開します。");
                        break;
                    }

                    int index = done + failed + 1;
                    Console.WriteLine($"[{index}/{targets.Count}] {cid} {Truncate(entry["title"]?.ToString() ?? "", 40)}");

                    int count;
                    try
                    {
                        var page = await DojinSampleImages.FetchAsync(cid, cts.Token);
                        if (page.NotFound)
                        {
                            notFound++;
                            count = 0;
                            Console.WriteLine("  -> 作品ページがありません（販売終了など）。サンプル画像なしとして記録します");
                        }
                        else
                        {
                            count = page.Samples.Count == 0 ? 0
                                : await DojinSampleImages.DownloadAsync(page.Samples, Path.Combine(config.ImageDirectory, cid), cts.Token);
                            if (count < page.Samples.Count)
                            {
                                failed++;
                                Console.WriteLine($"  -> {page.Samples.Count} 枚中 {count} 枚しか保存できませんでした（次回の実行で残りを取り直します）");
                                continue;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        Console.WriteLine("[STOP] 停止しました。再実行すると続きから再開します。");
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine($"  -> 取得失敗（次回の実行で再試行します）: {ex.Message}");
                        continue;
                    }

                    entry[CountKey] = count;
                    entry[CheckedAtKey] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                    unsaved++;
                    if ((unsaved >= SaveEveryItems || sinceSave.Elapsed >= SaveInterval) && !Flush())
                    {
                        Console.WriteLine("[FATAL] data.json の保存に失敗したため中断します。");
                        return 1;
                    }
                    done++;
                    totalImages += count;
                    if (count > 0) withImages++;

                    TimeSpan elapsed = stopwatch.Elapsed;
                    TimeSpan remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds / done * (targets.Count - done - failed));
                    Console.WriteLine($"  -> サンプル画像 {count} 枚 | 完了 {done}/{targets.Count}（画像あり {withImages}、失敗 {failed}） | 経過 {Format(elapsed)} / 残り約 {Format(remaining)}");

                    try { await Task.Delay(DelayBetweenItemsMs, cts.Token); } catch (OperationCanceledException) { }
                }

                if (!Flush())
                {
                    Console.WriteLine("[FATAL] data.json の保存に失敗しました。");
                    return 1;
                }

                Console.WriteLine($"=== サンプル画像の収集を終了: 完了 {done} 件（画像あり {withImages} 件・計 {totalImages} 枚、作品ページなし {notFound} 件） / 失敗 {failed} 件 / 所要 {Format(stopwatch.Elapsed)} ===");
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
