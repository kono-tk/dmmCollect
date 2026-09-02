using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace dmmCollect.Scrapers
{
    public class BaseScraper : IAsyncDisposable
    {
        protected IPlaywright? PlaywrightInstance { get; set; }
        protected IBrowser? Browser { get; set; }
        protected IBrowserContext? Context { get; set; }
        public IPage? Page { get; protected set; }

        protected readonly Dictionary<string, string> Urls;
        protected readonly bool Headless;
        protected readonly int SlowMo;
        protected readonly bool IsDebugMode;
        protected readonly bool Verbose;
        protected readonly string DownloadTempDir = Path.Combine(Path.GetTempPath(), "dmmCollect_downloads");

        public BaseScraper(Dictionary<string, string> urls, bool headless, int slowMo, bool isDebugMode, bool verbose = false)
        {
            Urls = urls;
            Headless = headless;
            SlowMo = slowMo;
            IsDebugMode = isDebugMode;
            Verbose = verbose;
        }

        public async Task InitializeAsync()
        {
            PlaywrightInstance = await Playwright.CreateAsync();

            Directory.CreateDirectory(DownloadTempDir);

            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = Headless,
                SlowMo = SlowMo,
                DownloadsPath = DownloadTempDir,
                Args = new[] { "--ignore-certificate-errors", "--ignore-ssl-errors", "--ignore-certificate-errors-spki-list" }
            };

            Browser = await PlaywrightInstance.Chromium.LaunchAsync(launchOptions);
            
            var contextOptions = new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36",
                IgnoreHTTPSErrors = true
            };

            Context = await Browser.NewContextAsync(contextOptions);
            Page = await Context.NewPageAsync();

            if (IsDebugMode)
            {
                Page.Console += (sender, msg) =>
                {
                    Console.WriteLine($"JS Console [{msg.Type}]: {msg.Text}");
                };
            }
        }

        public async Task CloseAsync()
        {
            if (Browser != null)
            {
                await Browser.CloseAsync();
                Console.WriteLine("ブラウザを閉じました。");
            }
            PlaywrightInstance?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
        }

        public async Task SaveDebugArtifactsAsync(IPage page, string prefix = "")
        {
            string ssName = string.IsNullOrEmpty(prefix) ? AppConstants.ERROR_SCREENSHOT_FILENAME : $"{prefix}_{AppConstants.ERROR_SCREENSHOT_FILENAME}";
            string htmlName = string.IsNullOrEmpty(prefix) ? AppConstants.ERROR_HTML_FILENAME : $"{prefix}_{AppConstants.ERROR_HTML_FILENAME}";

            string ssPath = Path.GetFullPath(ssName);
            string htmlPath = Path.GetFullPath(htmlName);

            try
            {
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = ssPath });
                Console.WriteLine($"デバッグ用スクリーンショットを保存しました: {ssPath}");

                string content = await page.ContentAsync();
                await File.WriteAllTextAsync(htmlPath, content);
                Console.WriteLine($"デバッグ用HTMLを保存しました: {htmlPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"デバッグ成果物の保存中にエラーが発生しました: {ex.Message}");
            }
        }

        public async Task<bool> CloseAdPopupIfPresentAsync(IPage? page = null)
        {
            var targetPage = page ?? Page;
            if (targetPage == null) return false;

            string[] popupSelectors = new[]
            {
                AppConstants.AD_POPUP_SELECTOR,
                "#popupWrapper",
                "#popupContent"
            };

            foreach (var selector in popupSelectors)
            {
                try
                {
                    var adPopup = targetPage.Locator(selector).First;
                    if (await adPopup.IsVisibleAsync())
                    {
                        Console.WriteLine($"広告popupを検出しました（セレクター: {selector}）。自動的に閉じます...");

                        try
                        {
                            string content = await targetPage.ContentAsync();
                            await File.WriteAllTextAsync("debug_popup.html", content);
                            Console.WriteLine("Popup検出時のHTMLを保存: debug_popup.html");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"デバッグHTML保存中にエラー: {ex.Message}");
                        }

                        string[] closeSelectors = new[]
                        {
                            $"{selector} button[aria-label*=\"閉じる\"]",
                            $"{selector} button",
                            $"{selector} .close",
                            $"{selector} [class*=\"close\"]",
                            AppConstants.AD_POPUP_CLOSE_BUTTON_SELECTOR,
                            "button[aria-label*=\"閉じる\"]",
                            "button:has-text(\"×\")",
                            "button:has-text(\"閉じる\")",
                            ".close",
                            "[class*=\"close\"]"
                        };

                        foreach (var closeSelector in closeSelectors)
                        {
                            try
                            {
                                var closeButton = targetPage.Locator(closeSelector).First;
                                if (await closeButton.IsVisibleAsync())
                                {
                                    Console.WriteLine($"Close buttonを発見: {closeSelector}");
                                    await closeButton.ClickAsync();
                                    await targetPage.WaitForTimeoutAsync(1000);

                                    if (!await adPopup.IsVisibleAsync())
                                    {
                                        Console.WriteLine("広告popupを閉じました。");
                                        return true;
                                    }
                                }
                            }
                            catch
                            {
                                // continue
                            }
                        }

                        try
                        {
                            Console.WriteLine("Escapeキーでpopupを閉じる試行...");
                            await targetPage.Keyboard.PressAsync("Escape");
                            await targetPage.WaitForTimeoutAsync(1000);
                            if (!await adPopup.IsVisibleAsync())
                            {
                                Console.WriteLine("Escapeキーで広告popupを閉じました。");
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Escapeキー試行中にエラー: {ex.Message}");
                        }

                        Console.WriteLine("適切なclose buttonが見つかりませんでした。");
                        return false;
                    }
                }
                catch
                {
                    // continue
                }
            }

            return false;
        }

        public async Task LoginAsync(string loginId, string password)
        {
            if (Page == null) throw new InvalidOperationException("Page is not initialized.");

            // DMMはトップページ遷移時にドメインを www.dmm.co.jp から www.fanza.jp へ
            // 誘導する場合がある（サイトのリブランディング）。設定上のドメインだけで
            // 厳密一致させるとタイムアウトするため、既知のドメインいずれでも同一パスに
            // 到達すれば遷移完了とみなす。
            var topUrlPattern = BuildTopPageRegex(Urls["top"]);

            try
            {
                Console.WriteLine("DMMトップページにアクセスし、年齢確認を通過します...");
                await Page.GotoAsync(Urls["age_check"]);

                await CloseAdPopupIfPresentAsync();

                // DMM側の年齢確認ページは表示文言が複数パターン存在する（"はい" / "18歳以上なので進む" 等、
                // A/Bテストと思われる）。文言に関わらず、遷移先href（declared=yes）で判定することで両対応する。
                await Page.Locator("a[href*='declared=yes']").First.ClickAsync();
                await Page.WaitForURLAsync(topUrlPattern);

                await CloseAdPopupIfPresentAsync();

                Console.WriteLine("ログインページに直接移動します...");
                await Page.GotoAsync(Urls["login_page"]);

                await CloseAdPopupIfPresentAsync();

                await Page.Locator(AppConstants.LOGIN_ID_SELECTOR).FillAsync(loginId);
                await Page.Locator(AppConstants.PASSWORD_SELECTOR).FillAsync(password);
                await Page.Locator(AppConstants.LOGIN_BUTTON_SELECTOR).ClickAsync();
                await Page.WaitForURLAsync(topUrlPattern);

                await CloseAdPopupIfPresentAsync();

                Console.WriteLine("ログイン成功。");
            }
            catch (Exception)
            {
                // ログイン過程での失敗は原因切り分けが難しいため、失敗時点の画面を必ず保存する。
                await SaveDebugArtifactsAsync(Page, "login");
                throw;
            }
        }

        private static Regex BuildTopPageRegex(string configuredTopUrl)
        {
            string path = new Uri(configuredTopUrl).AbsolutePath.TrimEnd('/');
            string pathPattern = Regex.Escape(path);
            return new Regex($@"(dmm\.co\.jp|fanza\.jp){pathPattern}/?(\?.*)?$");
        }
    }
}
