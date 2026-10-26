using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace dmmCollect
{
    public class DirectoryScanner
    {
        public string BaseDirectory { get; }
        public List<string> TargetRoots { get; }
        private readonly HashSet<string> _excludeDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN", "System Volume Information"
        };

        public DirectoryScanner(string baseDirectory, List<string> targetRoots)
        {
            BaseDirectory = baseDirectory;
            TargetRoots = targetRoots;
        }

        /// <summary>
        /// cid → コンテンツ実体フォルダ（.dcv の親フォルダ）の対応表を返す。
        /// フォルダ名からの作品特定は、ショートカット同期や dmmWin と同じ FindItemsForVideo を使う。
        /// 同じ作品に複数フォルダが対応する場合は先に見つかったものを採用する。
        /// </summary>
        public Dictionary<string, string> MapContentFoldersByCid(DataManager dataManager)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var folder in FindAllDcvParentFolders())
            {
                string key = FileSanitizer.SanitizeFilename(Path.GetFileName(folder));
                foreach (var item in dataManager.FindItemsForVideo(key))
                {
                    string cid = ItemIdentity.Of(item);
                    if (cid.Length > 0) map.TryAdd(cid, folder);
                }
            }
            return map;
        }

        public List<string> FindAllDcvParentFolders()
        {
            Console.WriteLine("TargetRoots 内の全 .dcv ファイルの親フォルダを検索しています...");
            var dcvParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allDcvFiles = new List<string>();

            foreach (var root in TargetRoots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    var dcvs = Directory.GetFiles(root, "*.dcv", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                    allDcvFiles.AddRange(dcvs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNING] {root} 内の .dcv 検索中にエラー: {ex.Message}");
                }
            }

            foreach (var dcvFile in allDcvFiles)
            {
                var parts = dcvFile.Split(Path.DirectorySeparatorChar);
                if (parts.Any(p => _excludeDirs.Contains(p))) continue;

                var parent = Path.GetDirectoryName(dcvFile);
                if (!string.IsNullOrEmpty(parent))
                {
                    dcvParents.Add(Path.GetFullPath(parent));
                }
            }

            Console.WriteLine($"{dcvParents.Count} 件のユニークなコンテンツフォルダを発見しました。");
            var sortedList = dcvParents.ToList();
            sortedList.Sort(StringComparer.OrdinalIgnoreCase);
            return sortedList;
        }
    }
}
