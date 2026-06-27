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
        private readonly Dictionary<string, List<JsonObject>> _exactMatchCache = new();
        private readonly Dictionary<string, List<JsonObject>> _colonRemovedMatchCache = new();
        private readonly Dictionary<string, List<JsonObject>> _flexibleMatchCache = new();
        private readonly Dictionary<int, string> _sanCache = new();
        private readonly Dictionary<int, string?> _sanFirst3Cache = new();

        private static readonly Regex TitlePrefixPattern = new(@"^(HD|8K|HQ|4K|FULLHD|SD|SP|PPV|Blu-ray|BluRay|DVD|\s)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HashPattern = new(@"[_\-]?[a-fA-F0-9]{6,32}$", RegexOptions.Compiled);

        public DataManager(string dataPath, bool backupOnInit = false)
        {
            DataPath = dataPath;
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
                File.WriteAllText(DataPath, jsonString);

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
            _exactMatchCache.Clear();
            _colonRemovedMatchCache.Clear();
            _flexibleMatchCache.Clear();
            _sanCache.Clear();
            _sanFirst3Cache.Clear();

            for (int i = 0; i < Data.Count; i++)
            {
                var item = Data[i];
                string? title = item["title"]?.ToString();
                if (!string.IsNullOrEmpty(title))
                {
                    string normTitle = NormalizeTitle(title);
                    _titleIndex[normTitle] = i;

                    string exactKey = FileSanitizer.SanitizeFilename(title);
                    if (!_exactMatchCache.TryGetValue(exactKey, out var exactList))
                    {
                        exactList = new List<JsonObject>();
                        _exactMatchCache[exactKey] = exactList;
                    }
                    exactList.Add(item);

                    string colonKey = FileSanitizer.SanitizeFilenameRemoveColon(title);
                    if (!_colonRemovedMatchCache.TryGetValue(colonKey, out var colonList))
                    {
                        colonList = new List<JsonObject>();
                        _colonRemovedMatchCache[colonKey] = colonList;
                    }
                    colonList.Add(item);

                    string flexKey = NormalizeSeparators(exactKey);
                    if (!_flexibleMatchCache.TryGetValue(flexKey, out var flexList))
                    {
                        flexList = new List<JsonObject>();
                        _flexibleMatchCache[flexKey] = flexList;
                    }
                    flexList.Add(item);

                    int refKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
                    _sanCache[refKey] = exactKey;
                    var sanWords = Regex.Split(exactKey, @"[_\-\s]+").Where(w => !string.IsNullOrEmpty(w)).ToArray();
                    _sanFirst3Cache[refKey] = sanWords.Length >= 3 ? string.Join("_", sanWords.Take(3)) : null;
                }

                string? subtitle = item["subtitle"]?.ToString();
                if (!string.IsNullOrEmpty(subtitle))
                {
                    _subtitleIndex[subtitle] = i;
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
                entry = FindBookEntry(title);
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
                    updated = true;
                }
            }

            return updated;
        }

        public List<JsonObject> FindItemsForVideo(string searchKey)
        {
            if (_exactMatchCache.TryGetValue(searchKey, out var exactMatches))
            {
                return exactMatches;
            }

            if (_colonRemovedMatchCache.TryGetValue(searchKey, out var colonRemovedMatches))
            {
                return colonRemovedMatches;
            }

            string normalizedSearchKey = NormalizeSeparators(searchKey);
            if (_flexibleMatchCache.TryGetValue(normalizedSearchKey, out var flexibleMatches))
            {
                return flexibleMatches;
            }

            var partialMatches = Data.Where(item =>
            {
                int rk = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
                return _sanCache.TryGetValue(rk, out var san) && !string.IsNullOrEmpty(san) && san.StartsWith(searchKey);
            }).ToList();

            if (partialMatches.Any())
            {
                return partialMatches;
            }

            if (searchKey.Length > 20)
            {
                var reverseMatches = Data.Where(item =>
                {
                    int rk = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
                    return _sanCache.TryGetValue(rk, out var san) && !string.IsNullOrEmpty(san) && searchKey.StartsWith(san);
                }).ToList();

                if (reverseMatches.Any())
                {
                    return reverseMatches;
                }
            }

            string cleanedSearchKey = HashPattern.Replace(searchKey, "").TrimEnd('_', '-');
            if (cleanedSearchKey != searchKey && cleanedSearchKey.Length > 10)
            {
                var hashMatches = Data.Where(item =>
                {
                    int rk = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
                    if (!_sanCache.TryGetValue(rk, out var san) || string.IsNullOrEmpty(san)) return false;
                    return san.StartsWith(cleanedSearchKey) || cleanedSearchKey.StartsWith(san);
                }).ToList();

                if (hashMatches.Any())
                {
                    return hashMatches;
                }
            }

            if (searchKey.Length > 15)
            {
                var words = Regex.Split(searchKey, @"[_\-\s]+").Where(w => !string.IsNullOrEmpty(w)).ToArray();
                if (words.Length >= 3)
                {
                    string firstWordsKey = string.Join("_", words.Take(3));
                    var wordMatches = Data.Where(item =>
                    {
                        int rk = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
                        return _sanFirst3Cache.TryGetValue(rk, out var first3) && first3 != null && first3 == firstWordsKey;
                    }).ToList();

                    if (wordMatches.Any())
                    {
                        return wordMatches;
                    }
                }
            }

            return new List<JsonObject>();
        }

        private string NormalizeSeparators(string text)
        {
            string normalized = Regex.Replace(text, @"[_\-\s]+", " ");
            return normalized.Trim();
        }
    }
}
