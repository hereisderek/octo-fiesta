using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Search;
using octo_fiesta.Models.Settings;
using octo_fiesta.Models.Subsonic;

namespace octo_fiesta.Services.GDStudio;

/// <summary>
/// Metadata via the GD Studio API. Upstream only has track search (plus "_album" search that
/// lists tracks of matching albums), so albums/artists are derived from track results and
/// identified by name, and found songs are remembered to serve GetSongAsync.
/// </summary>
public class GDStudioMetadataService : IMusicMetadataService
{
    public const string ProviderName = "gdstudio";

    private readonly HttpClient _http;
    private readonly ILogger<GDStudioMetadataService> _logger;
    private readonly GDStudioSettings _s;

    // ponytail: unbounded in-memory map, add eviction if search volume ever makes it matter
    private readonly ConcurrentDictionary<string, Song> _songs = new();
    private readonly ConcurrentDictionary<string, string> _pics = new();
    // Tracks seen per album (in any search), so opening an album still works when a re-query misses it.
    private readonly ConcurrentDictionary<string, List<GDStudioTrack>> _albumTracks = new();

    private void RememberAlbum(string key, IEnumerable<GDStudioTrack> tracks)
        => _albumTracks.AddOrUpdate(key, _ => tracks.ToList(),
            (_, old) => old.Concat(tracks).DistinctBy(t => t.Id).ToList());

    public GDStudioMetadataService(
        IHttpClientFactory httpClientFactory,
        IOptions<GDStudioSettings> settings,
        ILogger<GDStudioMetadataService> logger)
    {
        _http = httpClientFactory.CreateClient(GDStudioHttpClientConfiguration.ClientName);
        _logger = logger;
        _s = settings.Value;
    }

    // Albums and artists have no ids upstream: the id is the base64url-encoded name.
    private const string AlbumPrefix = "ext-gdstudio-album-";
    private const string ArtistPrefix = "ext-gdstudio-artist-";

    private static string Enc(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Dec(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
    }

    // The API answers 422 for count > 99.
    private const int MaxCount = 99;

    // The first call to each source (+ suffix) may have to warm up a plugin (e.g. download and run the
    // site's signing script), so it gets three times the normal timeout.
    private readonly ConcurrentDictionary<string, byte> _warmed = new();

    // The API rate limits (about 50 requests per 5 minutes, then 503/429), and a client that opens an
    // artist page asks for every album at once. So: identical queries are shared and remembered for a
    // few minutes (in flight or done, never failures), at most MaxParallel run at a time, and a 503/429
    // is retried once after a short pause.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private const int MaxParallel = 4;
    private readonly SemaphoreSlim _gate = new(MaxParallel);
    // ponytail: unbounded like the maps above, add eviction if it ever matters
    private readonly ConcurrentDictionary<string, (DateTime At, Task<List<GDStudioTrack>> Task)> _queryCache = new();

    private Task<List<GDStudioTrack>> Fetch(string api, string name, int count, TimeSpan timeout)
    {
        var key = $"{api}\n{count}\n{name}";
        if (_queryCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl
            && !hit.Task.IsFaulted && !hit.Task.IsCanceled)
            return hit.Task;

        var entry = (At: DateTime.UtcNow, Task: FetchCore(api, name, count, timeout));
        _queryCache[key] = entry;
        _ = entry.Task.ContinueWith(t => { if (t.IsFaulted || t.IsCanceled) _queryCache.TryRemove(new KeyValuePair<string, (DateTime, Task<List<GDStudioTrack>>)>(key, entry)); },
            TaskScheduler.Default);
        return entry.Task;
    }

    private async Task<List<GDStudioTrack>> FetchCore(string api, string name, int count, TimeSpan timeout)
    {
        var url = _s.Url($"types=search&source={Uri.EscapeDataString(api)}"
                  + $"&name={Uri.EscapeDataString(name)}&count={count}");
        // Time spent queued for a slot does not count against the request timeout, only the request and its retry do
        using (var queue = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            await _gate.WaitAsync(queue.Token);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            for (var attempt = 0; ; attempt++)
            {
                using var response = await _http.GetAsync(url, cts.Token);
                if (attempt == 0 && response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
                {
                    var pause = response.Headers.RetryAfter?.Delta is { } d && d < TimeSpan.FromSeconds(3) ? d : TimeSpan.FromSeconds(1);
                    _logger.LogWarning("GDStudio {Source} '{Query}' got {Status}, retrying once in {Ms} ms", api, name, (int)response.StatusCode, (int)pause.TotalMilliseconds);
                    await Task.Delay(pause, cts.Token);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<GDStudioTrack>>(cts.Token) ?? [];
            }
        }
        finally { _gate.Release(); }
    }

    // Queries every configured source in parallel (`suffix` selects e.g. "_album") and interleaves
    // the results so each source is represented. A failing or timed-out source is logged and skipped.
    private async Task<List<GDStudioTrack>> QueryAsync(string name, int count, string suffix = "")
    {
        count = Math.Min(count, MaxCount);
        async Task<List<GDStudioTrack>> One(string source)
        {
            var api = source + suffix; // what the API sees, e.g. netease_album
            var seconds = _warmed.TryAdd(api, 0) ? _s.TimeoutSeconds * 3 : _s.TimeoutSeconds;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var results = await Fetch(api, name, count, TimeSpan.FromSeconds(seconds));
                _logger.LogInformation("GDStudio {Source} '{Query}' (count={Count}) -> {Results} results in {Ms} ms",
                    api, name, count, results.Count, watch.ElapsedMilliseconds);
                return results.Where(t => !string.IsNullOrEmpty(t.Id))
                    .Select(t => t with { Id = _s.TrackId(source, t.Id) }).ToList();
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("GDStudio source '{Source}' timed out after {Seconds}s for '{Query}'", api, seconds, name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GDStudio source '{Source}' failed for '{Query}'", api, name);
            }
            return [];
        }

        var lists = await Task.WhenAll(_s.Sources.Select(One));
        return lists.SelectMany(l => l.Select((t, i) => (t, i))).OrderBy(x => x.i).Select(x => x.t).ToList();
    }

    public async Task<List<Song>> SearchSongsAsync(string query, int limit = 20)
        => (await QueryAsync(query, limit)).Select(ToSong).ToList();

    // An album is identified by its name plus its first artist, so different releases that share a name
    // (every artist has a "素颜") stay apart. Ids without an artist part are still understood.
    private static string AlbumKey(string album, string? artist) => Enc(artist is null ? album : album + "\t" + artist);

    private static (string Name, string? Artist) SplitAlbumKey(string encoded)
    {
        var text = Dec(encoded);
        var i = text.IndexOf('\t');
        return i < 0 ? (text, null) : (text[..i], text[(i + 1)..]);
    }

    // `<source>_album` returns the tracks of albums matching the keyword.
    private async Task<List<Album>> AlbumsFromAsync(string suffix, string name, int count, Func<GDStudioTrack, bool>? filter = null)
    {
        var tracks = (await QueryAsync(name, count, suffix)).Where(t => filter?.Invoke(t) ?? true).ToList();
        return tracks.GroupBy(t => (t.Album, Artist: t.Artist.FirstOrDefault())).Where(g => g.Key.Album != "").Select(g =>
        {
            RememberAlbum(AlbumKey(g.Key.Album, g.Key.Artist), g);
            return g;
        }).Select(g => new Album
        {
            Id = AlbumPrefix + AlbumKey(g.Key.Album, g.Key.Artist),
            Title = g.Key.Album,
            Artist = g.Key.Artist ?? "",
            ArtistId = g.Key.Artist is null ? null : ArtistPrefix + Enc(g.Key.Artist),
            SongCount = g.Count(),
            ExternalProvider = ProviderName,
            ExternalId = AlbumKey(g.Key.Album, g.Key.Artist)
        }).ToList();
    }

    public async Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20)
        => (await AlbumsFromAsync("_album", query, 50)).Take(limit).ToList();

    public async Task<Album?> GetAlbumAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;
        var (name, artist) = SplitAlbumKey(externalId);
        var cached = _albumTracks.TryGetValue(externalId, out var seen) ? seen : [];
        bool IsThisAlbum(GDStudioTrack t) => t.Album == name && (artist is null || t.Artist.FirstOrDefault() == artist);
        // One album search per source (results are shared and remembered, see Fetch), plus what earlier searches
        // already showed for this album...
        var tracks = (await QueryAsync(name, MaxCount, "_album")).Concat(cached).DistinctBy(t => t.Id).Where(IsThisAlbum).ToList();
        // ...and only when that finds nothing (the album search returns just the best few matches) a track
        // search for "<artist> <album>", to keep the request count down.
        if (tracks.Count == 0)
            tracks = (await QueryAsync(artist is null ? name : $"{artist} {name}", MaxCount)).Where(IsThisAlbum).ToList();
        if (tracks.Count == 0) return null;
        var songs = tracks.Select(ToSong).ToList();
        for (var i = 0; i < songs.Count; i++) { songs[i].Track = i + 1; songs[i].TotalTracks = songs.Count; }
        return new Album
        {
            Id = AlbumPrefix + externalId,
            Title = name,
            Artist = songs[0].AlbumArtist ?? "",
            ArtistId = songs[0].ArtistId,
            SongCount = songs.Count,
            ExternalProvider = ProviderName,
            ExternalId = externalId,
            Songs = songs
        };
    }

    public async Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20)
        => (await QueryAsync(query, 50)).SelectMany(t => t.Artist).Distinct().Take(limit).Select(ToArtist).ToList();

    public Task<Artist?> GetArtistAsync(string externalProvider, string externalId)
        => Task.FromResult(externalProvider == ProviderName ? ToArtist(Dec(externalId)) : null);

    public async Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return [];
        var name = Dec(externalId);
        return await AlbumsFromAsync("", name, 50, t => t.Artist.Contains(name));
    }

    public async Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20)
    {
        var songs = (await QueryAsync(query, Math.Max(songLimit, artistLimit))).ToList();
        return new SearchResult
        {
            Songs = songs.Take(songLimit).Select(ToSong).ToList(),
            Artists = songs.SelectMany(t => t.Artist).Distinct().Take(artistLimit).Select(ToArtist).ToList(),
            Albums = await SearchAlbumsAsync(query, albumLimit)
        };
    }

    // No get-by-id upstream ("url" only returns the file link), so serve what we've seen in
    // searches/albums. After a restart an unseen id falls back to a bare song (title = id).
    public async Task<Song?> GetSongAsync(string externalProvider, string externalId)
    {
        if (externalProvider != ProviderName) return null;
        var song = _songs.GetOrAdd(externalId, id => new Song
        {
            Title = id, ExternalProvider = ProviderName, ExternalId = id
        });
        if (song.CoverArtUrlLarge is null && _pics.TryGetValue(externalId, out var pic))
        {
            song.CoverArtUrlLarge = song.CoverArtUrl = await ResolvePicAsync(externalId, pic);
        }
        return song;
    }

    public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20) => Task.FromResult(new List<ExternalPlaylist>());
    public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId) => Task.FromResult<ExternalPlaylist?>(null);
    public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId) => Task.FromResult(new List<Song>());

    private async Task<string?> ResolvePicAsync(string trackId, string picId)
    {
        if (picId.StartsWith("//")) return "https:" + picId;
        if (picId.StartsWith("http")) return picId;
        try
        {
            var (source, _) = _s.SplitTrackId(trackId);
            var r = await _http.GetFromJsonAsync<GDStudioPic>(
                _s.Url($"types=pic&source={Uri.EscapeDataString(source)}&id={Uri.EscapeDataString(picId)}&size=500"));
            return string.IsNullOrEmpty(r?.Url) ? null : r.Url;
        }
        catch { return null; }
    }

    private static Artist ToArtist(string name) => new()
    {
        Id = ArtistPrefix + Enc(name), Name = name, ExternalProvider = ProviderName, ExternalId = Enc(name)
    };

    public async Task<string?> GetLyricsAsync(string trackId)
    {
        try
        {
            var (source, id) = _s.SplitTrackId(trackId);
            var r = await _http.GetFromJsonAsync<GDStudioLyric>(
                _s.Url($"types=lyric&source={Uri.EscapeDataString(source)}&id={Uri.EscapeDataString(id)}"));
            return string.IsNullOrWhiteSpace(r?.Lyric) ? null : r.Lyric;
        }
        catch { return null; }
    }

    private Song ToSong(GDStudioTrack t)
    {
        var artists = t.Artist.Select(ToArtist).ToList();
        var song = new Song
        {
            Title = t.Name,
            Artist = string.Join(", ", t.Artist),
            Artists = artists,
            ArtistId = artists.FirstOrDefault()?.Id,
            Album = t.Album,
            AlbumId = t.Album == "" ? null : AlbumPrefix + AlbumKey(t.Album, t.Artist.FirstOrDefault()),
            AlbumArtist = t.Artist.FirstOrDefault(),
            IsLocal = false,
            ExternalProvider = ProviderName,
            ExternalId = t.Id
        };
        _songs[t.Id] = song;
        if (t.Album != "") RememberAlbum(AlbumKey(t.Album, t.Artist.FirstOrDefault()), [t]);
        if (!string.IsNullOrEmpty(t.PicId)) _pics[t.Id] = t.PicId;
        return song;
    }

    private record GDStudioTrack(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("artist")] List<string> Artist,
        [property: JsonPropertyName("album")] string Album,
        [property: JsonPropertyName("pic_id")] string? PicId);

    private record GDStudioPic([property: JsonPropertyName("url")] string? Url);

    private record GDStudioLyric([property: JsonPropertyName("lyric")] string? Lyric);
}
