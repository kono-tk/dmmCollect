using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace dmmCollect.Scrapers
{
    /// <summary>
    /// 動画（video.dmm.co.jp）のユーザーレビューを取得する。
    ///
    /// 作品ページはレビューを GraphQL（api.video.dmm.co.jp/graphql の UserReviews / ReviewSummary）で読み込んでいる。
    /// ログイン不要で、ネタバレレビュー（isExposure）も全文が入っているため、ブラウザは使わない。
    /// 1回に最大100件取れる（ページの表示は10件ずつ）。並び順 RELEASE_DESC が「新着順」。
    /// レビューには固有の id と投稿日時（秒まで）が付くので、保存済みのレビューがあれば
    /// 新着順に読んで既知の id に達したところで止める（差分取得）。
    /// 公開 API ではないので、項目名が変わったら「取得失敗」として表面化する。
    /// dmmWin の Components/VideoReviewClient.cs も同じ取得をする（変える場合は両方を合わせること）。
    /// </summary>
    public static class VideoReviewClient
    {
        public const string Endpoint = "https://api.video.dmm.co.jp/graphql";
        public const string ReviewerUrlBase = "https://review.dmm.co.jp/review-front/reviewer/list/";
        private const int PerPage = 100;          // API の上限
        private const int MaxPages = 100;         // 想定外の応答で無限にめくらないため（100件 × 100 = 10,000件）
        private const int DelayBetweenPagesMs = 300;

        /// <summary>
        /// 以前のブラウザでの収集で、ネタバレレビューを展開せずに保存した場合の本文（注意書きだけ）。
        /// このような古いファイルは id を持たないため、次の取得で全件を取り直す。
        /// </summary>
        public const string SpoilerPlaceholder = "※このレビューは作品の内容に関する記述が含まれています。";

        private const string Query = @"query UserReviews($id: ID!, $sort: ReviewSort!, $offset: Int!, $limit: Int!, $withSummary: Boolean!) {
  reviewSummary(contentId: $id) @include(if: $withSummary) { average total withCommentTotal }
  reviews(contentId: $id, sort: $sort, limit: $limit, offset: $offset) {
    items { id title rating reviewerId nickname isPurchased comment helpfulCount publishDate }
  }
}";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            http.DefaultRequestHeaders.Add("fanza-device", "BROWSER");
            return http;
        }

        /// <summary>取得結果。Added は新しく加わったレビューの件数、Full は全件を取り直したかどうか。</summary>
        public record Result(ReviewCollection Collection, int Added, bool Full);

        /// <summary>
        /// 作品のレビューを取得する。existing（保存済みのファイル）があり、そのレビューがすべて id を持てば差分取得する。
        /// 差分で件数が合わない（削除された等）場合や full=true の場合は全件を取り直す。取得に失敗した場合は null。
        /// </summary>
        public static async Task<Result?> FetchAsync(string cid, string floor, ReviewCollection? existing, bool full, CancellationToken ct = default)
        {
            try
            {
                return await UpdateAsync(Http, cid, floor, existing, full, ct);
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

        public static async Task<Result> UpdateAsync(HttpClient http, string cid, string floor, ReviewCollection? existing, bool full, CancellationToken ct)
        {
            var result = new ReviewCollection
            {
                Cid = cid,
                SourceUrl = VideoFloor.ContentPageUrl(cid, floor),
                CollectedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
            };

            bool incremental = !full && existing != null && existing.Comments.Count > 0
                               && existing.Comments.All(c => !string.IsNullOrEmpty(c.Id));
            if (incremental)
            {
                var known = existing!.Comments.ToDictionary(c => c.Id, StringComparer.Ordinal);
                var fresh = new List<ReviewComment>();
                bool reachedKnown = false;
                for (int page = 0; page < MaxPages && !reachedKnown; page++)
                {
                    var items = await FetchPageAsync(http, cid, page * PerPage, page == 0 ? result : null, ct);
                    foreach (var item in items)
                    {
                        if (known.TryGetValue(item.Id, out var old))
                        {
                            old.HelpfulCount = item.HelpfulCount;   // 同じページに出た既知のレビューは「参考になった」数だけ新しくする
                            reachedKnown = true;
                        }
                        else if (!reachedKnown)
                        {
                            fresh.Add(item);
                        }
                    }
                    if (items.Count < PerPage) break;
                    if (!reachedKnown) await Task.Delay(DelayBetweenPagesMs, ct);
                }

                result.Comments.AddRange(fresh);
                result.Comments.AddRange(existing.Comments);
                if (result.Comments.Count == result.CommentCount)
                {
                    return new Result(result, fresh.Count, Full: false);
                }
                // 件数が合わない（レビューの削除・非公開など）。全件を取り直す
                result.Comments.Clear();
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int page = 0; page < MaxPages; page++)
            {
                var items = await FetchPageAsync(http, cid, page * PerPage, page == 0 ? result : null, ct);
                foreach (var item in items)
                {
                    if (seen.Add(item.Id)) result.Comments.Add(item);   // 取得中に新着が入ってページの境目がずれた場合の重複
                }
                if (items.Count < PerPage || result.Comments.Count >= result.CommentCount) break;
                await Task.Delay(DelayBetweenPagesMs, ct);
            }

            if (result.Comments.Count != result.CommentCount)
            {
                Console.WriteLine($"[WARNING] コメント件数が一致しません ({cid}: 集計 {result.CommentCount} 件 / 取得 {result.Comments.Count} 件)");
            }
            // 新しく加わった件数。id の無い古いファイルからの取り直しでは、件数の差で数える
            int added;
            if (existing == null) added = result.Comments.Count;
            else if (existing.Comments.Any(c => string.IsNullOrEmpty(c.Id))) added = Math.Max(0, result.Comments.Count - existing.Comments.Count);
            else
            {
                var before = existing.Comments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                added = result.Comments.Count(c => !before.Contains(c.Id));
            }
            return new Result(result, added, Full: true);
        }

        /// <summary>新着順に offset から最大 PerPage 件を取る。summaryTarget を渡すと集計値（平均評価など）も入れる。</summary>
        private static async Task<List<ReviewComment>> FetchPageAsync(
            HttpClient http, string cid, int offset, ReviewCollection? summaryTarget, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["operationName"] = "UserReviews",
                ["query"] = Query,
                ["variables"] = new JsonObject
                {
                    ["id"] = cid,
                    ["sort"] = "RELEASE_DESC",
                    ["offset"] = offset,
                    ["limit"] = PerPage,
                    ["withSummary"] = summaryTarget != null,
                },
            };

            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(Endpoint, content, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("応答を解釈できませんでした");
            if (root["errors"] is JsonArray errors && errors.Count > 0)
            {
                throw new InvalidOperationException($"サイトがエラーを返しました: {errors[0]?["message"]}");
            }
            response.EnsureSuccessStatusCode();

            var data = root["data"] as JsonObject ?? throw new InvalidOperationException("応答に data がありません");
            if (summaryTarget != null)
            {
                // レビューが1件も無い作品は reviewSummary が null
                var summary = data["reviewSummary"] as JsonObject;
                summaryTarget.AverageRating = summary?["average"] is JsonValue avg ? avg.GetValue<double>() : null;
                summaryTarget.RatingCount = summary?["total"]?.GetValue<int>() ?? 0;
                summaryTarget.CommentCount = summary?["withCommentTotal"]?.GetValue<int>() ?? 0;
                if (summaryTarget.RatingCount == 0) summaryTarget.AverageRating = null;
            }

            var list = data["reviews"]?["items"] as JsonArray
                ?? throw new InvalidOperationException("応答に reviews.items がありません");
            var items = new List<ReviewComment>();
            foreach (var node in list)
            {
                if (node is JsonObject review) items.Add(ParseReview(review));
            }
            return items;
        }

        public static ReviewComment ParseReview(JsonObject review)
        {
            string published = review["publishDate"]?.ToString() ?? "";
            string reviewerId = review["reviewerId"]?.ToString() ?? "";
            return new ReviewComment
            {
                Id = review["id"]?.ToString() ?? "",
                Rating = review["rating"]?.GetValue<int>() ?? 0,
                Title = (review["title"]?.ToString() ?? "").Trim(),
                Body = (review["comment"]?.ToString() ?? "").Replace("\r\n", "\n").Trim(),
                Reviewer = review["nickname"]?.ToString() ?? "",
                ReviewerUrl = reviewerId.Length > 0 ? ReviewerUrlBase + reviewerId : "",
                PublishedAt = published,
                Date = DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) : "",
                Purchased = review["isPurchased"]?.GetValue<bool>() ?? false,
                HelpfulCount = review["helpfulCount"]?.GetValue<int>() ?? 0,
            };
        }
    }
}
