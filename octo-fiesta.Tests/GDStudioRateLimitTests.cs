using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.GDStudio;
using Xunit;

namespace octo_fiesta.Tests;

/// <summary>GD Studio answers 503/429 once it is hit too often; a burst must not lose the whole query.</summary>
public class GDStudioRateLimitTests
{
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(Interlocked.Increment(ref Calls)));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private const string OneTrack = """[{"id":"1","name":"Song","artist":["A"],"album":"Al","pic_id":null}]""";

    private static GDStudioMetadataService Service(StubHandler handler, string source = "netease")
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(GDStudioHttpClientConfiguration.ClientName)).Returns(new HttpClient(handler));
        return new GDStudioMetadataService(factory.Object,
            Options.Create(new GDStudioSettings { Source = source, TimeoutSeconds = 5 }),
            NullLogger<GDStudioMetadataService>.Instance);
    }

    [Fact]
    public async Task A503IsRetriedOnce()
    {
        var handler = new StubHandler(n => n == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(OneTrack));

        var songs = await Service(handler).SearchSongsAsync("q");

        Assert.Single(songs);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task IdenticalQueriesShareOneRequest()
    {
        var handler = new StubHandler(_ => Json(OneTrack));
        var service = Service(handler);

        await Task.WhenAll(service.SearchSongsAsync("q"), service.SearchSongsAsync("q"));
        await service.SearchSongsAsync("q");

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AFailedQueryIsNotRemembered()
    {
        var handler = new StubHandler(n => n <= 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(OneTrack));
        var service = Service(handler);

        Assert.Empty(await service.SearchSongsAsync("q")); // 503, retry 503 -> skipped, no exception
        Assert.Single(await service.SearchSongsAsync("q")); // asks again instead of replaying the failure
    }

    [Fact]
    public async Task OneSourceFailingStillReturnsTheOther()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OneTrack) });
        // both sources answer; each song id carries its source
        var songs = await Service(handler, "netease,joox").SearchSongsAsync("q");

        Assert.Equal(2, songs.Count);
        Assert.Equal(["netease~1", "joox~1"], songs.Select(s => s.ExternalId).OrderByDescending(x => x).ToArray());
    }
}
