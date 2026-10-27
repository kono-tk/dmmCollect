using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace dmmCollect.Scrapers
{
    /// <summary>
    /// 電子書籍（book.dmm.co.jp）のユーザーレビューを取得する。
    ///
    /// 作品ページはレビューを /ajax/review/getjson/ への POST（フォーム形式）で読み込んでいる。
    /// ログイン不要で JSON が返り、ネタバレレビューも全文が入っているため、ブラウザは使わない
    /// （動画は「レビューを表示する」を押して展開する必要がある）。
    /// 1回に返るのは最大10件なので、コメント件数に達するまでページをめくる。
    /// 公開 API ではないので、項目名が変わったら「取得失敗」として表面化する。
    /// </summary>
    public static class BookReviewClient
    {
        public const string Endpoint = "https://book.dmm.co.jp/ajax/review/getjson/";
        private const int PerPage = 10;
        private const int DelayBetweenPagesMs = 300;

        // ページ数の上限（想定外の応答で無限にめくらないため）。10件 × 200 = 2,000件
        private const int MaxPages = 200;

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            handler.CookieContainer.Add(new Cookie("age_check_done", "1", "/", ".dmm.co.jp"));
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            return http;
        }

        /// <summary>
        /// 作品のレビューをすべて取得する。取得に失敗した場合は null。
        /// コメント付きレビューが 0 件でも、集計値（平均評価など）を入れたコレクションを返す。
        /// </summary>
        /// <param name="sourceUrl">作品ページの URL（comments.json の source_url に記録する）</param>
        public static async Task<ReviewCollection?> FetchAsync(string cid, string sourceUrl, CancellationToken ct = default)
        {
            var result = new ReviewCollection
            {
                Cid = cid,
                SourceUrl = sourceUrl,
                CollectedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
            };

            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int page = 1; page <= MaxPages; page++)
                {
                    var root = await FetchPageAsync(cid, page, ct);
                    if (page == 1) ParseSummary(root, result);

                    var list = root["list"] as JsonArray;
                    if (list == null || list.Count == 0) break;

                    int added = 0;
                    foreach (var node in list)
                    {
                        if (node is not JsonObject review) continue;
                        string id = review["review_review_id"]?.ToString() ?? "";
                        if (id.Length > 0 && !seen.Add(id)) continue;   // ページの境目で重複した場合
                        result.Comments.Add(ParseReview(review));
                        added++;
                    }

                    if (added == 0 || result.Comments.Count >= result.CommentCount) break;
                    await Task.Delay(DelayBetweenPagesMs, ct);
                }

                if (result.Comments.Count != result.CommentCount)
                {
                    Console.WriteLine($"[WARNING] コメント件数が一致しません (表示 {result.CommentCount} 件 / 取得 {result.Comments.Count} 件)");
                }
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] レビュー取得エラー ({cid}): {ex.Message}");
                return null;
            }
        }

        private static async Task<JsonObject> FetchPageAsync(string cid, int page, CancellationToken ct)
        {
            // release_desc（投稿の新しい順）でめくる。参考になった順は投票で順位が動き、めくる途中で抜けや重複が起きうる
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["content_id"] = cid,
                ["sort"] = "release_desc",
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["per_page"] = PerPage.ToString(CultureInfo.InvariantCulture),
                ["include_distribution"] = "0",
                ["is_spoiler"] = "0",
            });
            using var response = await Http.PostAsync(Endpoint, content, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException("応答を解釈できませんでした");
        }

        /// <summary>集計値。review_posting_cnt = 評価数（星だけの投稿を含む）、review_comment_cnt = コメント付きの件数。</summary>
        internal static void ParseSummary(JsonObject root, ReviewCollection result)
        {
            if (!root.ContainsKey("review_posting_cnt") || !root.ContainsKey("review_comment_cnt"))
            {
                throw new InvalidOperationException("応答に件数がありません（仕様が変わった可能性があります）");
            }

            result.RatingCount = ToInt(root["review_posting_cnt"]);
            result.CommentCount = ToInt(root["review_comment_cnt"]);
            result.AverageRating = result.RatingCount > 0 ? ToDouble(root["review_evaluate_avg"]) : null;
        }

        internal static ReviewComment ParseReview(JsonObject review)
        {
            string reviewerId = review["review_reviewer_id"]?.ToString() ?? "";
            return new ReviewComment
            {
                Rating = ToInt(review["review_star"]),
                Title = CleanText(review["title"]?.ToString()),
                Body = CleanText(review["text"]?.ToString()),
                Reviewer = review["nickname"]?.ToString() ?? "",
                ReviewerUrl = reviewerId.Length > 0 ? $"https://review.dmm.co.jp/review-front/reviewer/list/{reviewerId}" : "",
                Date = review["release_date"]?.ToString() ?? "",
                Purchased = review["purchase_date"] is JsonValue v && !string.IsNullOrEmpty(v.ToString()),
                HelpfulCount = ToInt(review["review_reference_yes"]),
            };
        }

        private static readonly Regex BrPattern = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TagPattern = new(@"<[^>]+>", RegexOptions.Compiled);

        /// <summary>本文の改行は &lt;br&gt;。改行に直し、残りのタグを除いて文字参照を戻す（動画の comments.json と同じ形にする）。</summary>
        internal static string CleanText(string? html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string text = BrPattern.Replace(html, "\n");
            text = TagPattern.Replace(text, "");
            return WebUtility.HtmlDecode(text).Replace("\r\n", "\n").Trim();
        }

        private static int ToInt(JsonNode? node)
        {
            if (node == null) return 0;
            try { return node.GetValue<int>(); }
            catch { return int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0; }
        }

        private static double? ToDouble(JsonNode? node)
        {
            if (node == null) return null;
            try { return node.GetValue<double>(); }
            catch { return double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null; }
        }
    }
}
