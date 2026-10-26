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
