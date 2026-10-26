using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace dmmCollect
{
    /// <summary>
    /// --build-content-index: コンテンツ索引（content_index.json）を作成・更新し、
    /// 現行のタイトル照合（フォルダ名 → FindItemsForVideo）による対応付けとの差分を報告する。
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

        private static void Report(ContentIndex index, ConfigManager config, DataManager dataManager)
        {
            var dataCids = new HashSet<string>(dataManager.Data.Select(e => ItemIdentity.Of(e)).Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase);
            var byCid = index.FoldersByCid();

            Console.WriteLine("\n--- 索引の内容 ---");
            Console.WriteLine($"cid を読めなかったフォルダ: {index.Folders.Count(f => f.Cid == null)}");
            foreach (var f in index.Folders.Where(f => f.Cid == null).Take(10)) Console.WriteLine($"   {f.Path}");
            var unknown = byCid.Keys.Where(c => !dataCids.Contains(c)).ToList();
            Console.WriteLine($"data.json に無い cid: {unknown.Count}");
            foreach (var c in unknown.Take(10)) Console.WriteLine($"   {c}: {byCid[c][0].Path}");
            var multi = byCid.Where(p => p.Value.Count > 1).ToList();
            Console.WriteLine($"複数の実体フォルダを持つ cid: {multi.Count}（{multi.Sum(p => p.Value.Count)} フォルダ）");
            foreach (var p in multi.Take(10)) Console.WriteLine($"   {p.Key}: {p.Value.Count} フォルダ（例: {Path.GetFileName(p.Value[0].Path)}）");
            Console.WriteLine($"収録作品名を持つフォルダ: {index.Folders.Count(f => f.Titles.Count > 0)}");
            Console.WriteLine($"一覧に出る data.json の作品: {byCid.Keys.Count(dataCids.Contains)}");

            // 現行のタイトル照合（フォルダ名 → data.json の作品）との比較
            Console.WriteLine("\n--- 現行のタイトル照合との比較 ---");
            var titleFolderByCid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in index.Folders.Where(f => f.Available))
            {
                string key = FileSanitizer.SanitizeFilename(Path.GetFileName(folder.Path));
                foreach (var item in dataManager.FindItemsForVideo(key))
                {
                    string cid = ItemIdentity.Of(item);
                    if (cid.Length > 0) titleFolderByCid.TryAdd(cid, folder.Path);
                }
            }

            var agree = new List<string>();
            var indexOnly = new List<string>();
            var titleOnly = new List<string>();
            var disagree = new List<string>();
            foreach (var cid in byCid.Keys.Union(titleFolderByCid.Keys, StringComparer.OrdinalIgnoreCase).Where(dataCids.Contains))
            {
                bool inIndex = byCid.TryGetValue(cid, out var folders);
                bool inTitle = titleFolderByCid.TryGetValue(cid, out var titleFolder);
                if (inIndex && inTitle)
                {
                    if (folders!.Any(f => string.Equals(f.Path, titleFolder, StringComparison.OrdinalIgnoreCase))) agree.Add(cid);
                    else disagree.Add(cid);
                }
                else if (inIndex) indexOnly.Add(cid);
                else titleOnly.Add(cid);
            }

            Console.WriteLine($"一致: {agree.Count}");
            Console.WriteLine($"索引でだけ実体が見つかる（タイトル照合では漏れていた）: {indexOnly.Count}");
            foreach (var c in indexOnly.Take(15)) Console.WriteLine($"   {c}: {Path.GetFileName(byCid[c][0].Path)}");
            Console.WriteLine($"タイトル照合でだけ見つかる（索引では別の作品の実体）: {titleOnly.Count}");
            foreach (var c in titleOnly.Take(15))
            {
                string folder = titleFolderByCid[c];
                string? indexCid = index.Folders.FirstOrDefault(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase))?.Cid;
                Console.WriteLine($"   {c}: {Path.GetFileName(folder)}（索引では {indexCid ?? "cid なし"}）");
            }
            Console.WriteLine($"対応するフォルダが食い違う: {disagree.Count}");
            foreach (var c in disagree.Take(15)) Console.WriteLine($"   {c}: 照合 {Path.GetFileName(titleFolderByCid[c])} / 索引 {Path.GetFileName(byCid[c][0].Path)}");
        }
    }
}
