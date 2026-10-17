using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
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
    ///   （コメントが0件だった作品も確認済みとして扱う）。あわせて公式の平均評価・評価数
    ///   （rating_average / rating_count）も data.json に記録する。これらが未記録の作品は、
    ///   確認済みでも一度だけ再取得する。
    /// ・recheck=true なら確認済みの作品も再収集する（コメントの更新用）。
    /// ・停止は Ctrl+C か、BaseDirectory に stop_comments.txt を置く。どちらも作品の切れ目で止まる。
    /// </summary>
    public static class CommentCollector
    {
        public const string StopFileName = "stop_comments.txt";
        private const string CheckedAtKey = "comments_checked_at";
        private const string CountKey = "comment_count";
        private const string RatingAverageKey = "rating_average";
        private const string RatingCountKey = "rating_count";
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
                    if (!recheck && onlyCid == null && entry[CheckedAtKey] != null && entry[RatingCountKey] != null)
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
                    SetRating(entry, reviews);
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

                // 収集で増えたコメントを観点の評判・食い違いに反映する（数秒。失敗しても収集結果には影響しない）
                if (done > 0 && onlyCid == null)
                {
                    try { AnalyzeAll(config, dataManager, dryRun: false); }
                    catch (Exception ex) { Console.WriteLine($"[WARNING] コメント解析に失敗しました: {ex.Message}"); }
                }

                Console.WriteLine($"=== コメント収集を終了: 完了 {done} 件 / コメントあり {withComments} 件 / 失敗 {failed} 件 / 所要 {Format(stopwatch.Elapsed)} ===");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        /// <summary>公式の平均評価と評価数を data.json のエントリへ書く。評価が無い作品は平均を持たない。</summary>
        private static void SetRating(JsonObject entry, ReviewCollection reviews)
        {
            entry[RatingCountKey] = reviews.RatingCount;
            if (reviews.AverageRating.HasValue) entry[RatingAverageKey] = reviews.AverageRating.Value;
            else entry.Remove(RatingAverageKey);
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
            var entryByCid = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var e in dataManager.Data)
            {
                string id = ItemIdentity.Of(e);
                if (id.Length > 0) entryByCid.TryAdd(id, e);
            }
            int total = 0, indexed = 0, upToDate = 0, failed = 0, ratingsFilled = 0;
            var stopwatch = Stopwatch.StartNew();

            foreach (var (cid, folder) in folderByCid)
            {
                string path = Path.Combine(folder, CommentStore.FileName);
                if (!File.Exists(path)) continue;
                total++;

                try
                {
                    // 評価値が data.json に無い作品は、comments.json の集計値から補完する（再取得は不要）
                    bool needsRating = entryByCid.TryGetValue(cid, out var entry) && entry[RatingCountKey] == null;
                    bool needsIndex = !index.IsUpToDate(cid, path);
                    if (!needsRating && !needsIndex) { upToDate++; continue; }

                    var collection = JsonSerializer.Deserialize<ReviewCollection>(File.ReadAllText(path));
                    if (collection == null) { failed++; continue; }
                    collection.Cid = cid;

                    if (needsRating)
                    {
                        SetRating(entry!, collection);
                        ratingsFilled++;
                    }
                    if (needsIndex)
                    {
                        index.Upsert(collection, path);
                        indexed++;
                    }
                    else
                    {
                        upToDate++;
                    }
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

            if (ratingsFilled > 0 && !dataManager.SaveData(createBackup: false))
            {
                Console.WriteLine("[FATAL] data.json の保存に失敗しました。");
                return 1;
            }

            Console.WriteLine($"=== 索引作成を終了: comments.json {total} 件 / 取り込み {indexed} / スキップ {upToDate} / 評価値を data.json に補完 {ratingsFilled} / 失敗 {failed} / 所要 {Format(stopwatch.Elapsed)} ===");
            return failed == 0 ? 0 : 1;
        }

        private const string AspectsKey = "comment_aspects";
        private const string MismatchKey = "comment_mismatch";

        /// <summary>
        /// 全作品のコメント（comments.sqlite）から、観点の評判と星・本文の食い違いを求めて data.json に書く。
        /// 辞書とルールだけの処理で数秒で終わるため、毎回全作品を計算し直す（辞書を直した場合も再実行でよい）。
        /// dryRun なら data.json は書かず、件数と食い違いコメントの例を表示する。
        /// </summary>
        public static int AnalyzeAll(ConfigManager config, DataManager dataManager, bool dryRun, bool verifyWithLlm = false, string? dumpAspectsPath = null)
        {
            Console.WriteLine($"=== コメント解析{(dryRun ? "（確認のみ・data.json は書きません）" : "")} ===");
            string dbPath = Path.Combine(config.BaseDirectory, CommentIndex.FileName);
            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"[FATAL] コメント索引がありません: {dbPath}（--build-comment-index で作成してください）");
                return 1;
            }

            var byCid = new Dictionary<string, List<CommentInput>>(StringComparer.Ordinal);
            var examples = new List<string>();
            var candidates = new List<CommentInput>();   // 解析器が食い違いと判定したもの（LLM の確認対象）
            int total = 0, highNeg = 0, lowPos = 0;

            using (var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT cid, rating, title, body FROM comments";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string cid = reader.GetString(0);
                    var comment = new CommentInput(reader.GetInt32(1), reader.GetString(2), reader.GetString(3));
                    if (!byCid.TryGetValue(cid, out var list)) byCid[cid] = list = new List<CommentInput>();
                    list.Add(comment);

                    total++;
                    var kind = CommentAnalyzer.MismatchOf(comment);
                    if (kind == CommentMismatch.HighStarNegativeText) highNeg++;
                    if (kind == CommentMismatch.LowStarPositiveText) lowPos++;
                    if (kind != CommentMismatch.None) candidates.Add(comment);
                    if (kind != CommentMismatch.None && examples.Count < 400 && total % 7 == 0)
                    {
                        string text = (comment.Title + " / " + comment.Body).Replace("\n", " ");
                        examples.Add($"[星{comment.Rating} {(kind == CommentMismatch.HighStarNegativeText ? "本文否定" : "本文肯定")}] {(text.Length > 110 ? text[..110] + "…" : text)}");
                    }
                }
            }

            if (dumpAspectsPath != null) DumpAspects(byCid, dumpAspectsPath);

            // LLM の判定（保存済みのものは常に使う。--verify-llm なら未判定の候補を判定する）
            var verdicts = new CommentVerdictStore(config.BaseDirectory);
            if (verifyWithLlm)
            {
                int code = VerifyCandidates(config, candidates, verdicts);
                if (code != 0) return code;
            }
            int verified = candidates.Count(c => verdicts.Verdicts.ContainsKey(CommentAnalyzer.VerdictKey(c)));
            int confirmed = candidates.Count(c =>
                CommentAnalyzer.Refine(CommentAnalyzer.MismatchOf(c), verdicts.Verdicts.GetValueOrDefault(CommentAnalyzer.VerdictKey(c)))
                != CommentMismatch.None);

            var entryByCid = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var e in dataManager.Data)
            {
                string id = ItemIdentity.Of(e);
                if (id.Length > 0) entryByCid.TryAdd(id, e);
            }

            var aspectCounts = new Dictionary<string, int>();
            var mismatchCounts = new Dictionary<string, int>();
            int worksWithAspects = 0, worksWithMismatch = 0;

            foreach (var (cid, comments) in byCid)
            {
                var analysis = CommentAnalyzer.AnalyzeWork(comments, verdicts.Verdicts);
                foreach (var a in analysis.Aspects) aspectCounts[a] = aspectCounts.GetValueOrDefault(a) + 1;
                foreach (var m in analysis.Mismatches) mismatchCounts[m] = mismatchCounts.GetValueOrDefault(m) + 1;
                if (analysis.Aspects.Count > 0) worksWithAspects++;
                if (analysis.Mismatches.Count > 0) worksWithMismatch++;

                if (dryRun || !entryByCid.TryGetValue(cid, out var entry)) continue;
                SetLabels(entry, AspectsKey, analysis.Aspects);
                SetLabels(entry, MismatchKey, analysis.Mismatches);
            }

            Console.WriteLine($"コメント {total} 件 / {byCid.Count} 作品");
            Console.WriteLine($"星と本文の食い違い（解析器の候補）: 星高・本文否定 {highNeg} 件 / 星低・本文肯定 {lowPos} 件");
            Console.WriteLine($"  LLM で確認済み {verified}/{candidates.Count} 件 → 食い違いとして残ったもの {confirmed} 件（未確認の候補は候補のまま数えます）");
            Console.WriteLine($"観点の評判が付いた作品: {worksWithAspects} / 食い違いラベルが付いた作品: {worksWithMismatch}");
            foreach (var (label, count) in aspectCounts.OrderByDescending(p => p.Value)) Console.WriteLine($"  {label}: {count}作品");
            foreach (var (label, count) in mismatchCounts) Console.WriteLine($"  {label}: {count}作品");

            if (dryRun)
            {
                Console.WriteLine("\n--- 食い違いコメントの例 ---");
                foreach (var line in examples.Take(40)) Console.WriteLine(line);
                return 0;
            }

            if (!dataManager.SaveData(createBackup: false))
            {
                Console.WriteLine("[FATAL] data.json の保存に失敗しました。");
                return 1;
            }
            Console.WriteLine("data.json を更新しました。");
            return 0;
        }

        /// <summary>
        /// 食い違いの候補を LLM に読ませて判定を保存する。判定は1件ごとに保存せず、10件ごと・終了時・停止時にまとめて保存する
        /// （同じ本文は再判定しない）。Ctrl+C で止めても、それまでの判定は残る。
        /// </summary>
        private static int VerifyCandidates(ConfigManager config, List<CommentInput> candidates, CommentVerdictStore verdicts)
        {
            var pending = candidates
                .GroupBy(CommentAnalyzer.VerdictKey)
                .Select(g => g.First())
                .Where(c => !verdicts.Verdicts.ContainsKey(CommentAnalyzer.VerdictKey(c)))
                .ToList();

            Console.WriteLine($"LLM で確認する候補: {pending.Count} 件（判定済みの {candidates.Count - pending.Count} 件は再利用）");
            if (pending.Count == 0) return 0;

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                if (!cts.IsCancellationRequested)
                {
                    Console.WriteLine("\n[STOP] 停止要求を受け付けました。判定を保存して終了します。");
                    cts.Cancel();
                }
            };
            Console.CancelKeyPress += onCancel;

            try
            {
                using var verifier = new CommentVerifier(config.Config.llm);
                string? problem = verifier.CheckAsync(cts.Token).GetAwaiter().GetResult();
                if (problem != null)
                {
                    Console.WriteLine($"[FATAL] {problem}");
                    return 1;
                }
                Console.WriteLine($"判定モデル: {verifier.Model}");

                var stopwatch = Stopwatch.StartNew();
                int done = 0, failed = 0;
                foreach (var comment in pending)
                {
                    if (cts.IsCancellationRequested) break;

                    try
                    {
                        string? verdict = verifier.JudgeAsync(comment, cts.Token).GetAwaiter().GetResult();
                        if (verdict == null) { failed++; continue; }
                        verdicts.Verdicts[CommentAnalyzer.VerdictKey(comment)] = verdict;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine($"[WARNING] 判定に失敗しました: {ex.Message}");
                        continue;
                    }

                    done++;
                    if (done % 10 == 0)
                    {
                        verdicts.Save();
                        TimeSpan remaining = TimeSpan.FromSeconds(stopwatch.Elapsed.TotalSeconds / (done + failed) * (pending.Count - done - failed));
                        Console.WriteLine($"  判定 {done + failed}/{pending.Count}（失敗 {failed}） 経過 {Format(stopwatch.Elapsed)} / 残り約 {Format(remaining)}");
                    }
                }

                verdicts.Save();
                Console.WriteLine($"LLM の判定を保存しました: {done} 件（失敗 {failed} 件。失敗分は次回の実行でやり直します）");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        /// <summary>
        /// 辞書の調整用。解析器が付けた「コメント×観点×極性」を1行1件の JSON で書き出す。
        /// 極性が付いたものだけを書く（LLM に抜き取って判定させ、一致率を見るための材料）。
        /// </summary>
        private static void DumpAspects(Dictionary<string, List<CommentInput>> byCid, string path)
        {
            int lines = 0;
            using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
            foreach (var (cid, comments) in byCid)
            {
                foreach (var comment in comments)
                {
                    foreach (var (aspect, polarity) in CommentAnalyzer.AspectPolarities(comment))
                    {
                        var row = new JsonObject
                        {
                            ["cid"] = cid,
                            ["rating"] = comment.Rating,
                            ["title"] = comment.Title,
                            ["body"] = comment.Body,
                            ["aspect"] = aspect,
                            ["polarity"] = polarity,
                        };
                        writer.WriteLine(row.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                        lines++;
                    }
                }
            }
            Console.WriteLine($"観点の極性を書き出しました: {lines} 行 → {path}");
        }

        private static void SetLabels(JsonObject entry, string key, List<string> labels)
        {
            if (labels.Count == 0) entry.Remove(key);
            else entry[key] = new JsonArray(labels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}時間{t.Minutes:00}分" : $"{t.Minutes}分{t.Seconds:00}秒";
    }
}
