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

        public List<string> FindKeywordDirs(List<string>? keywords = null)
        {
            Console.WriteLine("処理対象となるキーワードディレクトリを再帰的に検索しています...");
            var candidateDirs = new List<string>();

            try
            {
                if (Directory.Exists(BaseDirectory))
                {
                    var allDirs = Directory.GetDirectories(BaseDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                    foreach (var dir in allDirs)
                    {
                        var dirName = Path.GetFileName(dir);
                        if (dirName.Equals("FLAT", StringComparison.OrdinalIgnoreCase) || dirName.Equals("_lost_found", StringComparison.OrdinalIgnoreCase) || dirName.Equals("lost+found", StringComparison.OrdinalIgnoreCase)) continue;

                        var htmlFile = Path.Combine(dir, $"{dirName}.html");
                        if (File.Exists(htmlFile))
                        {
                            candidateDirs.Add(dir);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] キーワードディレクトリ検索中にエラー: {ex.Message}");
            }

            if (keywords == null || keywords.Count == 0)
            {
                Console.WriteLine($"{candidateDirs.Count} 件の候補ディレクトリが見つかりました。");
                return candidateDirs;
            }

            var keywordsSet = new HashSet<string>(keywords, StringComparer.OrdinalIgnoreCase);
            var targetDirs = candidateDirs.Where(d => keywordsSet.Contains(Path.GetFileName(d))).ToList();
            Console.WriteLine($"キーワードに一致した {targetDirs.Count} 件の候補ディレクトリが見つかりました。");
            return targetDirs;
        }

        public List<string> GetTargetFolders(List<string>? keywords = null)
        {
            var targetFolders = new List<string>();
            try
            {
                if (!Directory.Exists(BaseDirectory)) return targetFolders;

                if (keywords != null && keywords.Count > 0)
                {
                    var keywordsSet = new HashSet<string>(keywords, StringComparer.OrdinalIgnoreCase);
                    var allDirs = Directory.GetDirectories(BaseDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                    foreach (var dir in allDirs)
                    {
                        var dirName = Path.GetFileName(dir);
                        if (dirName.Equals("FLAT", StringComparison.OrdinalIgnoreCase) || dirName.Equals("_lost_found", StringComparison.OrdinalIgnoreCase) || dirName.Equals("lost+found", StringComparison.OrdinalIgnoreCase)) continue;

                        if (keywordsSet.Contains(dirName))
                        {
                            targetFolders.Add(dir);
                        }
                    }
                }
                else
                {
                    // 配下にディレクトリを持たない末端フォルダ（リーフフォルダ）をスキャン
                    var allDirs = Directory.GetDirectories(BaseDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                    foreach (var dir in allDirs)
                    {
                        var dirName = Path.GetFileName(dir);
                        if (dirName.Equals("FLAT", StringComparison.OrdinalIgnoreCase) || dirName.Equals("_lost_found", StringComparison.OrdinalIgnoreCase) || dirName.Equals("lost+found", StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            var subDirs = Directory.GetDirectories(dir);
                            if (subDirs.Length == 0)
                            {
                                targetFolders.Add(dir);
                            }
                        }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] GetTargetFoldersスキャン中にエラー: {ex.Message}");
            }
            return targetFolders;
        }

        public Dictionary<string, string> FindTargetFolders()
        {
            Console.WriteLine("ショートカット先のフォルダを全検索しています...");
            var folderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in TargetRoots)
            {
                if (!Directory.Exists(root)) continue;

                try
                {
                    var allDirs = Directory.GetDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                    foreach (var path in allDirs)
                    {
                        // 除外ディレクトリ判定
                        var parts = path.Split(Path.DirectorySeparatorChar);
                        if (parts.Any(p => _excludeDirs.Contains(p))) continue;

                        try
                        {
                            // 末端フォルダ（子ディレクトリを含まない）の判定
                            var subDirs = Directory.GetDirectories(path);
                            if (subDirs.Length == 0)
                            {
                                var folderName = Path.GetFileName(path);
                                folderMap[folderName] = path;
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // アクセス不可
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNING] {root} のスキャン中にエラー: {ex.Message}");
                }
            }

            Console.WriteLine($"{folderMap.Count} 件の末端フォルダを発見しました。");
            return folderMap;
        }

        public HashSet<string> FindAllLinkedTargets(string? excludeDir = null)
        {
            if (!string.IsNullOrEmpty(excludeDir))
            {
                Console.WriteLine($"'{BaseDirectory}'内の既存ショートカットをスキャンしています（'{Path.GetFileName(excludeDir)}' を除く）...");
            }
            else
            {
                Console.WriteLine($"'{BaseDirectory}'内の既存ショートカットをスキャンしています...");
            }

            var linkedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(BaseDirectory)) return linkedTargets;

            try
            {
                var lnkFiles = Directory.GetFiles(BaseDirectory, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                var lnkFilesToScan = lnkFiles.AsEnumerable();

                if (!string.IsNullOrEmpty(excludeDir))
                {
                    string excludePathStr = Path.GetFullPath(excludeDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    lnkFilesToScan = lnkFilesToScan.Where(f => !Path.GetFullPath(f).StartsWith(excludePathStr, StringComparison.OrdinalIgnoreCase));
                }

                var listToScan = lnkFilesToScan.ToList();
                int total = listToScan.Count;
                int current = 0;

                // COMのWScript.Shellを動的に生成
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    Console.WriteLine("[ERROR] WScript.Shell がシステムに存在しません。");
                    return linkedTargets;
                }
                dynamic shell = Activator.CreateInstance(shellType)!;

                foreach (var lnkPath in listToScan)
                {
                    current++;
                    if (current % 100 == 0 || current == total)
                    {
                        Console.Write($"\rスキャン中: {current}/{total}");
                    }

                    try
                    {
                        dynamic shortcut = shell.CreateShortcut(lnkPath);
                        string targetPath = shortcut.TargetPath;
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            linkedTargets.Add(Path.GetFullPath(targetPath));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\nショートカットの解決に失敗: {lnkPath} ({ex.Message})");
                    }
                }
                Console.WriteLine(); // 改行
            }
            catch (Exception ex)
            {
                Console.WriteLine($"既存リンクのスキャン中に致命的なエラー: {ex.Message}");
            }

            Console.WriteLine($"{linkedTargets.Count} 件のユニークなリンク先を発見しました。");
            return linkedTargets;
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
