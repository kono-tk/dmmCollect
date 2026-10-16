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
        [JsonPropertyName("rating")] public int Rating { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("reviewer")] public string Reviewer { get; set; } = "";
        [JsonPropertyName("reviewer_url")] public string ReviewerUrl { get; set; } = "";
        [JsonPropertyName("date")] public string Date { get; set; } = "";
        [JsonPropertyName("purchased")] public bool Purchased { get; set; }
        [JsonPropertyName("helpful_count")] public int HelpfulCount { get; set; }
    }

    /// <summary>
    /// 1作品分のレビュー収集結果。コンテンツ実体フォルダ直下の comments.json にそのまま保存する。
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
        public const int FormatVersion = 1;

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// folder 直下に comments.json を書き込み、書いたパスを返す。
        /// フォルダの更新日時は書き込み前の値へ戻す（ショートカットの日付の既定値に
        /// フォルダの最終更新日時を使っているため、ファイルを足しただけで並びが変わるのを防ぐ）。
        /// </summary>
        public static string Save(string folder, ReviewCollection collection)
        {
            string path = Path.Combine(folder, FileName);
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
