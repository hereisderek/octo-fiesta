using System.Xml.Linq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Services.Subsonic;
using Xunit;

namespace octo_fiesta.Tests;

/// <summary>External items say where they come from in "externalProvider"; the "Remote" suffix marker stays.</summary>
public class ExternalOriginFieldTests
{
    private static readonly SubsonicResponseBuilder Builder = new();

    private static Song External(string provider, string id) => new() { Title = "t", ExternalProvider = provider, ExternalId = id };

    [Theory]
    [InlineData("applemusic", "1638697115", "AppleMusic")]
    [InlineData("deezer", "42", "Deezer")]
    [InlineData("gdstudio", "apple~1020406366", "GDStudio-Apple")]
    [InlineData("gdstudio", "netease~167827", "GDStudio-Netease")]
    public void SongNamesItsOrigin_AndKeepsTheRemoteMarker(string provider, string id, string expected)
    {
        var json = Builder.ConvertSongToJson(External(provider, id));

        Assert.Equal(expected, json["externalProvider"]);
        Assert.Equal("Remote", json["suffix"]);
    }

    [Fact]
    public void SongXmlHasTheSameAttribute()
    {
        var xml = Builder.ConvertSongToXml(External("gdstudio", "apple~1"), XNamespace.None);

        Assert.Equal("GDStudio-Apple", (string?)xml.Attribute("externalProvider"));
        Assert.Equal("Remote", (string?)xml.Attribute("suffix"));
    }

    [Fact]
    public void LocalSongHasNoOriginField()
    {
        var song = External("gdstudio", "apple~1");
        song.IsLocal = true;

        Assert.False(Builder.ConvertSongToJson(song).ContainsKey("externalProvider"));
    }

    [Fact]
    public void AlbumAndArtistNameTheProvider()
    {
        var album = (Dictionary<string, object>)Builder.ConvertAlbumToJson(new Album { Id = "a", Title = "t", ExternalProvider = "gdstudio" });
        var artist = (Dictionary<string, object>)Builder.ConvertArtistToJson(new Artist { Id = "a", Name = "n", ExternalProvider = "applemusic" });

        Assert.Equal("GDStudio", album["externalProvider"]);
        Assert.Equal("AppleMusic", artist["externalProvider"]);
    }
}
