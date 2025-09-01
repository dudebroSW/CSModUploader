using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// 
/// </summary>
internal class Uploader
{
    private const int ChunkSizeBytes = 50 * 1024 * 1024;
    private const int CsGameId = 251;
    private const string TemplateMetadataBlob = "{\\r\\n\\t\\\"serverFileId\\\": 0000000,\\r\\n\\t\\\"windowsFileId\\\": 0000000,\\r\\n\\t\\\"androidFileId\\\": 0000000,\\r\\n\\t\\\"pluginRoot\\\": \\\"TemplatePluginRoot\\\",\\r\\n\\t\\\"modDataPaths\\\": [\\r\\n\\t\\t\\\"TemplateModDataPath\\\"\\r\\n\\t]\\r\\n}";

    /// <summary>
    /// 
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private static async Task<int> Main(string[] args)
    {
        var pause = args.Any(a => a.Equals("--pause", StringComparison.OrdinalIgnoreCase) || a.Equals("-p", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("=== Contractor$ Mod Uploader (DudebroSW) ===");

        var gameId = CsGameId.ToString();

        Console.Write("\nMod ID (numeric): ");
        var modId = Console.ReadLine()?.Trim() ?? "";

        Console.Write("\nOAuth Access Token (Authorization: Bearer ...): ");
        var accessToken = Console.ReadLine()?.Trim() ?? "";

        Console.Write("\nFolder containing packaged .zip files: ");
        var zipFolder = Console.ReadLine()?.Trim() ?? "";

        var (serverZip, pcZip, androidZip) = DetectZips(zipFolder);
        Console.WriteLine($"Detected:\n server -> {serverZip}\n pc -> {pcZip}\n android -> {androidZip}");

        var (pluginRoot, modDataPath) = ReadPluginInfoFromSubfolders(zipFolder);
        Console.WriteLine($"Detected from JSON -> pluginRoot: {pluginRoot}, modDataPath: {modDataPath}");

        Console.Write("\nChangelog (upload notes): ");
        var changelog = Console.ReadLine()?.Trim() ?? "";

        if (!ValidateInputs(gameId, modId, accessToken, serverZip, pcZip, androidZip))
        {
            return 1;
        }

        var baseUri = $"https://g-{gameId}.modapi.io/v1";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var serverId = await UploadMulipartModfile(http, baseUri, gameId, modId, serverZip, changelog, label: "server");
            var pcId = await UploadMulipartModfile(http, baseUri, gameId, modId, pcZip, changelog, label: "pc");
            var androidId = await UploadMulipartModfile(http, baseUri, gameId, modId, androidZip, changelog, label: "android");
            
            Console.WriteLine($"\nUpload complete. Modfile IDs => server:{serverId} pc:{pcId} android:{androidId}");

            var modJson = await GetModAsync(http, baseUri, gameId, modId);
            var modName = modJson.RootElement.TryGetProperty("name", out var blobName) && blobName.ValueKind == JsonValueKind.String ? blobName.GetString() ?? "" : "";
            var currentMetadata = modJson.RootElement.TryGetProperty("metadata_blob", out var blobEl) && blobEl.ValueKind == JsonValueKind.String ? blobEl.GetString() ?? "" : "";
            string updatedMetadata;

            if (string.IsNullOrWhiteSpace(currentMetadata))
            {
                updatedMetadata = BuildMetadataFromTemplate(serverId, pcId, androidId, pluginRoot, modDataPath);
            }
            else
            {
                updatedMetadata = PatchMetadata(currentMetadata, serverId, pcId, androidId);
            }

            var tags = ExtractTagsFromMod(modJson);
            EnsureTag(tags, "server");
            EnsureTag(tags, "windows");
            EnsureTag(tags, "android");

            await EditModAsync(http, baseUri, gameId, modId, updatedMetadata, tags);

            Console.WriteLine($"\n{modName} successfully updated!");

            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"HTTP error: {ex.Message}");
            Console.ResetColor();
            return 2;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex}");
            Console.ResetColor();
            return 3;
        }
        finally
        {
            if (pause || (Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected))
            {
                Console.WriteLine("\nDone. Press Enter to close...");
                try { Console.ReadLine(); } catch { }
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="http"></param>
    /// <param name="baseUri"></param>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <param name="zipPath"></param>
    /// <param name="changelog"></param>
    /// <param name="label"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static async Task<long> UploadMulipartModfile(HttpClient http, string baseUri, string gameId, string modId, string zipPath, string changelog, string label)
    {
        Console.WriteLine($"\n[{label}] Starting multipart upload for: {Path.GetFileName(zipPath)}");

        // 1 - create multipart session
        var fileInfo = new FileInfo(zipPath);
        var createSessionForm = new Dictionary<string, string>
        {
            ["filename"] = Path.GetFileName(zipPath),
            ["filesize"] = fileInfo.Length.ToString(CultureInfo.InvariantCulture),
            ["nonce"] = Guid.NewGuid().ToString()
        };

        using var sessionResp = await http.PostAsync(
            $"{baseUri}/games/{gameId}/mods/{modId}/files/multipart",
            new FormUrlEncodedContent(createSessionForm)
        );
        var sessionJson = await EnsureJsonAsync(sessionResp, "Create Multipart Upload Session");
        var uploadId = sessionJson.RootElement.GetProperty("upload_id").GetString()
                       ?? throw new InvalidOperationException("No upload_id in session response.");

        Console.WriteLine($"[{label}] Session created. upload_id = {uploadId}");

        // 2 - upload parts
        await UploadAllPartsAsync(http, baseUri, gameId, modId, uploadId, zipPath);

        // 3 - complete session
        var completeForm = new Dictionary<string, string> { ["upload_id"] = uploadId };
        using var completeResp = await http.PostAsync(
            $"{baseUri}/games/{gameId}/mods/{modId}/files/multipart/complete",
            new FormUrlEncodedContent(completeForm)
        );
        var completeJson = await EnsureJsonAsync(completeResp, "Complete Multipart Upload Session");
        await PollMultipartCompletionAsync(http, baseUri, gameId, modId, uploadId, label, TimeSpan.FromMinutes(5));

        // 4 - upload modfile
        var modfileId = await UploadModfileAsync(http, baseUri, gameId, modId, uploadId, changelog, label);

        Console.WriteLine($"[{label}] Completed. New modfile_id = {modfileId}");
        return modfileId;
    }
     
    /// <summary>
    /// 
    /// </summary>
    /// <param name="http"></param>
    /// <param name="baseUri"></param>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <param name="uploadId"></param>
    /// <param name="zipPath"></param>
    /// <returns></returns>
    private static async Task UploadAllPartsAsync(HttpClient http, string baseUri, string gameId, string modId, string uploadId, string zipPath)
    {
        var fileLen = new FileInfo(zipPath).Length;
        var total = fileLen;
        var sent = 0L;
        var partIndex = 0;

        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        byte[] buffer = new byte[ChunkSizeBytes];
        int bytesRead;
        while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, ChunkSizeBytes))) > 0)
        {
            var start = sent;
            var endInclusive = sent + bytesRead - 1;

            using var content = new ByteArrayContent(buffer, 0, bytesRead);

            content.Headers.Add("Content-Range", $"bytes {start}-{endInclusive}/{total}");
            var putUri = $"{baseUri}/games/{gameId}/mods/{modId}/files/multipart?upload_id={Uri.EscapeDataString(uploadId)}";

            using var resp = await http.PutAsync(putUri, content);
            await EnsureJsonAsync(resp, $"Add Multipart Upload Part (part {partIndex})");

            sent += bytesRead;
            partIndex++;
            Console.WriteLine($"  Uploaded part {partIndex} [{start}-{endInclusive}] ({bytesRead} bytes)");
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="http"></param>
    /// <param name="baseUri"></param>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <param name="uploadId"></param>
    /// <param name="label"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="TimeoutException"></exception>
    private static async Task PollMultipartCompletionAsync(HttpClient http, string baseUri, string gameId, string modId, string uploadId, string label, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        int last = -1;
        var delay = TimeSpan.FromSeconds(1);

        while (sw.Elapsed < timeout)
        {
            var status = await GetMultipartStatusAsync(http, baseUri, gameId, modId, uploadId);

            if (status != last)
            {
                Console.WriteLine($"[{label}] Upload status: {StatusToText(status)} ({status})");
                last = status;
            }

            if (status == 3) // completed
                return;

            if (status == 4) // cancelled
                throw new InvalidOperationException($"[{label}] Multipart upload was CANCELLED (upload_id {uploadId}).");

            await Task.Delay(delay);
            if (delay < TimeSpan.FromSeconds(10)) delay += TimeSpan.FromSeconds(1);
        }

        throw new TimeoutException($"[{label}] Timed out waiting for multipart completion (upload_id {uploadId}).");
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="http"></param>
    /// <param name="baseUri"></param>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <param name="uploadId"></param>
    /// <param name="changelog"></param>
    /// <param name="label"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static async Task<long> UploadModfileAsync(HttpClient http, string baseUri, string gameId, string modId, string uploadId, string changelog, string label)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(uploadId), "upload_id" },
            { new StringContent(changelog), "changelog"}
        };

        using var resp = await http.PostAsync($"{baseUri}/games/{gameId}/mods/{modId}/files", form);
        var json = await EnsureJsonAsync(resp, "Upload Modfile (from upload_id)");

        if (json.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id))
        {
            Console.WriteLine($"[{label}] Modfile created from upload {uploadId}. id={id}");
            return id;
        }

        if (resp.Headers.Location is Uri loc && long.TryParse(loc.Segments.Last().TrimEnd('/'), out var fromLoc))
        {
            Console.WriteLine($"[{label}] Modfile created (via Location). id={fromLoc}");
            return fromLoc;
        }

        throw new InvalidOperationException("Add Modfile response did not contain a modfile id.");
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="http"></param>
    /// <param name="baseUri"></param>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <param name="uploadId"></param>
    /// <returns></returns>
    private static async Task<int> GetMultipartStatusAsync(HttpClient http, string baseUri, string gameId, string modId, string uploadId)
    {
        var url = $"{baseUri}/games/{gameId}/mods/{modId}/files/multipart/sessions?upload_id={Uri.EscapeDataString(uploadId)}";
        using var resp = await http.GetAsync(url);
        var json = await EnsureJsonAsync(resp, "Get Multipart Upload Sessions");

        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in data.EnumerateArray())
            {
                if (it.TryGetProperty("upload_id", out var u) && u.GetString() == uploadId &&
                    it.TryGetProperty("status", out var st) && st.TryGetInt32(out var val))
                {
                    return val;
                }
            }
        }

        if (json.RootElement.TryGetProperty("upload_id", out var u2) &&
            u2.GetString() == uploadId &&
            json.RootElement.TryGetProperty("status", out var st2) &&
            st2.TryGetInt32(out var val2))
        {
            return val2;
        }

        return 0;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="http"></param>
    /// <param name="baseUri"></param>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <returns></returns>
    private static async Task<JsonDocument> GetModAsync(HttpClient http, string baseUri, string gameId, string modId)
    {
        using var resp = await http.GetAsync($"{baseUri}/games/{gameId}/mods/{modId}");
        return await EnsureJsonAsync(resp, "Get Mod");
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="http"></param>
    /// <param name="baseUri"></param>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <param name="newBlob"></param>
    /// <param name="tags"></param>
    /// <returns></returns>
    private static async Task EditModAsync(HttpClient http, string baseUri, string gameId, string modId, string newBlob, IEnumerable<string>? tags = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(newBlob, Encoding.UTF8), "metadata_blob" }
        };

        if (tags != null)
        {
            foreach (var t in tags)
            {
                if (!string.IsNullOrWhiteSpace(t))
                    form.Add(new StringContent(t), "tags[]");
            }
        }

        using var resp = await http.PostAsync($"{baseUri}/games/{gameId}/mods/{modId}", form);
        await EnsureJsonAsync(resp, "Edit Mod");
        Console.WriteLine("\nEdit Mod: metadata_blob updated successfully!");
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="http"></param>
    /// <param name="baseUri"></param>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <param name="tags"></param>
    /// <returns></returns>
    /// <exception cref="HttpRequestException"></exception>
    private static async Task AddModTagsAsync(HttpClient http, string baseUri, string gameId, string modId, IEnumerable<string> tags)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var t in tags)
        {
            if (!string.IsNullOrWhiteSpace(t))
                pairs.Add(new KeyValuePair<string, string>("tags[]", t));
        }

        using var form = new FormUrlEncodedContent(pairs);
        using var resp = await http.PostAsync($"{baseUri}/games/{gameId}/mods/{modId}/tags", form);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Add Mod Tags failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");

        Console.WriteLine("Added tags: " + string.Join(", ", tags));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="resp"></param>
    /// <param name="label"></param>
    /// <returns></returns>
    /// <exception cref="HttpRequestException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    private static async Task<JsonDocument> EnsureJsonAsync(HttpResponseMessage resp, string label)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{label} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{text}");
        }
        try
        {
            return JsonDocument.Parse(text);
        }
        catch
        {
            throw new InvalidOperationException($"{label} returned non-JSON content:\n{text}");
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="gameId"></param>
    /// <param name="modId"></param>
    /// <param name="token"></param>
    /// <param name="server"></param>
    /// <param name="pc"></param>
    /// <param name="android"></param>
    /// <returns></returns>
    private static bool ValidateInputs(string gameId, string modId, string token, string server, string pc, string android)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("Game ID, Mod ID, and Access Token are required.");
            return false;
        }
        if (!File.Exists(server)) { Console.WriteLine($"SERVER zip not found: {server}"); return false; }
        if (!File.Exists(pc)) { Console.WriteLine($"PC zip not found: {pc}"); return false; }
        if (!File.Exists(android)) { Console.WriteLine($"ANDROID zip not found: {android}"); return false; }
        return true;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="folder"></param>
    /// <returns></returns>
    /// <exception cref="DirectoryNotFoundException"></exception>
    private static (string serverZip, string pcZip, string androidZip) DetectZips(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");

        string serverPattern = "*_server.zip";
        string pcPattern = "*_pc.zip";
        string androidPattern = "*_android.zip";

        string server = FindSingle(folder, serverPattern, "SERVER");
        string pc = FindSingle(folder, pcPattern, "PC");
        string android = FindSingle(folder, androidPattern, "ANDROID");

        return (server, pc, android);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="folder"></param>
    /// <param name="pattern"></param>
    /// <param name="label"></param>
    /// <returns></returns>
    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    private static string FindSingle(string folder, string pattern, string label)
    {
        var matches = Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly);
        if (matches.Length == 0)
            throw new FileNotFoundException($"No {label} file matching {pattern} in {folder}");
        if (matches.Length > 1)
            throw new InvalidOperationException($"Multiple {label} files matching {pattern} in {folder}. Please keep only one.");

        return matches[0];
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="modJson"></param>
    /// <returns></returns>
    private static List<string> ExtractTagsFromMod(JsonDocument modJson)
    {
        var result = new List<string>();
        var root = modJson.RootElement;

        if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in tagsEl.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
                }
                else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    var s = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
                }
            }
        }

        var dedup = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in result)
            if (seen.Add(t)) dedup.Add(t);

        return dedup;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="tags"></param>
    /// <param name="tag"></param>
    private static void EnsureTag(List<string> tags, string tag)
    {
        if (!tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            tags.Add(tag);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="parentFolder"></param>
    /// <returns></returns>
    /// <exception cref="DirectoryNotFoundException"></exception>
    /// <exception cref="FileNotFoundException"></exception>
    private static (string pluginRoot, string modDataPath) ReadPluginInfoFromSubfolders(string parentFolder)
    {
        if (string.IsNullOrWhiteSpace(parentFolder) || !Directory.Exists(parentFolder))
            throw new DirectoryNotFoundException($"Folder not found: {parentFolder}");

        var pcFolder = Directory.GetDirectories(parentFolder, "*_pc", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var androidFolder = Directory.GetDirectories(parentFolder, "*_android", SearchOption.TopDirectoryOnly).FirstOrDefault();

        (string pluginRoot, string modDataPath)? pcVals = pcFolder != null ? ReadPluginJson(pcFolder) : null;
        (string pluginRoot, string modDataPath)? anVals = androidFolder != null ? ReadPluginJson(androidFolder) : null;

        var chosen = pcVals ?? anVals;
        if (chosen is null)
            throw new FileNotFoundException("No JSON found in *_pc or *_android subfolders.");

        if (pcVals != null && anVals != null &&
            (!string.Equals(pcVals.Value.pluginRoot, anVals.Value.pluginRoot, StringComparison.Ordinal) ||
             !string.Equals(pcVals.Value.modDataPath, anVals.Value.modDataPath, StringComparison.Ordinal)))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"WARNING: pc/android JSON mismatch.\n  pc: pluginRoot='{pcVals.Value.pluginRoot}', modDataPath='{pcVals.Value.modDataPath}'\n  android: pluginRoot='{anVals.Value.pluginRoot}', modDataPath='{anVals.Value.modDataPath}'\nUsing PC values.");
            Console.ResetColor();
        }

        return chosen.Value;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="folder"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static (string pluginRoot, string modDataPath)? ReadPluginJson(string folder)
    {
        var jsonPath = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (jsonPath is null) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;

        if (!root.TryGetProperty("pluginRoot", out var pr) || pr.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"pluginRoot missing or not a string in {jsonPath}.");
        if (!root.TryGetProperty("modDataPaths", out var md) || md.ValueKind != JsonValueKind.Array || md.GetArrayLength() == 0 || md[0].ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"modDataPaths[0] missing or not a string in {jsonPath}.");

        return (pr.GetString()!, md[0].GetString()!);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="serverId"></param>
    /// <param name="windowsId"></param>
    /// <param name="androidId"></param>
    /// <param name="pluginRoot"></param>
    /// <param name="modDataPath"></param>
    /// <returns></returns>
    private static string BuildMetadataFromTemplate(long serverId, long windowsId, long androidId, string pluginRoot, string modDataPath)
    {
        string metadata = TemplateMetadataBlob;
        metadata = RegexReplaceNumberPropEscaped(metadata, "serverFileId", serverId);
        metadata = RegexReplaceNumberPropEscaped(metadata, "windowsFileId", windowsId);
        metadata = RegexReplaceNumberPropEscaped(metadata, "androidFileId", androidId);
        metadata = metadata.Replace("TemplatePluginRoot", pluginRoot);
        metadata = metadata.Replace("TemplateModDataPath", modDataPath);
        return metadata;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="current"></param>
    /// <param name="serverId"></param>
    /// <param name="windowsId"></param>
    /// <param name="androidId"></param>
    /// <returns></returns>
    private static string PatchMetadata(string current, long serverId, long windowsId, long androidId)
    {
        try
        {
            var node = JsonNode.Parse(current) as JsonObject;
            if (node is not null)
            {
                if (node.ContainsKey("serverFileId")) node["serverFileId"] = serverId;
                if (node.ContainsKey("windowsFileId")) node["windowsFileId"] = windowsId;
                if (node.ContainsKey("androidFileId")) node["androidFileId"] = androidId;

                var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                return json;
            }
        }
        catch { }

        string result = current;
        result = RegexReplaceNumberProp(result, "serverFileId", serverId);
        result = RegexReplaceNumberProp(result, "windowsFileId", windowsId);
        result = RegexReplaceNumberProp(result, "androidFileId", androidId);

        if (ReferenceEquals(result, current) && current.Contains("\\\""))
        {
            result = RegexReplaceNumberPropEscaped(result, "serverFileId", serverId);
            result = RegexReplaceNumberPropEscaped(result, "windowsFileId", windowsId);
            result = RegexReplaceNumberPropEscaped(result, "androidFileId", androidId);
        }

        return result;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="text"></param>
    /// <param name="key"></param>
    /// <param name="id"></param>
    /// <returns></returns>
    private static string RegexReplaceNumberProp(string text, string key, long id)
    {
        var pattern = $@"(?<pre>""{Regex.Escape(key)}""\s*:\s*)(?<val>""?\d+""?)";
        return Regex.Replace(text, pattern, m => m.Groups["pre"].Value + id);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="text"></param>
    /// <param name="key"></param>
    /// <param name="id"></param>
    /// <returns></returns>
    private static string RegexReplaceNumberPropEscaped(string text, string key, long id)
    {
        var pattern = $@"(?<pre>\\\""{Regex.Escape(key)}\\\""\s*:\s*)(?<val>\\?""?\d+\\?""?)";
        return Regex.Replace(text, pattern, m => m.Groups["pre"].Value + id);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="s"></param>
    /// <returns></returns>
    private static string StatusToText(int s) => s switch
    {
        0 => "INCOMPLETE",
        1 => "PENDING",
        2 => "PROCESSING",
        3 => "COMPLETE",
        4 => "CANCELLED",
        _ => $"UNKNOWN({s})"
    };
}
