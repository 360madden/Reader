using System.Text;
using Reader.Core;
using Reader.Models;

namespace Reader.Tests;

public class MarkerParserTests
{
    private static readonly string FullMarker =
        "##READER_DATA##|Arthok|70|Mage|SomeGuild|12500|15000|mana|8900|10000|1234.56|789.01|-45.23|Dragnoth|72|55|hostile|##END_READER##";

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ParseFromBuffer_FullMarker_ReturnsCorrectSnapshot()
    {
        var snapshot = MarkerParser.ParseFromBuffer(Bytes(FullMarker));

        Assert.NotNull(snapshot);
        Assert.Equal("Arthok", snapshot.Player.Name);
        Assert.Equal(70, snapshot.Player.Level);
        Assert.Equal("Mage", snapshot.Player.Calling);
        Assert.Equal("SomeGuild", snapshot.Player.Guild);

        Assert.Equal(12500, snapshot.Stats.Hp);
        Assert.Equal(15000, snapshot.Stats.HpMax);
        Assert.Equal("mana", snapshot.Stats.ResourceKind);
        Assert.Equal(8900, snapshot.Stats.Resource);
        Assert.Equal(10000, snapshot.Stats.ResourceMax);

        Assert.Equal(1234.56f, snapshot.Position.X!.Value, precision: 2);
        Assert.Equal(789.01f, snapshot.Position.Y!.Value, precision: 2);
        Assert.Equal(-45.23f, snapshot.Position.Z!.Value, precision: 2);

        Assert.NotNull(snapshot.Target);
        Assert.Equal("Dragnoth", snapshot.Target.Name);
        Assert.Equal(72, snapshot.Target.Level);
        Assert.Equal(55, snapshot.Target.HpPercent);
        Assert.Equal("hostile", snapshot.Target.Relation);
    }

    [Fact]
    public void ParseFromBuffer_MarkerEmbeddedInLargerBuffer_ReturnsCorrectSnapshot()
    {
        // Simulate the marker appearing partway through a large memory buffer
        byte[] prefix = new byte[256];
        byte[] suffix = new byte[128];
        byte[] marker = Bytes(FullMarker);
        byte[] buffer = [.. prefix, .. marker, .. suffix];

        var snapshot = MarkerParser.ParseFromBuffer(buffer);

        Assert.NotNull(snapshot);
        Assert.Equal("Arthok", snapshot.Player.Name);
    }

    [Fact]
    public void ParseFromBuffer_NoTarget_ReturnsNullTarget()
    {
        string marker = "##READER_DATA##|Arthok|70|Mage||12500|15000|mana|8900|10000|1234.56|789.01|-45.23||||##END_READER##";
        var snapshot = MarkerParser.ParseFromBuffer(Bytes(marker));

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Target);
        Assert.Null(snapshot.Player.Guild);
    }

    [Fact]
    public void ParseFromBuffer_NoMarker_ReturnsNull()
    {
        byte[] buffer = Bytes("just some random bytes in memory");
        Assert.Null(MarkerParser.ParseFromBuffer(buffer));
    }

    [Fact]
    public void ParseFromBuffer_MissingEndMarker_ReturnsNull()
    {
        string truncated = "##READER_DATA##|Arthok|70|Mage||12500|15000|mana|8900|10000|1234.56|789.01|-45.23||||";
        Assert.Null(MarkerParser.ParseFromBuffer(Bytes(truncated)));
    }

    [Fact]
    public void ParseFromBuffer_TooFewFields_ReturnsNull()
    {
        string bad = "##READER_DATA##|Arthok|70|##END_READER##";
        Assert.Null(MarkerParser.ParseFromBuffer(Bytes(bad)));
    }

    [Fact]
    public void ParseFromBuffer_NonAsciiName_ParsesCorrectly()
    {
        string marker = "##READER_DATA##|Ärthök|70|Warrior||5000|5000|energy|100|100|0.00|0.00|0.00||||##END_READER##";
        var snapshot = MarkerParser.ParseFromBuffer(Bytes(marker));

        Assert.NotNull(snapshot);
        Assert.Equal("Ärthök", snapshot.Player.Name);
    }

    [Fact]
    public void ParseFromBuffer_EmptyBuffer_ReturnsNull()
    {
        Assert.Null(MarkerParser.ParseFromBuffer([]));
    }
}
