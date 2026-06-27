using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using dmmCollect.Scrapers;

namespace dmmCollect
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {

            // 引数の簡易解析
            string mode = "video";
            var keywords = new List<string>();
            bool renewLnk = false;
            bool lostChild = false;
            bool debug = false;
            bool headless = true;
            int maxPages = 1;
            bool showHelp = false;
            bool syncOnly = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-m" || args[i] == "--mode")
                {
                    if (i + 1 < args.Length) mode = args[++i];
                }
                else if (args[i] == "-k" || args[i] == "--keyword")
                {
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        keywords.Add(args[++i]);
                    }
                }
                else if (args[i] == "--renew-lnk")
                {
                    renewLnk = true;
                }
                else if (args[i] == "--lost-child")
                {
                    lostChild = true;
                }
                else if (args[i] == "--debug")
                {
                    debug = true;
                }
                else if (args[i] == "--headful")
                {
                    headless = false;
                }
                else if (args[i] == "-p" || args[i] == "--pages")
                {
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                    {
                        maxPages = p == 0 ? 999 : p;
                        i++;
                    }
                }
                else if (args[i] == "-h" || args[i] == "--help")
                {
                    showHelp = true;
                }
                else if (args[i] == "--sync-only")
                {
                    syncOnly = true;
                }
            }

            if (showHelp)
            {
                ShowHelp();
                return 0;
            }

            Console.WriteLine($"=== dmmCollect (C# 統合版) 起動 (Mode: {mode}) ===");

            var scriptDir = AppDomain.CurrentDomain.BaseDirectory;
            ConfigManager config;
            try
            {
                config = new ConfigManager(mode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] 設定ファイルの読み込みに失敗しました: {ex.Message}");
                return 1;
            }

            string baseDir = config.BaseDirectory;
            string imageDir = config.ImageDirectory;
            string dataPath = config.DataPath;

            Console.WriteLine($"BaseDirectory: {baseDir}");
            Console.WriteLine($"ImageDirectory: {imageDir}");
            Console.WriteLine($"DataPath: {dataPath}");

            var dataManager = new DataManager(dataPath, backupOnInit: true);

            // === スクレイピング部 (dmmLogin) ===
            if (!syncOnly)
            {
                try
                {
                    if (mode == "dlsite")
                {
                    string? dlId = Environment.GetEnvironmentVariable("DLSITE_LOGIN_ID");
                    string? dlPw = Environment.GetEnvironmentVariable("DLSITE_PASSWORD");
                    if (string.IsNullOrEmpty(dlId) || string.IsNullOrEmpty(dlPw))
                    {
                        Console.WriteLine("[FATAL] 環境変数 DLSITE_LOGIN_ID と DLSITE_PASSWORD を設定してください。");
                        return 1;
                    }

                    var urls = new Dictionary<string, string>
                    {
                        ["login_url"] = config.Config.dlsite.login_url ?? AppConstants.DLSITE_LIBRARY_URL
                    };

                    await using var scraper = new DLsiteScraper(urls, headless, 500, debug);
                    await scraper.InitializeAsync();
                    await scraper.ScrapeDlsiteAsync(dataManager, imageDir);
                }
                else
                {
                    string? dmmId = Environment.GetEnvironmentVariable("DMM_LOGIN_ID");
                    string? dmmPw = Environment.GetEnvironmentVariable("DMM_PASSWORD");
                    if (string.IsNullOrEmpty(dmmId) || string.IsNullOrEmpty(dmmPw))
                    {
                        Console.WriteLine("[FATAL] 環境変数 DMM_LOGIN_ID と DMM_PASSWORD を設定してください。");
                        return 1;
                    }

                    var urls = new Dictionary<string, string>
                    {
                        ["age_check"] = config.Config.login.urls.age_check ?? "https://www.dmm.co.jp/age_check/=/?rurl=https%3A%2F%2Fwww.dmm.co.jp%2Ftop%2F",
                        ["top"] = config.Config.login.urls.top ?? "https://www.dmm.co.jp/top/",
                        ["login_page"] = config.Config.login.urls.login_page ?? "https://accounts.dmm.co.jp/service/login/password/=/path=https%3A%2F%2Fwww.dmm.co.jp%2Ftop%2F",
                        ["my_library_search"] = config.Config.login.urls.my_library_search ?? "https://www.dmm.co.jp/digital/-/mylibrary/search/",
                        ["detail_page_base"] = config.Config.login.urls.detail_page_base ?? "https://www.dmm.co.jp/digital/videoa/-/detail/=/cid={{cid}}/",
                        ["library_url"] = config.Config.books.library_url ?? "https://book.dmm.co.jp/shelf/?tab=library&page={{page_num}}"
                    };

                    if (mode == "books")
                    {
                        string contentDir = Path.Combine(baseDir, "books");
                        await using var scraper = new BooksScraper(urls, headless, 500, debug);
                        await scraper.InitializeAsync();
                        await scraper.LoginAsync(dmmId, dmmPw);
                        await scraper.ScrapeAndDownloadBooksAsync(dataManager, imageDir, 1, maxPages, contentDir);
                    }
                    else if (mode == "dojin")
                    {
                        urls["library_url"] = config.Config.dojin.library_url ?? "https://www.dmm.co.jp/dc/-/mylibrary/";
                        await using var scraper = new DojinScraper(urls, headless, 500, debug);
                        await scraper.InitializeAsync();
                        await scraper.LoginAsync(dmmId, dmmPw);
                        await scraper.ScrapeDojinAsync(dataManager, imageDir);
                    }
                    else // video
                    {
                        await using var scraper = new VideoScraper(urls, headless, 500, config.VideoDownloadDirectory, debug);
                        await scraper.InitializeAsync();
                        await scraper.LoginAsync(dmmId, dmmPw);
                        await scraper.NavigateAndWaitForVideoLibraryAsync();
                        
                        string? targetKeyword = keywords.Count > 0 && keywords[0] != "-" ? keywords[0] : null;
                        await scraper.SearchAndExpandAsync(targetKeyword);
                        
                        await scraper.UpdateCidsAndDownloadImagesAsync(imageDir, dataManager);
                        await scraper.CollectPurchaseDatesAsync(dataManager);

                        // 不足している詳細情報の自動取得
                        await scraper.UpdateAllDetailsAsync(dataManager, imageDir);

                        // 関連タグの自動収集
                        await scraper.CollectRelatedTagsBatchAsync(dataManager);
                    }
                }

                    Console.WriteLine("=== スクレイピング処理が正常に完了しました ===");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FATAL] スクレイピング処理中に致命的なエラーが発生しました: {ex.Message}");
                    Console.WriteLine($"[FATAL] スタックトレース:\n{ex}");
                    return 1;
                }
            }
            else
            {
                Console.WriteLine("=== --sync-only 指定のためスクレイピングをスキップします ===");
            }

            // === 同期処理部 (dmmSearch) ===
            Console.WriteLine("=== ショートカット同期処理を開始します ===");
            try
            {
                var scanner = new DirectoryScanner(baseDir, config.TargetRoots);
                var shortcutMgr = new ShortcutManager();

                if (lostChild)
                {
                    RunLostChildMode(scanner, shortcutMgr, dataManager);
                }
                else if (keywords.Count == 0 || (keywords.Count > 0 && keywords[0] == "-"))
                {
                    RunFlatMode(scanner, shortcutMgr, dataManager);
                }
                else
                {
                    RunStandardSearchMode(scanner, shortcutMgr, dataManager, keywords, renewLnk, config.MaxAltLength);
                }

                Console.WriteLine("=== 全ての処理が正常に完了しました ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] ショートカット同期処理中にエラーが発生しました: {ex.Message}");
                return 1;
            }

            return 0;
        }

        private static void RunStandardSearchMode(DirectoryScanner scanner, ShortcutManager shortcutMgr, DataManager dataManager, List<string> keywords, bool renewLnk, int maxAltLen)
        {
            var keywordDirs = scanner.FindKeywordDirs(keywords.Count > 0 ? keywords : null);
            if (keywordDirs.Count == 0)
            {
                Console.WriteLine("[WARNING] 処理対象のキーワードディレクトリが見つかりませんでした。");
                return;
            }

            var targetFolders = scanner.FindTargetFolders();

            // data.json からサニタイズ済みタイトルをキーにする日付情報の辞書を作成
            var dataMap = new Dictionary<string, (string? purchaseDate, bool notfound)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in dataManager.Data)
            {
                string? title = entry["title"]?.ToString();
                if (!string.IsNullOrEmpty(title))
                {
                    string sanitizedTitle = FileSanitizer.SanitizeFilename(title);
                    string? purchaseDate = entry.ContainsKey("purchase_date") ? entry["purchase_date"]?.ToString() : null;
                    bool notfound = entry.ContainsKey("notfound") && entry["notfound"] != null && entry["notfound"]!.AsValue().TryGetValue(out bool nf) && nf;
                    
                    dataMap[sanitizedTitle] = (purchaseDate, notfound);
                }
            }

            foreach (var kd in keywordDirs)
            {
                Console.WriteLine($"\n--- キーワード '{Path.GetFileName(kd)}' の処理を開始 ---");

                var htmlFile = Path.Combine(kd, $"{Path.GetFileName(kd)}.html");
                if (File.Exists(htmlFile))
                {
                    CreateSearchTxtFromHtml(htmlFile, maxAltLen);
                }

                string searchTxtPath = Path.Combine(kd, AppConstants.SEARCH_TXT_FILENAME);
                if (!File.Exists(searchTxtPath))
                {
                    Console.WriteLine($"  {AppConstants.SEARCH_TXT_FILENAME} が見つからないため、ショートカット作成をスキップします。");
                    continue;
                }

                if (renewLnk)
                {
                    Console.WriteLine($"  --renew-lnk 指定のため、'{Path.GetFileName(kd)}' 内の既存 .lnk ファイルを削除します。");
                    foreach (var file in Directory.GetFiles(kd, "*.lnk"))
                    {
                        File.Delete(file);
                    }
                }

                var sanitizedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(searchTxtPath))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        sanitizedKeywords.Add(FileSanitizer.SanitizeFilename(line.Trim()));
                    }
                }

                Console.WriteLine($"  {sanitizedKeywords.Count} 件のキーワードを元にリンクを作成/更新します。");
                foreach (var keyword in sanitizedKeywords)
                {
                    foreach (var folder in targetFolders)
                    {
                        if (folder.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            CreateOrUpdateShortcut(kd, folder.Value, shortcutMgr, dataMap);
                        }
                    }
                }
            }
        }

        private static void CreateOrUpdateShortcut(string keywordDir, string targetFolder, ShortcutManager shortcutMgr, Dictionary<string, (string? purchaseDate, bool notfound)> dataMap)
        {
            string folderName = Path.GetFileName(targetFolder);
            string linkNameBase = FileSanitizer.SanitizeFilename($"{folderName}_link");
            string linkPathStr = FileSanitizer.FormatFilename(
                Path.Combine(keywordDir, $"{linkNameBase}.lnk"),
                100
            );

            string sanitizedTitle = FileSanitizer.SanitizeFilename(folderName);
            // デフォルトのタイムスタンプとして実体フォルダの最終更新日時を使用する（未来の日付になるのを防ぐ）
            DateTime targetDt = Directory.GetLastWriteTime(targetFolder);

            if (dataMap.TryGetValue(sanitizedTitle, out var entryData))
            {
                if (entryData.notfound)
                {
                    targetDt = new DateTime(1980, 1, 2);
                }
                else if (!string.IsNullOrEmpty(entryData.purchaseDate))
                {
                    string[] possibleFormats = new[] { "yyyy年MM月dd日 HH:mm", "yyyy/MM/dd", "yyyy-MM-dd" };
                    foreach (var fmt in possibleFormats)
                    {
                        if (DateTime.TryParseExact(entryData.purchaseDate, fmt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime parsedDate))
                        {
                            targetDt = parsedDate;
                            break;
                        }
                    }
                }
            }

            if (File.Exists(linkPathStr))
            {
                DateTime currentMTime = File.GetLastWriteTime(linkPathStr);
                if (currentMTime != targetDt)
                {
                    Console.WriteLine($"    [UPDATE] タイムスタンプを更新: {Path.GetFileName(linkPathStr)}");
                    shortcutMgr.SetFileTimestamp(linkPathStr, targetDt);
                }
            }
            else
            {
                shortcutMgr.CreateShortcut(linkPathStr, targetFolder);
                shortcutMgr.SetFileTimestamp(linkPathStr, targetDt);
            }
        }

        private static void CreateSearchTxtFromHtml(string htmlFile, int maxAltLen)
        {
            string searchTxtFile = Path.Combine(Path.GetDirectoryName(htmlFile)!, AppConstants.SEARCH_TXT_FILENAME);
            if (File.Exists(searchTxtFile) && File.GetLastWriteTime(searchTxtFile) >= File.GetLastWriteTime(htmlFile))
            {
                return;
            }

            Console.WriteLine($"  HTMLを処理中: {Path.GetFileName(htmlFile)}");
            try
            {
                string htmlContent = File.ReadAllText(htmlFile);
                var parser = new HtmlParser();
                var document = parser.ParseDocument(htmlContent);

                var imgTags = document.QuerySelectorAll("span.mySearchList_item_pict img[alt][src]");
                var altTexts = new List<string>();

                foreach (var img in imgTags)
                {
                    string? alt = img.GetAttribute("alt")?.Trim();
                    if (!string.IsNullOrEmpty(alt))
                    {
                        string subAlt = alt.Substring(0, Math.Min(alt.Length, maxAltLen));
                        altTexts.Add(FileSanitizer.SanitizeFilename(subAlt));
                    }
                }

                File.WriteAllLines(searchTxtFile, altTexts);
                Console.WriteLine($"  -> {Path.GetFileName(searchTxtFile)} を作成/更新しました ({altTexts.Count}件)。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERROR] HTMLのパースに失敗しました ({Path.GetFileName(htmlFile)}): {ex.Message}");
            }
        }

        private static void RunLostChildMode(DirectoryScanner scanner, ShortcutManager shortcutMgr, DataManager dataManager)
        {
            Console.WriteLine("--- 孤立コンテンツ検索モードを開始 ---");
            string lostFoundDir = Path.Combine(scanner.BaseDirectory, AppConstants.LOST_FOUND_DIR_NAME);
            
            var mainLinkedTargets = scanner.FindAllLinkedTargets(excludeDir: lostFoundDir);

            if (Directory.Exists(lostFoundDir))
            {
                Console.WriteLine($"'{AppConstants.LOST_FOUND_DIR_NAME}' 内のクリーンアップを開始します...");
                int deletedCount = 0;

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    var lnkFiles = Directory.GetFiles(lostFoundDir, "*.lnk");

                    foreach (var lnkPath in lnkFiles)
                    {
                        try
                        {
                            dynamic shortcut = shell.CreateShortcut(lnkPath);
                            string targetPath = shortcut.TargetPath;

                            if (!string.IsNullOrEmpty(targetPath) && mainLinkedTargets.Contains(Path.GetFullPath(targetPath)))
                            {
                                File.Delete(lnkPath);
                                Console.WriteLine($"    [DELETE] 不要なリンクを削除: {Path.GetFileName(lnkPath)} (ターゲット: {Path.GetFileName(targetPath)})");
                                deletedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"ショートカットの解決/削除に失敗: {lnkPath} ({ex.Message})");
                        }
                    }
                }

                if (deletedCount > 0)
                {
                    Console.WriteLine($"{deletedCount} 件の不要なリンクを削除しました。");
                }
                else
                {
                    Console.WriteLine("削除対象の不要なリンクはありませんでした。");
                }
            }

            var allTargetFolders = scanner.FindTargetFolders().Values.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allTargetFolders.Count == 0)
            {
                Console.WriteLine("[WARNING] 検索対象のフォルダが見見つかりませんでした。");
                return;
            }

            Console.WriteLine($"{allTargetFolders.Count} 件のターゲットフォルダをスキャンしました。");
            
            var lostChildren = allTargetFolders.Where(f => !mainLinkedTargets.Contains(f)).OrderBy(f => f).ToList();
            if (lostChildren.Count == 0)
            {
                Console.WriteLine("新たに発見された孤立コンテンツはありませんでした。");
                return;
            }

            Console.WriteLine($"{lostChildren.Count} 件の孤立したコンテンツを新たに発見しました。");
            Directory.CreateDirectory(lostFoundDir);

            // data.json から日付辞書を作成
            var dataMap = new Dictionary<string, (string? purchaseDate, bool notfound)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in dataManager.Data)
            {
                string? title = entry["title"]?.ToString();
                if (!string.IsNullOrEmpty(title))
                {
                    string sanitizedTitle = FileSanitizer.SanitizeFilename(title);
                    string? purchaseDate = entry.ContainsKey("purchase_date") ? entry["purchase_date"]?.ToString() : null;
                    bool notfound = entry.ContainsKey("notfound") && entry["notfound"] != null && entry["notfound"]!.AsValue().TryGetValue(out bool nf) && nf;
                    dataMap[sanitizedTitle] = (purchaseDate, notfound);
                }
            }

            foreach (var targetFolder in lostChildren)
            {
                CreateOrUpdateShortcut(lostFoundDir, targetFolder, shortcutMgr, dataMap);
            }
        }

        private static void RunFlatMode(DirectoryScanner scanner, ShortcutManager shortcutMgr, DataManager dataManager)
        {
            Console.WriteLine("--- フラットモードを開始 ---");
            var contentFolders = scanner.FindAllDcvParentFolders();
            if (contentFolders.Count == 0)
            {
                Console.WriteLine("[WARNING] ショートカットを作成する対象のコンテンツフォルダが見つかりませんでした。");
                return;
            }

            string flatDir = Path.Combine(scanner.BaseDirectory, AppConstants.FLAT_DIR_NAME);
            Directory.CreateDirectory(flatDir);
            Console.WriteLine($"'{AppConstants.FLAT_DIR_NAME}' にショートカットを作成/更新します...");

            // data.json からサニタイズ済みタイトルをキーにする日付情報の辞書を作成
            var dataMap = new Dictionary<string, (string? purchaseDate, bool notfound)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in dataManager.Data)
            {
                string? title = entry["title"]?.ToString();
                if (!string.IsNullOrEmpty(title))
                {
                    string sanitizedTitle = FileSanitizer.SanitizeFilename(title);
                    string? purchaseDate = entry.ContainsKey("purchase_date") ? entry["purchase_date"]?.ToString() : null;
                    bool notfound = entry.ContainsKey("notfound") && entry["notfound"] != null && entry["notfound"]!.AsValue().TryGetValue(out bool nf) && nf;
                    
                    dataMap[sanitizedTitle] = (purchaseDate, notfound);
                }
            }

            foreach (var targetFolder in contentFolders)
            {
                CreateOrUpdateShortcut(flatDir, targetFolder, shortcutMgr, dataMap);
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine("使用方法: dmmCollect.exe [オプション]");
            Console.WriteLine();
            Console.WriteLine("オプション:");
            Console.WriteLine("  -m, --mode <mode>       実行モードを指定します (video, books, dojin, dlsite) (デフォルト: video)");
            Console.WriteLine();
            Console.WriteLine("【スクレイピング用オプション】");
            Console.WriteLine("  -p, --pages <num>       最大取得ページ数を指定します。");
            Console.WriteLine("                          ※ [books] モードでのみ有効 (デフォルト: 1, 0を指定すると全ページ収集)");
            Console.WriteLine("  --debug                 Playwrightなどのデバッグログやコンソール出力を有効にします。");
            Console.WriteLine("                          ※ 全モードで有効");
            Console.WriteLine("  --headful               ブラウザを表示モードで起動します。");
            Console.WriteLine("                          ※ 全モードで有効");
            Console.WriteLine();
            Console.WriteLine("【同期・ショートカット作成用オプション】");
            Console.WriteLine("  -k, --keyword <word>    処理対象のキーワードを指定します (複数指定可)。");
            Console.WriteLine("                          ※ [video] モードでのスクレイピング絞り込み、およびショートカット作成時の");
            Console.WriteLine("                            キーワード選択に使用します。'-' を指定するとフラットモードで実行します。");
            Console.WriteLine("  --renew-lnk             同期処理時に対象キーワードフォルダ内の既存 .lnk ファイルを削除して再作成します。");
            Console.WriteLine("                          ※ ショートカット同期処理で有効");
            Console.WriteLine("  --lost-child            孤立したコンテンツ（どのキーワードフォルダにも紐づかないフォルダ）を検索し、");
            Console.WriteLine("                          '_lost_found' フォルダにショートカットを作成します。");
            Console.WriteLine("                          ※ ショートカット同期処理で有効");
            Console.WriteLine();
            Console.WriteLine("【その他】");
            Console.WriteLine("  --sync-only             スクレイピングをスキップし、ショートカットの同期処理のみ実行します。");
            Console.WriteLine("  -h, --help              このヘルプメッセージを表示して終了します。");
        }
    }
}
