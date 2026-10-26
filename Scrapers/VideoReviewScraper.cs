using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Playwright;

namespace dmmCollect.Scrapers
{
    // ユーザーレビュー（コメント）の収集。
    // 作品ページ https://video.dmm.co.jp/av/content/?id={cid} の #review 領域が対象。
    // レビュー一覧は画面内に入ってから GraphQL で遅延読み込みされ、10件ずつのボタン式ページング。
    public partial class VideoScraper
    {
        private const string ReviewItemSelector = "#review [data-e2eid=\"review-item\"]";

        /// <summary>
        /// ネタバレを含むレビューは、本文の代わりにこの注意書きだけが表示され、「レビューを表示する」を
        /// 押すと全文が出る。展開せずに取ると、注意書きだけがコメントとして保存されてしまう。
        /// </summary>
        public const string SpoilerPlaceholder = "※このレビューは作品の内容に関する記述が含まれています。";

        private const string ShowReviewButtonSelector = "button:has-text('レビューを表示する')";

        private static readonly Regex AverageRatingPattern = new(@"平均評価\s*([0-9.]+)\s*点", RegexOptions.Compiled);
        private static readonly Regex RatingCountPattern = new(@"総評価数\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex CommentCountPattern = new(@"[（(]\s*(\d+)\s*件のコメント\s*[）)]", RegexOptions.Compiled);
        private static readonly Regex ReviewDatePattern = new(@"(\d{4}/\d{1,2}/\d{1,2})", RegexOptions.Compiled);
        private static readonly Regex HelpfulCountPattern = new(@"(\d+)\s*人が参考になった", RegexOptions.Compiled);

        /// <summary>
        /// 作品ページからユーザーレビューを収集する。ページ取得自体に失敗した場合は null。
        /// コメント付きレビューが 0 件でも、集計値（平均評価など）を入れたコレクションを返す。
        /// </summary>
        /// <param name="floor">VideoFloor.Av / VideoFloor.Amateur（素人作品は作品ページの URL が違う）</param>
        public async Task<ReviewCollection?> FetchReviewsForItemAsync(string cid, string floor = VideoFloor.Av)
        {
            string url = VideoFloor.ContentPageUrl(cid, floor);
            var result = new ReviewCollection
            {
                Cid = cid,
                SourceUrl = url,
                CollectedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
            };

            try
            {
                // 年齢確認は Cookie で通過する（ログインしていなくてもレビューは閲覧できる）。
                await Context!.AddCookiesAsync(new[]
                {
                    new Cookie { Name = "age_check_done", Value = "1", Domain = ".dmm.co.jp", Path = "/" }
                });

                await using var page = await Context!.NewPageAsync();
                await page.GotoAsync(url, new() { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });

                var review = page.Locator("#review");
                try
                {
                    await review.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 20000 });
                }
                catch (TimeoutException)
                {
                    // ページは開けたがレビュー領域が出ない作品（販売終了・ページ形式が異なる等）は、
                    // 毎回再試行しても結果が変わらないため「コメント0件」として確認済みにする。
                    Console.WriteLine($"[INFO] レビュー領域がありません。コメント0件として扱います (URL: {url})");
                    return result;
                }

                // 画面内に入るまで一覧が読み込まれないため、領域までスクロールする。
                await review.ScrollIntoViewIfNeededAsync();

                ParseSummary(await review.InnerTextAsync(), result);
                if (result.CommentCount == 0)
                {
                    return result;
                }

                try
                {
                    await page.WaitForSelectorAsync(ReviewItemSelector, new() { Timeout = 20000 });
                }
                catch (TimeoutException)
                {
                    Console.WriteLine($"[WARNING] コメント{result.CommentCount}件の表示を確認できませんでした (URL: {url})");
                    return null;
                }

                var parser = new HtmlParser();
                int pageNo = 1;
                while (true)
                {
                    await ExpandSpoilerReviewsAsync(page, review);
                    string html = await review.EvaluateAsync<string>("e => e.outerHTML");
                    var document = await parser.ParseDocumentAsync(html);
                    var items = document.QuerySelectorAll("[data-e2eid=\"review-item\"]");
                    string signature = items.Length > 0 ? ReviewSignature(items[0]) : "";
                    foreach (var item in items)
                    {
                        result.Comments.Add(ParseReviewItem(item));
                    }
                    Console.WriteLine($"  レビュー {pageNo} ページ目: {items.Length} 件 (累計 {result.Comments.Count}/{result.CommentCount})");

                    if (result.Comments.Count >= result.CommentCount) break;

                    // 次ページのボタン（ページ番号表記）を押し、先頭レビューが入れ替わるまで待つ。
                    var next = review.Locator("ul[data-e2eid=\"pagination\"]").First
                        .GetByRole(AriaRole.Button, new() { Name = (pageNo + 1).ToString(), Exact = true });
                    if (await next.CountAsync() == 0 || !await next.IsEnabledAsync()) break;

                    await next.ClickAsync();
                    pageNo++;

                    if (!await WaitForReviewPageChangeAsync(page, signature)) break;
                }

                if (result.Comments.Count != result.CommentCount)
                {
                    Console.WriteLine($"[WARNING] コメント件数が一致しません (表示 {result.CommentCount} 件 / 取得 {result.Comments.Count} 件)");
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] レビュー取得エラー (URL: {url}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 表示中のページにある、折りたたまれたネタバレレビューをすべて展開する。
        /// 展開すると同じ本文の領域に全文が入り、ボタンは「閉じる」に変わる（再クリックの心配はない）。
        /// </summary>
        private static async Task ExpandSpoilerReviewsAsync(IPage page, ILocator review)
        {
            var buttons = review.Locator(ShowReviewButtonSelector);
            if (await buttons.CountAsync() == 0) return;

            await buttons.EvaluateAllAsync<object?>("els => { els.forEach(e => e.click()); return null; }");

            // 展開されるまで待つ（通常は一瞬）。残ったら警告して先へ進む。
            for (int i = 0; i < 20; i++)
            {
                await page.WaitForTimeoutAsync(250);
                if (!(await review.InnerTextAsync()).Contains("このレビューは作品の内容に関する記述が含まれています")) return;
            }
            Console.WriteLine("[WARNING] ネタバレレビューを展開できませんでした（注意書きのまま保存されます）。");
        }

        private static void ParseSummary(string text, ReviewCollection result)
        {
            var avg = AverageRatingPattern.Match(text);
            if (avg.Success && double.TryParse(avg.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
            {
                result.AverageRating = a;
            }

            var ratings = RatingCountPattern.Match(text);
            if (ratings.Success) result.RatingCount = int.Parse(ratings.Groups[1].Value);

            var comments = CommentCountPattern.Match(text);
            if (comments.Success) result.CommentCount = int.Parse(comments.Groups[1].Value);
        }

        private static string ReviewSignature(IElement item)
        {
            var reviewer = item.QuerySelector("a[href*=\"/reviewer/\"]")?.GetAttribute("href") ?? "";
            var title = item.QuerySelector("header span.text-base")?.TextContent ?? "";
            return $"{reviewer}|{title}";
        }

        private static async Task<bool> WaitForReviewPageChangeAsync(IPage page, string previousSignature)
        {
            var parser = new HtmlParser();
            for (int i = 0; i < 40; i++)
            {
                await page.WaitForTimeoutAsync(250);
                string? first = await page.Locator(ReviewItemSelector).First.EvaluateAsync<string?>("e => e.outerHTML");
                if (string.IsNullOrEmpty(first)) continue;

                var el = (await parser.ParseDocumentAsync(first)).QuerySelector("[data-e2eid=\"review-item\"]");
                if (el != null && ReviewSignature(el) != previousSignature) return true;
            }
            Console.WriteLine("[WARNING] 次ページのレビュー表示を待機中にタイムアウトしました。");
            return false;
        }

        private static ReviewComment ParseReviewItem(IElement item)
        {
            var comment = new ReviewComment
            {
                Rating = item.QuerySelectorAll("header img").Count(img =>
                    (img.GetAttribute("src") ?? "").Contains("star/yellow", StringComparison.OrdinalIgnoreCase)),
                Title = (item.QuerySelector("header span.text-base")?.TextContent ?? "").Trim(),
                Body = ExtractBody(item.QuerySelector("div.overflow-hidden"))
            };

            var reviewer = item.QuerySelector("a[href*=\"/reviewer/\"]");
            if (reviewer != null)
            {
                string name = reviewer.TextContent.Trim();
                const string suffix = "さんのレビュー";
                comment.Reviewer = name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;
                comment.ReviewerUrl = reviewer.GetAttribute("href") ?? "";
            }

            string meta = item.QuerySelector("span.text-gray-500")?.TextContent ?? "";
            var date = ReviewDatePattern.Match(meta);
            if (date.Success) comment.Date = date.Groups[1].Value;

            string text = item.TextContent;
            comment.Purchased = text.Contains("購入・利用済み", StringComparison.Ordinal);
            var helpful = HelpfulCountPattern.Match(text);
            if (helpful.Success) comment.HelpfulCount = int.Parse(helpful.Groups[1].Value);

            return comment;
        }

        // 本文は <p> が段落、<br> が空行。textContent だと改行が失われるため要素単位で組み立てる。
        private static string ExtractBody(IElement? body)
        {
            if (body == null) return "";

            var lines = new List<string>();
            foreach (var node in body.ChildNodes)
            {
                if (node is IElement el)
                {
                    if (el.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase)) lines.Add("");
                    else lines.Add(el.TextContent.Trim());
                }
                else if (node.NodeType == NodeType.Text)
                {
                    string t = node.TextContent.Trim();
                    if (t.Length > 0) lines.Add(t);
                }
            }

            return string.Join("\n", lines).Trim();
        }
    }
}
