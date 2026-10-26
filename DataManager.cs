using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace dmmCollect
{
    public class DataManager
    {
        public string DataPath { get; private set; }
        public List<JsonObject> Data { get; private set; } = new();
        private DateTime? _lastMTime;

        private readonly Dictionary<string, int> _titleIndex = new();
        private readonly Dictionary<string, int> _subtitleIndex = new();

        // books / dojin の照合に使う識別子の索引。サブタイトルだけで照合していた頃は、
        // サイト側の表記が変わる（「〜」→「～」、改題で「File1」が入る等）と別作品として
        // 追加され、同じ作品が2件・ファイルも2本になっていた。
        private readonly Dictionary<string, int> _idIndex = new();
        private readonly Dictionary<string, HashSet<string>> _aliasGroups = new(StringComparer.OrdinalIgnoreCase);
        // 出演者エイリアスの外部共有ファイル（モード別 performer_aliases_{mode}.json）。null/未存在なら data.json 内にフォールバック。
        private readonly string? _performerAliasesPath;

        private static readonly Regex TitlePrefixPattern = new(@"^(HD|8K|HQ|4K|FULLHD|SD|SP|PPV|Blu-ray|BluRay|DVD|\s)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public DataManager(string dataPath, bool backupOnInit = false, string? performerAliasesPath = null)
        {
            DataPath = dataPath;
            _performerAliasesPath = performerAliasesPath;
            if (backupOnInit)
            {
                CreateBackup();
            }
            Load();
        }

        public void Load()
        {
            if (string.IsNullOrEmpty(DataPath) || !File.Exists(DataPath))
            {
                Console.WriteLine($"DataManager: Data file not found at '{DataPath}'. Starting empty.");
                Data = new List<JsonObject>();
                return;
            }

            try
            {
                string jsonContent = File.ReadAllText(DataPath);
                var rootNode = JsonNode.Parse(jsonContent);

                if (rootNode is JsonObject obj && obj.ContainsKey("items"))
                {
                    var itemsArray = obj["items"]?.AsArray();
                    Data = itemsArray?.Select(node => node?.AsObject()).OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                }
                else if (rootNode is JsonArray arr)
                {
                    Data = arr.Select(node => node?.AsObject()).OfType<JsonObject>().ToList();
                }
                else
                {
                    Data = new List<JsonObject>();
                }

                Data = Data.Where(item => item != null).ToList();

                LoadPerformerAliases(rootNode);

                _lastMTime = File.GetLastWriteTime(DataPath);
                BuildIndexes();
                Console.WriteLine($"DataManager: Loaded {Data.Count} items from '{DataPath}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading data.json: {ex.Message}");
                throw;
            }
        }

        // 出演者エイリアスを読み込む。外部共有ファイル（performer_aliases_{mode}.json）を優先し、
        // 無ければ data.json 内の "performer_aliases"（旧形式・移行期の保険）にフォールバックする。
        private void LoadPerformerAliases(JsonNode? dataRootNode)
        {
            _aliasGroups.Clear();

            if (!string.IsNullOrEmpty(_performerAliasesPath) && File.Exists(_performerAliasesPath))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(_performerAliasesPath));
                    PopulateAliasGroups(node);
                    Console.WriteLine($"DataManager: Loaded performer aliases from '{_performerAliasesPath}' ({_aliasGroups.Count} names)");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DataManager: Failed to read performer aliases file '{_performerAliasesPath}': {ex.Message}");
                }
            }

            // フォールバック: data.json 内の performer_aliases
            if (dataRootNode is JsonObject rootObj && rootObj.ContainsKey("performer_aliases"))
            {
                PopulateAliasGroups(rootObj["performer_aliases"]);
            }
        }

        // エイリアス定義（{ "主名": [別名...] } 形式、または [[名前A,名前B], ...] 形式）から _aliasGroups を構築する。
        private void PopulateAliasGroups(JsonNode? aliasesNode)
        {
            if (aliasesNode is JsonObject aliasesObj)
            {
                foreach (var property in aliasesObj)
                {
                    string primaryName = property.Key;
                    var aliasList = property.Value?.AsArray()?.Select(n => n?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new List<string>();

                    var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryName };
                    foreach (var alias in aliasList)
                    {
                        group.Add(alias);
                    }

                    foreach (var name in group)
                    {
                        _aliasGroups[name] = group;
                    }
                }
            }
            else if (aliasesNode is JsonArray aliasesArr)
            {
                foreach (var itemNode in aliasesArr)
                {
                    if (itemNode is JsonArray groupArr)
                    {
                        var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var n in groupArr)
                        {
                            string name = n?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(name)) group.Add(name);
                        }
                        foreach (var name in group)
                        {
                            _aliasGroups[name] = group;
                        }
                    }
                }
            }
        }

        public HashSet<string> GetAliases(string name)
        {
            if (_aliasGroups.TryGetValue(name, out var group))
            {
                return group;
            }
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        }

        public bool ReloadIfUpdated()
        {
            if (!string.IsNullOrEmpty(DataPath) && File.Exists(DataPath))
            {
                DateTime currentMTime = File.GetLastWriteTime(DataPath);
                if (_lastMTime == null || currentMTime > _lastMTime)
                {
                    Load();
                    return true;
                }
            }
            return false;
        }

        public void ReloadData()
        {
            Load();
        }

        private void CreateBackup()
        {
            BackupHelper.CreateBackup(DataPath);
        }

        public bool SaveData(bool createBackup = true)
        {
            try
            {
                if (string.IsNullOrEmpty(DataPath))
                {
                    Console.WriteLine("Data path is not set, cannot save data");
                    return false;
                }

                if (createBackup)
                {
                    CreateBackup();
                }

                JsonObject rootObj = new JsonObject();
                if (File.Exists(DataPath))
                {
                    try
                    {
                        string content = File.ReadAllText(DataPath);
                        var parsed = JsonNode.Parse(content);
                        if (parsed is JsonObject obj)
                        {
                            rootObj = obj;
                        }
                    }
                    catch { /* ignore parsing errors */ }
                }

                var itemsArray = new JsonArray();
                foreach (var item in Data)
                {
                    var clone = JsonNode.Parse(item.ToJsonString());
                    if (clone != null) itemsArray.Add(clone);
                }
                rootObj["items"] = itemsArray;

                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                string jsonString = JsonSerializer.Serialize(rootObj, options);
                // 途中で止まっても data.json が壊れないよう、一時ファイルへ書いてから置き換える。
                string tempPath = DataPath + ".tmp";
                File.WriteAllText(tempPath, jsonString);
                File.Move(tempPath, DataPath, overwrite: true);

                _lastMTime = File.GetLastWriteTime(DataPath);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving data to {DataPath}: {ex.Message}");
                return false;
            }
        }

        private void BuildIndexes()
        {
            _titleIndex.Clear();
            _subtitleIndex.Clear();
            _idIndex.Clear();

            for (int i = 0; i < Data.Count; i++)
            {
                var item = Data[i];
                string? title = item["title"]?.ToString();
                if (!string.IsNullOrEmpty(title))
                {
                    string normTitle = NormalizeTitle(title);
                    _titleIndex[normTitle] = i;

                }

                string? subtitle = item["subtitle"]?.ToString();
                if (!string.IsNullOrEmpty(subtitle))
                {
                    _subtitleIndex[subtitle] = i;
                }

                string id = ItemIdentity.Of(item);
                if (id.Length > 0)
                {
                    _idIndex.TryAdd(id, i);      // 重複があれば先頭を正とする
                }
            }
        }

        public static string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            string replaced = TitlePrefixPattern.Replace(title, "");
            // 各種ハイフン・ダッシュ・マイナス記号を半角ハイフンに統一
            replaced = Regex.Replace(replaced, @"[‐‑‒–—―⁓〜～−－]", "-");
            return replaced;
        }

        public JsonObject? FindEntry(string normalizedTitle)
        {
            if (_titleIndex.TryGetValue(normalizedTitle, out int index))
            {
                return Data[index];
            }
            return null;
        }

        public JsonObject? FindBookEntry(string subtitle)
        {
            if (_subtitleIndex.TryGetValue(subtitle, out int index))
            {
                return Data[index];
            }
            return null;
        }

        public bool UpdateOrAddEntry(JsonObject newData, bool isBook = false)
        {
            string keyField = isBook ? "subtitle" : "title";
            string? title = newData[keyField]?.ToString();
            if (string.IsNullOrEmpty(title)) return false;

            JsonObject? entry;
            Dictionary<string, int> indexMap;
            string key;

            if (isBook)
            {
                // 識別子があれば先に識別子で照合する。見つからなければ従来どおりサブタイトルで照合する。
                string id = ItemIdentity.Of(newData);
                entry = id.Length > 0 && _idIndex.TryGetValue(id, out int idIndex) ? Data[idIndex] : null;
                entry ??= FindBookEntry(title);
                indexMap = _subtitleIndex;
                key = title;
            }
            else
            {
                key = NormalizeTitle(title);
                entry = FindEntry(key);
                indexMap = _titleIndex;
            }

            bool updated = false;
            if (entry != null)
            {
                foreach (var pair in newData)
                {
                    if (pair.Value == null) continue;
                    
                    if (!entry.ContainsKey(pair.Key) || entry[pair.Key]?.ToJsonString() != pair.Value.ToJsonString())
                    {
                        entry[pair.Key] = JsonNode.Parse(pair.Value.ToJsonString());
                        updated = true;
                    }
                }
            }
            else
            {
                var clone = JsonNode.Parse(newData.ToJsonString()) as JsonObject;
                if (clone != null)
                {
                    Data.Add(clone);
                    indexMap[key] = Data.Count - 1;

                    string newId = ItemIdentity.Of(clone);
                    if (newId.Length > 0) _idIndex.TryAdd(newId, Data.Count - 1);

                    updated = true;
                }
            }

            return updated;
        }
    }
}
