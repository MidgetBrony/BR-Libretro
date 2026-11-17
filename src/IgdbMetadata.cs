using MelonLoader;
using ModsPanel;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace BR_Libretro;

internal sealed class IgdbConfig
{
    public int Version { get; set; } = 1;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

internal sealed class IgdbSearchResult
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("first_release_date")] public long FirstReleaseDate { get; set; }
}

internal sealed class IgdbGame
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("summary")] public string Summary { get; set; } = string.Empty;
    [JsonProperty("first_release_date")] public long FirstReleaseDate { get; set; }
    [JsonProperty("involved_companies")] public List<IgdbCompanyLink> InvolvedCompanies { get; set; } = new();
    [JsonProperty("genres")] public List<IgdbNamedItem> Genres { get; set; } = new();
    [JsonProperty("cover")] public IgdbImage Cover { get; set; }
}

internal sealed class IgdbCompanyLink
{
    [JsonProperty("company")] public IgdbNamedItem Company { get; set; }
    [JsonProperty("developer")] public bool Developer { get; set; }
    [JsonProperty("publisher")] public bool Publisher { get; set; }
}

internal sealed class IgdbNamedItem
{
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
}

internal sealed class IgdbImage
{
    [JsonProperty("image_id")] public string ImageId { get; set; } = string.Empty;
}

internal sealed class IgdbTokenResponse
{
    [JsonProperty("access_token")] public string AccessToken { get; set; } = string.Empty;
    [JsonProperty("expires_in")] public int ExpiresIn { get; set; }
}

internal static class IgdbMetadata
{
    private const int ReviewPageSize = 10;
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string GamesUrl = "https://api.igdb.com/v4/games";
    private static readonly string ConfigPath = Path.Combine(DataPaths.Config, "igdb.json");
    private static readonly Regex SafeImageId = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Dictionary<string, string> SearchTerms = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> ReviewPages = new(StringComparer.OrdinalIgnoreCase);
    private static IgdbConfig config;
    private static string accessToken = string.Empty;
    private static DateTime tokenExpiresUtc = DateTime.MinValue;
    private static bool busy;
    private static float nextApiRequestRealtime;

    internal static bool IsConfigured
    {
        get
        {
            EnsureConfig();
            return !string.IsNullOrWhiteSpace(config.ClientId) && !string.IsNullOrWhiteSpace(config.ClientSecret);
        }
    }

    internal static void Open(RomPlatform platform)
    {
        EnsureConfig();
        IReadOnlyList<RomData> roms = RomLibrarySystem.GetPlatformItems(platform.Id);
        int enriched = roms.Count(value => value.HasIgdbMetadata);
        ModMenu menu = ModsUi.CreateMenu(RomMedia.Key + ".igdb." + platform.Id, "IGDB Metadata",
            IsConfigured ? $"{enriched} of {roms.Count} {platform.DisplayName} ROMs matched" : "IGDB credentials are not configured");
        menu.Eyebrow = "BR-LIBRETRO ROM MEDIA";
        menu.CloseText = "BACK";
        menu.AddButton("Configure IGDB", () => OpenCredentials(platform),
            IsConfigured ? "Credentials saved for BR-Libretro" : "Client ID and secret required");
        if (IsConfigured)
            menu.AddButton("Match missing ROM metadata", () => StartBulk(platform),
                "Automatically applies unique exact-title matches; ambiguous results are left for review");
        int unmatched = roms.Count(value => !value.HasIgdbMetadata);
        if (IsConfigured && unmatched > 0)
            menu.AddButton("Review unmatched ROMs", () => OpenReview(platform),
                $"Search and choose matches for {unmatched} ROM{(unmatched == 1 ? string.Empty : "s")}");
        menu.AddLabel("IGDB enrichment is optional. ROM discovery and launching remain fully offline. Metadata and artwork are cached against each ROM's stable media ID.");
        menu.Show();
    }

    private static void OpenReview(RomPlatform platform)
    {
        List<RomData> pending = RomLibrarySystem.GetPlatformItems(platform.Id)
            .Where(value => !value.HasIgdbMetadata)
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        int pageCount = Math.Max(1, (pending.Count + ReviewPageSize - 1) / ReviewPageSize);
        int page = ReviewPages.TryGetValue(platform.Id, out int saved) ? Math.Clamp(saved, 0, pageCount - 1) : 0;
        ReviewPages[platform.Id] = page;

        ModMenu menu = ModsUi.CreateMenu(RomMedia.Key + ".igdb.review." + platform.Id, "Review IGDB Matches",
            pending.Count == 0 ? $"All {platform.DisplayName} ROMs are matched" :
            $"{pending.Count} unmatched · page {page + 1} of {pageCount}");
        menu.Eyebrow = "ROM METADATA";
        menu.CloseText = "BACK";
        foreach (RomData item in pending.Skip(page * ReviewPageSize).Take(ReviewPageSize))
        {
            RomData selected = item;
            menu.AddButton(selected.DisplayName, () => OpenForRom(selected, () => OpenReview(platform)),
                "Search IGDB and choose the correct game");
        }
        if (pending.Count == 0) menu.AddLabel("No manual review is currently required.");
        if (page > 0) menu.AddButton("Previous ROMs", () => { ReviewPages[platform.Id] = page - 1; OpenReview(platform); });
        if (page + 1 < pageCount) menu.AddButton("Next ROMs", () => { ReviewPages[platform.Id] = page + 1; OpenReview(platform); });
        if (pending.Count > 0) menu.AddButton("Retry automatic matching", () => StartBulk(platform));
        menu.AddButton("Back", () => Open(platform));
        menu.Show();
    }

    internal static void OpenForRom(RomData item, Action back)
    {
        if (item == null) return;
        if (!RomPlatformCatalog.TryGet(item.PlatformId, out RomPlatform platform)) return;
        if (!IsConfigured) { OpenCredentials(platform, () => OpenForRom(item, back)); return; }

        string query = SearchTerms.TryGetValue(item.Id, out string saved) ? saved : CleanTitle(item.DisplayName);
        SearchTerms[item.Id] = query;
        ModMenu menu = ModsUi.CreateMenu(RomMedia.Key + ".igdb.search." + item.Id, "IGDB Search", platform.DisplayName);
        menu.Eyebrow = "ROM METADATA";
        menu.CloseText = "BACK";
        if (item.HasIgdbMetadata)
            menu.AddLabel($"Current match: {item.DisplayName} · IGDB {item.IgdbId}" +
                (string.IsNullOrWhiteSpace(item.ReleaseDate) ? string.Empty : $" · {item.ReleaseDate}"));
        menu.AddTextInput("Game title", () => SearchTerms[item.Id], value => SearchTerms[item.Id] = value ?? string.Empty,
            "Title to search on IGDB");
        menu.AddButton("Search IGDB", () => StartSearch(item, back), $"Limited to {platform.DisplayName}");
        menu.AddButton("Configure IGDB", () => OpenCredentials(platform, () => OpenForRom(item, back)));
        menu.AddButton("Back", back);
        menu.Show();
    }

    private static void OpenCredentials(RomPlatform platform, Action afterSave = null)
    {
        EnsureConfig();
        string pendingId = config.ClientId;
        string pendingSecret = config.ClientSecret;
        ModMenu menu = ModsUi.CreateMenu(RomMedia.Key + ".igdb.credentials", "IGDB Setup",
            "Twitch application credentials used only by BR-Libretro");
        menu.Eyebrow = "ROM METADATA";
        menu.CloseText = "BACK";
        menu.AddTextInput("Client ID", () => pendingId, value => pendingId = value ?? string.Empty, "Twitch application Client ID");
        menu.AddPasswordInput("Client secret", () => pendingSecret, value => pendingSecret = value ?? string.Empty, "Twitch application Client Secret");
        menu.AddButton("Save credentials", () =>
        {
            config.ClientId = pendingId.Trim();
            config.ClientSecret = pendingSecret.Trim();
            SaveConfig();
            accessToken = string.Empty;
            tokenExpiresUtc = DateTime.MinValue;
            ModsUi.ShowToast(IsConfigured ? "IGDB credentials saved." : "Both IGDB credential fields are required.");
            if (afterSave != null) afterSave(); else Open(platform);
        }, "Stored locally in Documents\\br-libretro\\config\\igdb.json");
        menu.AddButton("Back", () => Open(platform));
        menu.AddLabel("Create a confidential application in the Twitch Developer Console. BR-Libretro never logs the secret or copies credentials from another application.");
        menu.Show();
    }

    private static void StartSearch(RomData item, Action back)
    {
        if (busy) { ModsUi.ShowToast("An IGDB operation is already running."); return; }
        string query = SearchTerms.TryGetValue(item.Id, out string value) ? value.Trim() : CleanTitle(item.DisplayName);
        if (string.IsNullOrWhiteSpace(query)) { ModsUi.ShowToast("Enter a title to search."); return; }
        busy = true;
        ModsUi.ShowToast("Searching IGDB for " + query + "...", 6f);
        MelonCoroutines.Start(SearchRoutine(item, query, back));
    }

    private static IEnumerator SearchRoutine(RomData item, string query, Action back)
    {
        bool authenticated = false;
        string error = null;
        yield return EnsureToken((ok, message) => { authenticated = ok; error = message; });
        if (!authenticated) { FinishError(error); yield break; }
        if (!RomPlatformCatalog.TryGet(item.PlatformId, out RomPlatform platform)) { FinishError("Unknown ROM platform."); yield break; }

        List<IgdbSearchResult> results = null;
        yield return SearchRequest(query, platform.IgdbPlatformId, (value, message) => { results = value; error = message; });
        busy = false;
        if (results == null) { FinishError(error); yield break; }
        ShowResults(item, query, results, back);
    }

    private static void ShowResults(RomData item, string query, IReadOnlyList<IgdbSearchResult> results, Action back)
    {
        RomPlatformCatalog.TryGet(item.PlatformId, out RomPlatform platform);
        ModMenu menu = ModsUi.CreateMenu(RomMedia.Key + ".igdb.results." + item.Id, "IGDB Matches",
            $"{results.Count} result{(results.Count == 1 ? string.Empty : "s")} for {query}");
        menu.Eyebrow = "ROM METADATA";
        menu.CloseText = "BACK";
        foreach (IgdbSearchResult result in results.Take(20))
        {
            IgdbSearchResult selected = result;
            string year = selected.FirstReleaseDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(selected.FirstReleaseDate).Year.ToString() : "Release date unknown";
            menu.AddButton(selected.Name, () => StartApply(item, selected, back), year + " · " + platform.DisplayName);
        }
        if (results.Count == 0) menu.AddLabel("No matches were returned. Edit the title and try again.");
        menu.AddButton("Edit search", () => OpenForRom(item, back));
        menu.AddButton("Back", back);
        menu.Show();
    }

    private static void StartApply(RomData item, IgdbSearchResult selected, Action back)
    {
        if (busy) return;
        busy = true;
        ModsUi.ShowToast("Downloading metadata for " + selected.Name + "...", 6f);
        MelonCoroutines.Start(ApplyRoutine(item, selected.Id, back));
    }

    private static IEnumerator ApplyRoutine(RomData item, long igdbId, Action back)
    {
        IgdbGame details = null;
        string error = null;
        yield return DetailsRequest(igdbId, (value, message) => { details = value; error = message; });
        if (details == null) { busy = false; FinishError(error); yield break; }

        string coverPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(details.Cover?.ImageId))
            yield return DownloadCover(item.Id, details.Cover.ImageId, (value, _) => coverPath = value);
        RomLibrarySystem.ApplyIgdbMetadata(item, details, coverPath);
        busy = false;
        ModsUi.ShowToast(details.Name + " metadata saved.", 6f);
        back?.Invoke();
    }

    private static void StartBulk(RomPlatform platform)
    {
        if (busy) { ModsUi.ShowToast("An IGDB operation is already running."); return; }
        busy = true;
        MelonCoroutines.Start(BulkRoutine(platform));
    }

    private static IEnumerator BulkRoutine(RomPlatform platform)
    {
        bool authenticated = false;
        string error = null;
        yield return EnsureToken((ok, message) => { authenticated = ok; error = message; });
        if (!authenticated) { busy = false; FinishError(error); yield break; }

        List<RomData> pending = RomLibrarySystem.GetPlatformItems(platform.Id).Where(value => !value.HasIgdbMetadata).ToList();
        int matched = 0, review = 0, failed = 0;
        for (int index = 0; index < pending.Count; index++)
        {
            RomData item = pending[index];
            string title = CleanTitle(item.DisplayName);
            ModsUi.ShowToast($"IGDB {index + 1}/{pending.Count}: {title}", 3f);
            List<IgdbSearchResult> results = null;
            error = null;
            yield return SearchRequest(title, platform.IgdbPlatformId, (value, message) => { results = value; error = message; });
            if (results == null) { failed++; yield return new WaitForSecondsRealtime(0.3f); continue; }
            string canonicalTitle = RomTitle.Canonical(title);
            List<IgdbSearchResult> exact = results.Where(value =>
                string.Equals(RomTitle.Canonical(value.Name), canonicalTitle, StringComparison.Ordinal)).ToList();
            if (exact.Count != 1)
            {
                string candidates = string.Join(" | ", results.Take(5).Select(value => value?.Name).Where(value => !string.IsNullOrWhiteSpace(value)));
                MelonLogger.Msg($"IGDB review required for '{title}' ({platform.DisplayName}); canonical='{canonicalTitle}', candidates=[{candidates}]");
                review++;
                yield return new WaitForSecondsRealtime(0.3f);
                continue;
            }

            IgdbGame details = null;
            yield return DetailsRequest(exact[0].Id, (value, message) => { details = value; error = message; });
            if (details == null) { failed++; yield return new WaitForSecondsRealtime(0.3f); continue; }
            string coverPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(details.Cover?.ImageId))
                yield return DownloadCover(item.Id, details.Cover.ImageId, (value, _) => coverPath = value);
            RomLibrarySystem.ApplyIgdbMetadata(item, details, coverPath);
            matched++;
            yield return new WaitForSecondsRealtime(0.3f);
        }
        busy = false;
        ModsUi.ShowToast($"IGDB complete: {matched} matched, {review} need review, {failed} failed.", 10f);
        if (review > 0) OpenReview(platform); else Open(platform);
    }

    private static IEnumerator EnsureToken(Action<bool, string> completed)
    {
        EnsureConfig();
        if (!IsConfigured) { completed(false, "IGDB credentials are not configured."); yield break; }
        if (!string.IsNullOrWhiteSpace(accessToken) && tokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
        { completed(true, null); yield break; }

        string url = TokenUrl + "?client_id=" + UnityWebRequest.EscapeURL(config.ClientId) +
            "&client_secret=" + UnityWebRequest.EscapeURL(config.ClientSecret) + "&grant_type=client_credentials";
        using UnityWebRequest request = new(url, "POST")
        {
            downloadHandler = new DownloadHandlerBuffer(),
            uploadHandler = new UploadHandlerRaw(Array.Empty<byte>()),
            timeout = 30
        };
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        { completed(false, "IGDB authentication failed: " + SafeError(request)); yield break; }
        try
        {
            IgdbTokenResponse token = JsonConvert.DeserializeObject<IgdbTokenResponse>(request.downloadHandler.text);
            if (string.IsNullOrWhiteSpace(token?.AccessToken)) throw new InvalidDataException("IGDB returned an empty token.");
            accessToken = token.AccessToken;
            tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn));
            completed(true, null);
        }
        catch (Exception ex) { completed(false, "Could not read the IGDB token: " + ex.Message); }
    }

    private static IEnumerator SearchRequest(string title, int platformId, Action<List<IgdbSearchResult>, string> completed)
    {
        string filter = platformId > 0 ? $" where platforms = ({platformId});" : string.Empty;
        string query = $"search \"{EscapeQuery(title)}\"; fields id,name,first_release_date;{filter} limit 20;";
        yield return PostGames(query, (json, error) =>
        {
            if (json == null) { completed(null, error); return; }
            try { completed(JsonConvert.DeserializeObject<List<IgdbSearchResult>>(json) ?? new(), null); }
            catch (Exception ex) { completed(null, "Could not read IGDB search results: " + ex.Message); }
        });
    }

    private static IEnumerator DetailsRequest(long id, Action<IgdbGame, string> completed)
    {
        string query = "fields id,name,summary,first_release_date,involved_companies.company.name," +
            "involved_companies.developer,involved_companies.publisher,genres.name,cover.image_id; where id = " + id + "; limit 1;";
        yield return PostGames(query, (json, error) =>
        {
            if (json == null) { completed(null, error); return; }
            try { completed(JsonConvert.DeserializeObject<List<IgdbGame>>(json)?.FirstOrDefault(), null); }
            catch (Exception ex) { completed(null, "Could not read IGDB metadata: " + ex.Message); }
        });
    }

    private static IEnumerator PostGames(string body, Action<string, string> completed)
    {
        while (Time.realtimeSinceStartup < nextApiRequestRealtime) yield return null;
        nextApiRequestRealtime = Time.realtimeSinceStartup + 0.27f;
        using UnityWebRequest request = new(GamesUrl, "POST")
        {
            downloadHandler = new DownloadHandlerBuffer(),
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
            timeout = 30
        };
        request.SetRequestHeader("Client-ID", config.ClientId);
        request.SetRequestHeader("Authorization", "Bearer " + accessToken);
        request.SetRequestHeader("Content-Type", "text/plain");
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            if (request.responseCode == 401) { accessToken = string.Empty; tokenExpiresUtc = DateTime.MinValue; }
            completed(null, "IGDB request failed: " + SafeError(request));
        }
        else completed(request.downloadHandler.text, null);
    }

    private static IEnumerator DownloadCover(string romId, string imageId, Action<string, string> completed)
    {
        if (!SafeImageId.IsMatch(imageId ?? string.Empty)) { completed(null, "IGDB returned an invalid cover ID."); yield break; }
        string url = "https://images.igdb.com/igdb/image/upload/t_cover_big_2x/" + imageId + ".jpg";
        using UnityWebRequest request = UnityWebRequest.Get(url);
        request.timeout = 30;
        yield return request.SendWebRequest();
        byte[] bytes = request.downloadHandler?.data;
        if (request.result != UnityWebRequest.Result.Success || bytes == null || bytes.Length < 512 || bytes.Length > 10 * 1024 * 1024)
        { completed(null, "IGDB cover download failed: " + SafeError(request)); yield break; }
        string path = Path.Combine(DataPaths.Artwork, romId + ".jpg");
        try
        {
            Directory.CreateDirectory(DataPaths.Artwork);
            string staging = path + ".staging";
            File.WriteAllBytes(staging, bytes);
            if (File.Exists(path)) File.Replace(staging, path, null, true); else File.Move(staging, path);
            completed(path, null);
        }
        catch (Exception ex) { completed(null, ex.Message); }
    }

    private static void EnsureConfig()
    {
        if (config != null) return;
        try { config = File.Exists(ConfigPath) ? JsonConvert.DeserializeObject<IgdbConfig>(File.ReadAllText(ConfigPath)) : new IgdbConfig(); }
        catch (Exception ex) { MelonLogger.Warning("Could not read IGDB configuration: " + ex.Message); config = new IgdbConfig(); }
        config ??= new IgdbConfig();
    }

    private static void SaveConfig()
    {
        Directory.CreateDirectory(DataPaths.Config);
        string staging = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".staging";
        try
        {
            File.WriteAllText(staging, JsonConvert.SerializeObject(config, Formatting.Indented));
            if (File.Exists(ConfigPath)) File.Replace(staging, ConfigPath, null, true); else File.Move(staging, ConfigPath);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static string CleanTitle(string title) => RomTitle.Clean(title);

    private static string EscapeQuery(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string SafeError(UnityWebRequest request) => request.responseCode > 0
        ? request.responseCode + " " + (request.error ?? "request failed")
        : request.error ?? "request failed";

    private static void FinishError(string error)
    {
        busy = false;
        MelonLogger.Warning("IGDB metadata operation failed: " + (error ?? "unknown error"));
        ModsUi.ShowToast(error ?? "IGDB operation failed.", 10f);
    }
}
