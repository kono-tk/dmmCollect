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
            // GUI 等で標準出力をリダイレクトして取り込む際に日本語が化けないよう UTF-8 で出力する
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* リダイレクト不可環境は無視 */ }

            // 引数の簡易解析
            string mode = "video";
            bool debug = false;
            bool headless = true;
            int maxPages = 1;
            bool showHelp = false;
            bool syncOnly = false;
            string? commentsCid = null;
            bool commentsOnly = false;
            bool recheckComments = false;
            bool buildCommentIndex = false;
            bool analyzeComments = false;
            bool dryRun = false;
            bool verifyLlm = false;
            string? dumpAspects = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-m" || args[i] == "--mode")
                {
                    if (i + 1 < args.Length) mode = args[++i];
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
                else if (args[i] == "--comments-only")
                {
                    commentsOnly = true;
                }
                else if (args[i] == "--analyze-comments")
                {
                    analyzeComments = true;
                }
                else if (args[i] == "--dump-aspects")
                {
                    if (i + 1 < args.Length) dumpAspects = args[++i];
                }
                else if (args[i] == "--verify-llm")
                {
                    verifyLlm = true;
                }
                else if (args[i] == "--dry-run")
                {
                    dryRun = true;
                }
                else if (args[i] == "--build-comment-index")
                {
                    buildCommentIndex = true;
                }
                else if (args[i] == "--recheck-comments")
                {
                    recheckComments = true;
                }
                else if (args[i] == "--comments-cid")
                {
                    if (i + 1 < args.Length) commentsCid = args[++i];
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

            var dataManager = new DataManager(dataPath, backupOnInit: true, performerAliasesPath: config.PerformerAliasesPath);

            // === コメント解析（観点の評判・星と本文の食い違い）のみ ===
            if (analyzeComments)
            {
                if (mode != "video")
                {
                    Console.WriteLine("[FATAL] コメント解析は video モードでのみ使用できます。");
                    return 1;
                }
                return CommentCollector.AnalyzeAll(config, dataManager, dryRun, verifyLlm, dumpAspects);
            }

            // === コメント索引（全文検索用 SQLite）の作成のみ ===
            if (buildCommentIndex)
            {
                if (mode != "video")
                {
                    Console.WriteLine("[FATAL] コメント索引は video モードでのみ使用できます。");
                    return 1;
                }
                return CommentCollector.BuildIndex(config, dataManager);
            }

            // === コメント収集のみ（--comments-only / --comments-cid）===
            if (commentsOnly || commentsCid != null)
            {
                if (mode != "video")
                {
                    Console.WriteLine("[FATAL] コメント収集は video モードでのみ使用できます。");
                    return 1;
                }
                await using var commentScraper = new VideoScraper(new Dictionary<string, string>(), headless, 0, config.VideoDownloadDirectory, debug);
                await commentScraper.InitializeAsync();
                return await CommentCollector.RunAsync(commentScraper, config, dataManager, commentsCid, recheckComments);
            }

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
                        ["detail_page_base"] = config.Config.login.urls.detail_page_base ?? "https://www.dmm.co.jp/digital/videoa/-/detail/=/cid={cid}/",
                        ["library_url"] = config.Config.books.library_url ?? "https://book.dmm.co.jp/shelf/?tab=library&page={page_num}"
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
                        
                        // マイライブラリの全ページを1回巡回し、画像・CID・購入日をまとめて更新する。
                        // （以前はここで FLAT.html と、タイトルのキーワードごとの検索結果 HTML を保存していたが、
                        //   キーワードフォルダの廃止に伴い不要になった）
                        Console.WriteLine("=== マイライブラリの全ページを巡回（画像・CID・購入日） ===");
                        if (await scraper.SearchAndExpandAsync(null))
                        {
                            await scraper.ProcessAllPagesAsync(imageDir, dataManager);
                        }
                        else
                        {
                            Console.WriteLine("[ERROR] 全コンテンツの取得に失敗しました。");
                            return 1;
                        }

                        // 不足している詳細情報の自動取得
                        await scraper.UpdateAllDetailsAsync(dataManager, imageDir);

                        // 関連タグの自動収集
                        await scraper.CollectRelatedTagsBatchAsync(dataManager);

                        // コンテンツ実体を持つ作品のユーザーレビュー収集（確認済みはスキップ。--recheck-comments で再収集）
                        await CommentCollector.RunAsync(scraper, config, dataManager, null, recheckComments);
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

            // === 同期処理部（FLAT フォルダのショートカット） ===
            // video モードのみ .dcv リンク同期処理を行う
            if (mode == "video")
            {
                Console.WriteLine("=== ショートカット同期処理を開始します ===");
                try
                {
                    var scanner = new DirectoryScanner(baseDir, config.TargetRoots);
                    var shortcutMgr = new ShortcutManager();

                    // コンテンツ実体（.dcv を含むフォルダ）すべてのショートカットを FLAT に作成/更新する
                    RunFlatMode(scanner, shortcutMgr, dataManager);

                    Console.WriteLine("=== 全ての処理が正常に完了しました ===");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FATAL] ショートカット同期処理中にエラーが発生しました: {ex.Message}");
                    return 1;
                }
            }
            else
            {
                Console.WriteLine($"=== {mode} モードではショートカット同期処理をスキップします ===");
                Console.WriteLine("=== 全ての処理が正常に完了しました ===");
            }


            return 0;
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
                // リンク先が存在するか確認し、リンク切れなら削除して再作成する
                string? existingTarget = shortcutMgr.GetTargetPath(linkPathStr);
                if (existingTarget != null && !Directory.Exists(existingTarget))
                {
                    Console.WriteLine($"    [RELINK] リンク切れを検出: '{Path.GetFileName(linkPathStr)}' (旧: {existingTarget})");
                    Console.WriteLine($"             -> 再作成: '{Path.GetFileName(targetFolder)}'");
                    File.Delete(linkPathStr);
                    shortcutMgr.CreateShortcut(linkPathStr, targetFolder);
                    shortcutMgr.SetFileTimestamp(linkPathStr, targetDt);
                }
                else
                {
                    DateTime currentMTime = File.GetLastWriteTime(linkPathStr);
                    if (currentMTime != targetDt)
                    {
                        Console.WriteLine($"    [UPDATE] タイムスタンプを更新: {Path.GetFileName(linkPathStr)}");
                        shortcutMgr.SetFileTimestamp(linkPathStr, targetDt);
                    }
                }
            }
            else
            {
                shortcutMgr.CreateShortcut(linkPathStr, targetFolder);
                shortcutMgr.SetFileTimestamp(linkPathStr, targetDt);
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
            Console.WriteLine("【その他】");
            Console.WriteLine("  --sync-only             スクレイピングをスキップし、ショートカットの同期処理のみ実行します。");
            Console.WriteLine("                          ※ [video] モード。コンテンツ実体のショートカットを FLAT フォルダに作成/更新します。");
            Console.WriteLine("  --comments-only         コンテンツ実体を持つ作品のユーザーレビュー（コメント）収集だけを実行して終了します。");
            Console.WriteLine("                          ※ [video] モードのみ。ログイン不要。通常実行でもスクレイピング後に収集します。");
            Console.WriteLine("                          1作品ごとに comments.json（実体フォルダ）と data.json を保存するため、");
            Console.WriteLine("                          停止しても再実行で続きから再開します。Ctrl+C か BaseDirectory に");
            Console.WriteLine("                          stop_comments.txt を置くと作品の切れ目で停止します。");
            Console.WriteLine("  --build-comment-index   実体フォルダの comments.json から全文検索用の SQLite 索引（BaseDirectory\\comments.sqlite）を");
            Console.WriteLine("                          作成/更新して終了します。収集時にも1作品ごとに自動更新されます。");
            Console.WriteLine("  --analyze-comments      コメントから観点の評判（演技が好評など）と、星評価と本文の食い違いを求めて");
            Console.WriteLine("                          data.json に書き込んで終了します（辞書とルールの解析で数秒）。--dry-run で書き込まずに結果だけ表示。");
            Console.WriteLine("  --dump-aspects <file>   --analyze-comments と併用（辞書調整用）。観点ごとの極性判定を1行1件の JSON で書き出します。");
            Console.WriteLine("  --verify-llm            --analyze-comments と併用。食い違いの候補をローカル LLM（ollama）に読ませ、星と逆向きだと確認できたものだけ残します。");
            Console.WriteLine("                          判定は BaseDirectory\\comment_verdicts.json に保存され、同じ本文は再判定しません（Ctrl+C で止めても続きから再開）。");
            Console.WriteLine("  --recheck-comments      確認済みの作品もコメントを再収集します（更新確認用）。");
            Console.WriteLine("  --comments-cid <cid>    指定した作品1件だけコメントを収集して終了します（確認済みでも収集）。");
            Console.WriteLine("  -h, --help              このヘルプメッセージを表示して終了します。");
        }
    }
}
