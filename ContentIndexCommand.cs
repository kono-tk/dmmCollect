using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace dmmCollect
{
    /// <summary>
    /// --build-content-index: コンテンツ索引（content_index.json）を作成・更新し、索引の内容を報告する。
    /// </summary>
    public static class ContentIndexCommand
    {
        public static int Run(ConfigManager config, DataManager dataManager)
        {
            Console.WriteLine("=== コンテンツ索引の作成/更新 ===");
            string indexPath = ContentIndex.PathFor(config.BaseDirectory);
            var previous = ContentIndex.Load(indexPath);
            Console.WriteLine(previous == null ? "既存の索引はありません（全件を読み込みます）" : $"既存の索引: {previous.Folders.Count} フォルダ");

            var stopwatch = Stopwatch.StartNew();
            var index = ContentIndex.Build(config.TargetRoots, previous, out var stats, Console.WriteLine);
            index.Save(indexPath);
            stopwatch.Stop();

            Console.WriteLine($"保存しました: {indexPath}");
            Console.WriteLine($"実体フォルダ {stats.Folders} 件 / .dcv 読み込み {stats.FilesRead} 件・再利用 {stats.FilesReused} 件 / 所要 {stopwatch.Elapsed.TotalSeconds:F1} 秒");
            if (stats.UnavailableRoots.Count > 0)
            {
                Console.WriteLine($"[WARNING] 見つからないフォルダ（前回の情報を引き継ぎ）: {string.Join(", ", stats.UnavailableRoots)}");
            }

            Report(index, config, dataManager);
            return 0;
        }

        /// <summary>
        /// 索引を差分更新して保存する（変わっていない .dcv は読み直さないので数秒で終わる）。
        /// 収集の最後とコメント収集の前に呼ぶ。dmmWin は索引ファイルの更新を見て一覧を作り直す。
        /// </summary>
        public static ContentIndex Update(ConfigManager config)
        {
            string indexPath = ContentIndex.PathFor(config.BaseDirectory);
            var index = ContentIndex.Build(config.TargetRoots, ContentIndex.Load(indexPath), out var stats, Console.WriteLine);
            index.Save(indexPath);
            Console.WriteLine($"コンテンツ索引を更新しました（実体フォルダ {stats.Folders} 件 / .dcv 読み込み {stats.FilesRead} 件）");
            if (stats.UnavailableRoots.Count > 0)
            {
                Console.WriteLine($"[WARNING] 見つからないフォルダ（前回の情報を引き継ぎ）: {string.Join(", ", stats.UnavailableRoots)}");
            }
            return index;
        }

        private static void Report(ContentIndex index, ConfigManager config, DataManager dataManager)
        {
            var dataCids = new HashSet<string>(dataManager.Data.Select(e => ItemIdentity.Of(e)).Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase);
            var byCid = index.FoldersByWork(ContentIndex.CidForFolderName(dataManager.Data));

            Console.WriteLine("\n--- 索引の内容 ---");
            Console.WriteLine($"cid を読めなかったフォルダ: {index.Folders.Count(f => f.Cid == null)}");
            foreach (var f in index.Folders.Where(f => f.Cid == null).Take(10)) Console.WriteLine($"   {f.Path}");
            var unknown = byCid.Keys.Where(c => !dataCids.Contains(c)).ToList();
            Console.WriteLine($"data.json に無い cid: {unknown.Count}");
            foreach (var c in unknown.Take(10)) Console.WriteLine($"   {c}: {byCid[c][0].Path}");
            var multi = byCid.Where(p => p.Value.Count > 1).ToList();
            Console.WriteLine($"複数の実体フォルダを持つ cid: {multi.Count}（{multi.Sum(p => p.Value.Count)} フォルダ）");
            foreach (var p in multi.Take(10)) Console.WriteLine($"   {p.Key}: {p.Value.Count} フォルダ（例: {Path.GetFileName(p.Value[0].Path)}）");
            Console.WriteLine($"福袋の中身として振り分けたフォルダ: {byCid.Sum(p => p.Value.Count(f => !string.Equals(f.Cid, p.Key, StringComparison.OrdinalIgnoreCase)))}");
            Console.WriteLine($"収録作品名を持つフォルダ: {index.Folders.Count(f => f.Titles.Count > 0)}");
            Console.WriteLine($"一覧に出る data.json の作品: {byCid.Keys.Count(dataCids.Contains)}");

        }
    }
}
