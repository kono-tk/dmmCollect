using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace dmmCollect
{
    /// <summary>実体フォルダ内の .dcv ファイル1つ。</summary>
    public class ContentFile
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("mtime_ticks")] public long MTimeTicks { get; set; }

        /// <summary>.dcv に埋め込まれた作品の cid（読めなければ null）。</summary>
        [JsonPropertyName("cid")] public string? Cid { get; set; }

        /// <summary>.dcv に埋め込まれたパート番号（読めなければ null）。</summary>
        [JsonPropertyName("part")] public int? Part { get; set; }
    }

    /// <summary>コンテンツ実体フォルダ（.dcv を含むフォルダ）1つ。</summary>
    public class ContentFolder
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";

        /// <summary>フォルダの作品の cid。.dcv の cid の多数決（読めなければ null）。</summary>
        [JsonPropertyName("cid")] public string? Cid { get; set; }

        /// <summary>
        /// 収録作品名（BEST・福袋など）。renames.txt があればその各行、無ければ「01.作品名.dcv」形式のファイル名から。
        /// 同じ作品名が続く行（1作品が複数ファイルに分かれている）はまとめる。収録作品の情報が無ければ空。
        /// </summary>
        [JsonPropertyName("titles")] public List<string> Titles { get; set; } = new();

        /// <summary>
        /// false なら、前回の索引から引き継いだ情報で、今回は確認できていない（ドライブが接続されていない等）。
        /// 接続されていないドライブの作品が一覧から消えないよう、情報は残しておく。
        /// </summary>
        [JsonPropertyName("available")] public bool Available { get; set; } = true;

        [JsonPropertyName("files")] public List<ContentFile> Files { get; set; } = new();
    }

    /// <summary>索引の作成・更新の結果（件数の報告用）。</summary>
    public class ContentIndexBuildStats
    {
        public int Folders { get; set; }
        public int FilesRead { get; set; }
        public int FilesReused { get; set; }
        public int FoldersWithoutCid { get; set; }
        public List<string> UnavailableRoots { get; } = new();
    }

    /// <summary>
    /// コンテンツ索引（BaseDirectory\content_index.json）。cid → コンテンツ実体フォルダの対応表。
    ///
    /// 作品と実体フォルダの対応は、.dcv（中身は MP4）の先頭付近に DMM が埋め込んでいるメタ情報
    /// （{"v":"2","fid":"&lt;cid&gt;",...} / {"part":"1","file_id":"&lt;cid&gt;",...}、古い .dcv は {"file_id":"&lt;cid&gt;",...}）から確定的に求める。
    /// 以前のショートカット（FLAT の .lnk）とタイトルのあいまい照合を置き換えるもの。
    ///
    /// 実体フォルダから作り直せる派生データ。差分更新では、パス・サイズ・更新日時が前回と同じ .dcv は読み直さない。
    /// dmmCollect（収集の最後）と dmmWin（更新ボタン）の両方で使うため、両方に同じ内容を置く。
    /// 片方だけ直さないこと（dmmWin.Tests に両方の一致を確認するテストがある）。
    /// </summary>
    public class ContentIndex
    {
        public const string FileName = "content_index.json";
        public const string RenamesFileName = "renames.txt";
        public const int FormatVersion = 1;

        /// <summary>.dcv のメタ情報を探す範囲。実測では先頭 3KB 以内にある。</summary>
        public const int MetadataReadBytes = 256 * 1024;

        private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN", "System Volume Information",
        };

        private static readonly Regex FidPattern = new("\"fid\":\"([A-Za-z0-9_\\-]+)\"", RegexOptions.Compiled);
        private static readonly Regex PartPattern = new("\"part\":\"(\\d+)\",\"file_id\":\"([A-Za-z0-9_\\-]+)\"", RegexOptions.Compiled);
        // 古い .dcv は {"file_id":"<cid>","service_id":"digital",...} だけを持つ（fid も part も無い）
        private static readonly Regex FileIdPattern = new("\"file_id\":\"([A-Za-z0-9_\\-]+)\"", RegexOptions.Compiled);
        private static readonly Regex NumberedDcvPattern = new(@"^(\d{2})\.(.+)\.dcv$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        [JsonPropertyName("version")] public int Version { get; set; } = FormatVersion;
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
        [JsonPropertyName("roots")] public List<string> Roots { get; set; } = new();
        [JsonPropertyName("folders")] public List<ContentFolder> Folders { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string PathFor(string baseDirectory) => System.IO.Path.Combine(baseDirectory, FileName);

        /// <summary>索引を読む。無い・壊れている場合は null。</summary>
        public static ContentIndex? Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var index = JsonSerializer.Deserialize<ContentIndex>(File.ReadAllText(path), JsonOptions);
                return index?.Version == FormatVersion ? index : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ContentIndex.Load error: {ex.Message}");
                return null;
            }
        }

        /// <summary>一時ファイル経由で保存する（dmmCollect と dmmWin が同時に書いても壊れないように）。</summary>
        public void Save(string path)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>cid → 実体フォルダ。1つの cid に複数フォルダある作品（福袋など）は複数入る。</summary>
        public Dictionary<string, List<ContentFolder>> FoldersByCid()
        {
            var map = new Dictionary<string, List<ContentFolder>>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Folders)
            {
                if (string.IsNullOrEmpty(folder.Cid)) continue;
                if (!map.TryGetValue(folder.Cid, out var list)) map[folder.Cid] = list = new List<ContentFolder>();
                list.Add(folder);
            }
            return map;
        }

        /// <summary>
        /// フォルダ名 → フォルダ名と作品名（ファイル名用に整えたもの）が一致する data.json の作品の cid。
        /// 同じ作品名が複数ある場合は決められないので null（FoldersByWork の福袋の中身の判定に使う）。
        /// </summary>
        public static Func<string, string?> CidForFolderName(IEnumerable<JsonObject> data)
        {
            var byTitle = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var e in data)
            {
                string cid = ItemIdentity.Of(e);
                string title = e["title"]?.ToString() ?? "";
                if (cid.Length == 0 || title.Length == 0) continue;
                string key = FileSanitizer.SanitizeFilename(title);
                byTitle[key] = byTitle.TryGetValue(key, out var existing) && !string.Equals(existing, cid, StringComparison.OrdinalIgnoreCase) ? null : cid;
            }
            return name => byTitle.TryGetValue(name, out var cid) ? cid : null;
        }

        /// <summary>
        /// 作品の cid → 実体フォルダ。基本は .dcv の cid だが、福袋の中身は別扱いにする。
        ///
        /// 福袋を買うと、福袋そのものと中身の各作品が data.json に別々の作品として載り、中身は作品ごとのフォルダに
        /// ダウンロードされる。ところが中身のフォルダの .dcv には福袋の cid が記録されている。.dcv の cid だけで判定すると、
        /// 中身の作品の実体がすべて福袋に吸い込まれ、個々の作品が一覧から消えてしまう。
        /// そこで、.dcv の cid が複数のフォルダを持つ場合に限り、フォルダ名と作品名が一致する作品（cidForFolderName が返す）
        /// があれば、そのフォルダはその作品のものとする。
        /// </summary>
        /// <param name="cidForFolderName">フォルダ名 → そのフォルダ名と作品名が一致する作品の cid（一意に決まらなければ null）</param>
        public Dictionary<string, List<ContentFolder>> FoldersByWork(Func<string, string?>? cidForFolderName = null)
        {
            var folderCount = Folders
                .Where(f => !string.IsNullOrEmpty(f.Cid))
                .GroupBy(f => f.Cid!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var map = new Dictionary<string, List<ContentFolder>>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Folders)
            {
                if (string.IsNullOrEmpty(folder.Cid)) continue;

                string cid = folder.Cid;
                if (cidForFolderName != null && folderCount[cid] > 1)
                {
                    string? component = cidForFolderName(System.IO.Path.GetFileName(folder.Path));
                    if (!string.IsNullOrEmpty(component)) cid = component;
                }

                if (!map.TryGetValue(cid, out var list)) map[cid] = list = new List<ContentFolder>();
                list.Add(folder);
            }
            return map;
        }

        /// <summary>
        /// 作品の cid → 作品単位のファイル（comments.json など）を置く実体フォルダ。dmmCollect（書く側）と dmmWin（読む側）で
        /// 同じフォルダを選ぶための決まり（作品とフォルダの対応は FoldersByWork と同じ）:
        ///   ・確認できていない（Available=false）フォルダは除く
        ///   ・existingFileName を指定すると、そのファイルが既にあるフォルダを優先する
        ///   ・無ければパス順で最初のフォルダ（1つの作品に複数フォルダあっても、常に同じフォルダになる）
        /// </summary>
        public Dictionary<string, string> WorkFolderByCid(string? existingFileName = null, Func<string, string?>? cidForFolderName = null)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cid, folders) in FoldersByWork(cidForFolderName))
            {
                var paths = folders.Where(f => f.Available).Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                if (paths.Count == 0) continue;
                string? withFile = existingFileName == null
                    ? null
                    : paths.FirstOrDefault(p => File.Exists(System.IO.Path.Combine(p, existingFileName)));
                map[cid] = withFile ?? paths[0];
            }
            return map;
        }

        /// <summary>
        /// roots 配下の実体フォルダを走査して索引を作る。previous があれば、変わっていない .dcv は読み直さない。
        /// 見つからない root（ドライブ未接続など）の配下は、previous の情報を Available=false で引き継ぐ。
        /// </summary>
        public static ContentIndex Build(
            IReadOnlyList<string> roots, ContentIndex? previous, out ContentIndexBuildStats stats, Action<string>? progress = null)
        {
            stats = new ContentIndexBuildStats();
            var index = new ContentIndex
            {
                Roots = roots.ToList(),
                UpdatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            };

            var previousFiles = new Dictionary<string, ContentFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in previous?.Folders ?? new List<ContentFolder>())
            {
                foreach (var file in folder.Files)
                {
                    previousFiles[System.IO.Path.Combine(folder.Path, file.Name)] = file;
                }
            }

            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                {
                    stats.UnavailableRoots.Add(root);
                    continue;
                }

                foreach (string dir in EnumerateDirectories(root))
                {
                    string[] dcvPaths;
                    try
                    {
                        dcvPaths = Directory.GetFiles(dir, "*.dcv");
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (dcvPaths.Length == 0 || !seenFolders.Add(dir)) continue;

                    var folder = new ContentFolder { Path = dir };
                    foreach (string dcvPath in dcvPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        var info = new FileInfo(dcvPath);
                        var file = new ContentFile { Name = info.Name, Size = info.Length, MTimeTicks = info.LastWriteTimeUtc.Ticks };

                        // 前回 cid を読めなかったファイルは、解析の改善を反映するため読み直す
                        if (previousFiles.TryGetValue(dcvPath, out var old) && old.Size == file.Size && old.MTimeTicks == file.MTimeTicks
                            && !string.IsNullOrEmpty(old.Cid))
                        {
                            file.Cid = old.Cid;
                            file.Part = old.Part;
                            stats.FilesReused++;
                        }
                        else
                        {
                            (file.Cid, file.Part) = ReadDcvMetadata(dcvPath);
                            stats.FilesRead++;
                            if (stats.FilesRead % 100 == 0) progress?.Invoke($"コンテンツ索引: .dcv を {stats.FilesRead} 件読み込みました");
                        }
                        folder.Files.Add(file);
                    }

                    folder.Cid = MajorityCid(folder.Files);
                    folder.Titles = CollectedTitles(dir, folder.Files.Select(f => f.Name).ToList());
                    if (folder.Cid == null) stats.FoldersWithoutCid++;
                    index.Folders.Add(folder);
                }
            }

            // 見つからなかった root 配下は、前回の情報を「未確認」として残す
            foreach (var old in previous?.Folders ?? new List<ContentFolder>())
            {
                if (seenFolders.Contains(old.Path)) continue;
                if (!stats.UnavailableRoots.Any(r => IsUnder(old.Path, r))) continue;
                old.Available = false;
                index.Folders.Add(old);
            }

            index.Folders.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            stats.Folders = index.Folders.Count;
            return index;
        }

        private static IEnumerable<string> EnumerateDirectories(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                yield return dir;

                string[] children;
                try
                {
                    children = Directory.GetDirectories(dir);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (string child in children)
                {
                    if (!ExcludedDirectories.Contains(System.IO.Path.GetFileName(child))) stack.Push(child);
                }
            }
        }

        private static bool IsUnder(string path, string root)
        {
            string r = root.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(path.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>.dcv の先頭付近から cid とパート番号を読む。見つからなければ (null, null)。</summary>
        public static (string? Cid, int? Part) ReadDcvMetadata(string dcvPath)
        {
            try
            {
                byte[] buffer = new byte[MetadataReadBytes];
                int read;
                using (var stream = new FileStream(dcvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                return ParseDcvMetadata(Encoding.Latin1.GetString(buffer, 0, read));
            }
            catch (Exception)
            {
                return (null, null);
            }
        }

        /// <summary>.dcv の先頭部分（Latin1 で文字列化したもの）から cid とパート番号を取り出す。</summary>
        public static (string? Cid, int? Part) ParseDcvMetadata(string head)
        {
            var part = PartPattern.Match(head);
            if (part.Success)
            {
                return (part.Groups[2].Value, int.Parse(part.Groups[1].Value));
            }
            var fid = FidPattern.Match(head);
            if (fid.Success) return (fid.Groups[1].Value, null);
            var fileId = FileIdPattern.Match(head);
            return fileId.Success ? (fileId.Groups[1].Value, null) : (null, null);
        }

        private static string? MajorityCid(List<ContentFile> files) =>
            files.Where(f => !string.IsNullOrEmpty(f.Cid))
                 .GroupBy(f => f.Cid!, StringComparer.OrdinalIgnoreCase)
                 .OrderByDescending(g => g.Count())
                 .ThenBy(g => g.Key, StringComparer.Ordinal)
                 .Select(g => g.Key)
                 .FirstOrDefault();

        /// <summary>
        /// 収録作品名。renames.txt（1行目は見出し）があればその各行、無ければ「01.作品名.dcv」形式のファイル名から。
        /// 同じ作品名が続く行はまとめる。作品名が1種類しか無い（1作品の分割）なら空。
        /// </summary>
        public static List<string> CollectedTitles(string folder, IReadOnlyList<string> dcvNames)
        {
            List<string> titles = new();
            string renames = System.IO.Path.Combine(folder, RenamesFileName);
            try
            {
                if (File.Exists(renames))
                {
                    titles = File.ReadAllLines(renames)
                        .Select(l => l.Trim().TrimStart('﻿'))
                        .Where(l => l.Length > 0)
                        .Skip(1)
                        .ToList();
                }
            }
            catch (Exception)
            {
                titles = new();
            }

            if (titles.Count == 0)
            {
                var numbered = dcvNames.Select(n => NumberedDcvPattern.Match(n)).ToList();
                if (dcvNames.Count >= 2 && numbered.All(m => m.Success))
                {
                    titles = numbered.OrderBy(m => int.Parse(m.Groups[1].Value)).Select(m => m.Groups[2].Value.Trim()).ToList();
                }
            }

            // 1作品が複数ファイルに分かれている場合の重複（連続する同名）をまとめる
            var collapsed = new List<string>();
            foreach (string t in titles)
            {
                if (collapsed.Count == 0 || collapsed[^1] != t) collapsed.Add(t);
            }
            return collapsed.Distinct().Count() >= 2 ? collapsed : new List<string>();
        }
    }
}
