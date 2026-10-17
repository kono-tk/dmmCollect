using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace dmmCollect
{
    /// <summary>
    /// LLM が本文だけを読んで付けた判定（positive / negative / mixed）の保存先。
    /// BaseDirectory\comment_verdicts.json に「本文のハッシュ → 判定」で持つ。dmmWin も同じファイルを読む。
    /// コメントの内容が変わるとハッシュも変わるので、古い判定が別の内容に当たることはない。
    /// </summary>
    public class CommentVerdictStore
    {
        public const string FileName = "comment_verdicts.json";

        private readonly string _path;
        public Dictionary<string, string> Verdicts { get; } = new(StringComparer.Ordinal);

        public CommentVerdictStore(string baseDirectory)
        {
            _path = Path.Combine(baseDirectory, FileName);
            if (!File.Exists(_path)) return;

            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
                if (loaded != null)
                {
                    foreach (var (key, value) in loaded) Verdicts[key] = value;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] {FileName} を読めませんでした（判定をやり直します）: {ex.Message}");
            }
        }

        /// <summary>一時ファイル経由で保存する（途中で止まっても壊れない）。</summary>
        public void Save()
        {
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Verdicts, options), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
    }

    /// <summary>
    /// ローカル LLM（ollama）に、コメント本文が肯定的か否定的かを判定させる。
    /// 星の数は渡さない（星を渡すと、その向きに引きずられて食い違いを見逃すため）。
    /// </summary>
    public sealed class CommentVerifier : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _model;

        private static readonly JsonObject Schema = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["text_sentiment"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(
                        CommentAnalyzer.VerdictPositive, CommentAnalyzer.VerdictNegative, CommentAnalyzer.VerdictMixed),
                },
            },
            ["required"] = new JsonArray("text_sentiment"),
        };

        public string Model => _model;

        public CommentVerifier(LlmConfig llm)
        {
            // Windows では localhost が IPv6 を先に試して最初の通信で約2秒失うため、IPv4 に正規化する
            _endpoint = llm.endpoint.TrimEnd('/').Replace("//localhost:", "//127.0.0.1:", StringComparison.OrdinalIgnoreCase);
            _model = llm.model;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(30, llm.timeout_seconds)) };
        }

        /// <summary>ollama に届き、モデルが入っているか。</summary>
        public async Task<string?> CheckAsync(CancellationToken ct)
        {
            try
            {
                var json = JsonNode.Parse(await _http.GetStringAsync($"{_endpoint}/api/tags", ct));
                var models = json?["models"] as JsonArray;
                bool found = models?.Any(m => m?["name"]?.ToString() == _model || m?["model"]?.ToString() == _model) == true;
                return found ? null : $"ollama にモデル {_model} がありません";
            }
            catch (Exception ex)
            {
                return $"ollama に接続できません（{_endpoint}）: {ex.Message}";
            }
        }

        /// <summary>positive / negative / mixed のいずれか。応答を解釈できなければ null。</summary>
        public async Task<string?> JudgeAsync(CommentInput comment, CancellationToken ct)
        {
            string text = string.IsNullOrWhiteSpace(comment.Title) ? comment.Body : $"{comment.Title}\n{comment.Body}";
            if (text.Length > 1500) text = text[..1500];

            var request = new JsonObject
            {
                ["model"] = _model,
                ["stream"] = false,
                ["think"] = false,
                ["format"] = Schema.DeepClone(),
                ["prompt"] = "次の動画レビュー本文の全体的な評価が肯定的(positive)か否定的(negative)か、" +
                             "両方(mixed)かを答えてください。星の数は考慮せず本文だけで判断すること。\n\n本文:\n" + text,
                ["options"] = new JsonObject { ["temperature"] = 0 },
            };

            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{_endpoint}/api/generate", content, ct);
            response.EnsureSuccessStatusCode();

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var inner = JsonNode.Parse(body?["response"]?.ToString() ?? "");
            string? verdict = inner?["text_sentiment"]?.ToString();
            return verdict is CommentAnalyzer.VerdictPositive or CommentAnalyzer.VerdictNegative or CommentAnalyzer.VerdictMixed
                ? verdict
                : null;
        }

        public void Dispose() => _http.Dispose();
    }
}
