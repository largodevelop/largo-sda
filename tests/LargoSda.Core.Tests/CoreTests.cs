using LargoSda.Core;
using Xunit;

namespace LargoSda.Core.Tests;

public class SteamGuardTests
{
    // base64("12345678901234567890"); expected values computed with an independent implementation.
    private const string Secret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";

    [Theory]
    [InlineData(0, "GG5F5")]
    [InlineData(59, "PV9M4")]
    [InlineData(1111111109, "PY4YB")]
    [InlineData(1700000000, "R87JJ")]
    public void GeneratesKnownCodes(long time, string expected) =>
        Assert.Equal(expected, SteamGuard.GenerateCode(Secret, time));

    [Fact]
    public void CodeIsStableWithinPeriod() =>
        Assert.Equal(SteamGuard.GenerateCode(Secret, 60), SteamGuard.GenerateCode(Secret, 89));

    [Theory]
    [InlineData(0, 30)]
    [InlineData(29, 1)]
    [InlineData(45, 15)]
    public void ComputesSecondsRemaining(long time, int expected) =>
        Assert.Equal(expected, SteamGuard.SecondsRemaining(time));
}

public class MaFileTests
{
    [Fact]
    public void ParsesAndPreservesUnknownFields()
    {
        const string json = """
            {"shared_secret":"MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=","account_name":"demo","serial_number":"42",
             "Session":{"SteamID":76561190000000001,"SessionID":"abc"}}
            """;
        var file = MaFile.Parse(json);

        Assert.Equal("demo", file.AccountName);
        Assert.Equal(76561190000000001UL, file.SteamId);
        Assert.Contains("serial_number", file.ToJson());
        Assert.Contains("SessionID", file.ToJson());
    }

    [Fact]
    public void RejectsFileWithoutSharedSecret() =>
        Assert.ThrowsAny<Exception>(() => MaFile.Parse("""{"account_name":"x"}"""));

    [Fact]
    public void ReadsSdaSessionTokens()
    {
        var file = MaFile.Parse("""
            {"shared_secret":"MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=","account_name":"demo",
             "Session":{"SteamID":1,"AccessToken":"a","RefreshToken":"r","SessionID":"s"}}
            """);
        Assert.Equal(("a", "r", "s"), (file.Session!.AccessToken, file.Session.RefreshToken, file.Session.SessionId));
    }
}

public class SteamApiTests
{
    [Fact]
    public void GeneratesConfirmationHash() =>
        Assert.Equal("43vC3oBlbDh0ZI7+uZxuhTZNyeo=",
            ConfirmationClient.GenerateConfirmationHash("aWRlbnRpdHktc2VjcmV0LTEyMzQ=", 1700000000, "list"));

    [Fact]
    public void ReadsJwtExpiry()
    {
        Assert.Equal(1800000000, SteamAuth.GetTokenExpiry("x.eyJleHAiOiAxODAwMDAwMDAwfQ.y"));
        Assert.Equal(0, SteamAuth.GetTokenExpiry("garbage"));
    }

    [Fact]
    public void ProtobufRoundTrips()
    {
        var bytes = new ProtoWriter()
            .UInt64(1, 300)
            .String(2, "hello")
            .Fixed64(3, 76561190000000001)
            .Int32(4, -500)
            .Message(5, new ProtoWriter().UInt64(1, 3))
            .Message(5, new ProtoWriter().UInt64(1, 1))
            .ToArray();
        var message = ProtoMessage.Parse(bytes);

        Assert.Equal(300UL, message.UInt64(1));
        Assert.Equal("hello", message.String(2));
        Assert.Equal(76561190000000001UL, message.UInt64(3));
        Assert.Equal(-500, (int)(long)message.UInt64(4)!.Value);
        Assert.Equal([3UL, 1UL], message.Messages(5).Select(m => m.UInt64(1)!.Value));
    }
}
