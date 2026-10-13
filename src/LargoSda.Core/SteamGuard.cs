using System.Security.Cryptography;

namespace LargoSda.Core;

/// <summary>Generates Steam Guard (TOTP) login codes.</summary>
public static class SteamGuard
{
    public const int PeriodSeconds = 30;
    public const int CodeLength = 5;

    private const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";

    /// <param name="sharedSecretBase64">The <c>shared_secret</c> field of a .maFile.</param>
    /// <param name="unixTime">Steam-synchronised Unix time in seconds.</param>
    public static string GenerateCode(string sharedSecretBase64, long unixTime)
    {
        var key = Convert.FromBase64String(sharedSecretBase64);
        var counter = unixTime / PeriodSeconds;

        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            message[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, message, hash);

        var offset = hash[19] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24)
                    | (hash[offset + 1] << 16)
                    | (hash[offset + 2] << 8)
                    | hash[offset + 3];

        Span<char> code = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
        {
            code[i] = Alphabet[value % Alphabet.Length];
            value /= Alphabet.Length;
        }

        return new string(code);
    }

    /// <summary>Seconds left until the current code is replaced.</summary>
    public static int SecondsRemaining(long unixTime) => PeriodSeconds - (int)(unixTime % PeriodSeconds);
}
