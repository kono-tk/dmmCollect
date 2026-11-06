using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace dmmCollect
{
    /// <summary>ユーザーレビュー1件（コメント付きのもの）。</summary>
    public class ReviewComment
    {
        /// <summary>サイトのレビュー id（video の API で取得したもの。差分取得で既知のレビューを見分ける）。books と古いファイルには無い。</summary>
        [JsonPropertyName("id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string Id { get; set; } = "";
        [JsonPropertyName("rating")] public int Rating { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("reviewer")] public string Reviewer { get; set; } = "";
        [JsonPropertyName("reviewer_url")] public string ReviewerUrl { get; set; } = "";
        [JsonPropertyName("date")] public string Date { get; set; } = "";
        /// <summary>投稿日時（ISO 8601、秒まで。video の API で取得したもの）。books と古いファイルには無い。</summary>
        [JsonPropertyName("published_at")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string PublishedAt { get; set; } = "";
        [JsonPropertyName("purchased")] public bool Purchased { get; set; }
        [JsonPropertyName("helpful_count")] public int HelpfulCount { get; set; }
    }

    /// <summary>
    /// 1作品分のレビュー収集結果。video はコンテンツ実体フォルダ直下の comments.json、
    /// books は BaseDirectory\comments\&lt;cid&gt;.json にそのまま保存する。
    /// dmmWin の閲覧画面もこのファイルを読むため、項目名を変える場合は両方を合わせること。
    /// </summary>
    public class ReviewCollection
    {
        [JsonPropertyName("version")] public int Version { get; set; } = CommentStore.FormatVersion;
        [JsonPropertyName("cid")] public string Cid { get; set; } = "";
        [JsonPropertyName("source_url")] public string SourceUrl { get; set; } = "";
        [JsonPropertyName("collected_at")] public string CollectedAt { get; set; } = "";
        [JsonPropertyName("average_rating")] public double? AverageRating { get; set; }
        [JsonPropertyName("rating_count")] public int RatingCount { get; set; }
        [JsonPropertyName("comment_count")] public int CommentCount { get; set; }
        [JsonPropertyName("comments")] public List<ReviewComment> Comments { get; set; } = new();
    }

    public static class CommentStore
    {
        public const string FileName = "comments.json";
        /// <summary>2: video のレビューが id と published_at を持ち、新着順に並ぶ（差分取得に対応）。</summary>
        public const int FormatVersion = 2;

        /// <summary>books のレビューを置くフォルダ（BaseDirectory 直下）。書籍には作品ごとのフォルダが無いため、まとめて置く。</summary>
        public const string BooksFolderName = "comments";

        /// <summary>books の作品のレビューのファイル（BaseDirectory\comments\&lt;cid&gt;.json）。dmmWin も同じ場所を読む。</summary>
        public static string BookCommentPath(string baseDirectory, string cid) =>
            Path.Combine(baseDirectory, BooksFolderName, cid + ".json");

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>folder 直下に comments.json を書き込み、書いたパスを返す（video のコンテンツ実体フォルダ）。</summary>
        public static string Save(string folder, ReviewCollection collection) =>
            SaveFile(Path.Combine(folder, FileName), collection);

        /// <summary>
        /// path に書き込み、書いたパスを返す。フォルダが無ければ作る。
        /// フォルダの更新日時は書き込み前の値へ戻す（コンテンツ実体フォルダの日付を、ファイルを足しただけで変えないため）。
        /// </summary>
        /// <summary>保存済みのレビューのファイルを読む。無い・読めない場合は null。</summary>
        public static ReviewCollection? Load(string path)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<ReviewCollection>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] レビューのファイルを読めません（取り直します）: {path} ({ex.Message})");
                return null;
            }
        }

        public static string SaveFile(string path, ReviewCollection collection)
        {
            string folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            DateTime folderMTimeUtc = Directory.GetLastWriteTimeUtc(folder);

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(collection, WriteOptions), new System.Text.UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);

            try
            {
                Directory.SetLastWriteTimeUtc(folder, folderMTimeUtc);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] フォルダ更新日時の復元に失敗しました: {ex.Message}");
            }

            return path;
        }
    }
}
