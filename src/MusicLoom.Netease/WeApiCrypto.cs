using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace MusicLoom.Netease;

internal static class WeApiCrypto
{
    private const string Base62 = "PJArHa0dpwhvMNYqKnTbitWfEmosQ9527ZBx46IXUgOzD81VuSFyckLRljG3eC";
    private static readonly byte[] PrimaryKey = Encoding.ASCII.GetBytes("0CoJUm6Qyw8W8jud");
    private static readonly byte[] Iv = Encoding.ASCII.GetBytes("0102030405060708");
    private static readonly BigInteger Modulus = BigInteger.Parse(
        "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7",
        System.Globalization.NumberStyles.AllowHexSpecifier);
    private static readonly BigInteger Exponent = new(0x10001);

    public static WeApiEnvelope Encrypt(string json)
    {
        string secret = RandomString(16);
        return Encrypt(json, secret);
    }

    internal static WeApiEnvelope Encrypt(string json, string secret)
    {
        if (secret.Length != 16)
        {
            throw new ArgumentException("WEAPI secret must be 16 ASCII characters.", nameof(secret));
        }

        string firstPass = Convert.ToBase64String(AesCbcEncrypt(Encoding.UTF8.GetBytes(json), PrimaryKey));
        byte[] secretBytes = Encoding.ASCII.GetBytes(secret);
        string secondPass = Convert.ToBase64String(AesCbcEncrypt(Encoding.UTF8.GetBytes(firstPass), secretBytes));
        string encSecKey = RsaEncryptSecret(secret);
        return new WeApiEnvelope(secondPass, encSecKey);
    }

    public static string RandomString(int length)
    {
        Span<byte> bytes = length <= 128 ? stackalloc byte[length] : new byte[length];
        RandomNumberGenerator.Fill(bytes);
        StringBuilder builder = new(length);
        foreach (byte value in bytes)
        {
            builder.Append(Base62[value % Base62.Length]);
        }

        return builder.ToString();
    }

    private static byte[] AesCbcEncrypt(byte[] data, byte[] key)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static string RsaEncryptSecret(string secret)
    {
        string reversed = new(secret.Reverse().ToArray());
        byte[] bytes = Encoding.UTF8.GetBytes(reversed);
        BigInteger message = new(bytes, isUnsigned: true, isBigEndian: true);
        BigInteger encrypted = BigInteger.ModPow(message, Exponent, Modulus);
        return encrypted.ToString("x").PadLeft(256, '0');
    }
}

internal readonly record struct WeApiEnvelope(string Params, string EncSecKey);
