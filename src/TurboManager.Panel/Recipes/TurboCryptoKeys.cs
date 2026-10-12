using System.Numerics;
using System.Security.Cryptography;

namespace TurboManager.Panel.Recipes;

/// <summary>
/// RSA keys in the form Turbo's <c>Turbo:Crypto</c> expects: public exponent 3 (hex "3" in KeySize), modulus and
/// private exponent as hex. The defaults in Turbo's appsettings.json are public, so every hotel gets its own.
/// .NET's RSA API always uses exponent 65537, hence the key is built here from two random primes.
/// </summary>
public static class TurboCryptoKeys
{
    public sealed record Keys(string Exponent, string Modulus, string PrivateExponent);

    private static readonly BigInteger E = 3;

    public static Keys Generate(int bits = 1024)
    {
        while (true)
        {
            var p = Prime(bits / 2);
            var q = Prime(bits / 2);

            if (p == q)
            {
                continue;
            }

            var n = p * q;

            if (n.GetBitLength() != bits)
            {
                continue;
            }

            var phi = (p - 1) * (q - 1);
            var d = ModInverse(E, phi);

            return new Keys("3", Hex(n), Hex(d));
        }
    }

    private static string Hex(BigInteger value) => value.ToString("X").TrimStart('0');

    /// <summary>A random prime of exactly <paramref name="bits"/> bits with p ≡ 2 (mod 3), so 3 is invertible mod p-1.</summary>
    private static BigInteger Prime(int bits)
    {
        var bytes = new byte[bits / 8 + 1];

        while (true)
        {
            RandomNumberGenerator.Fill(bytes.AsSpan(0, bits / 8));
            bytes[^1] = 0; // positive
            bytes[bits / 8 - 1] |= 0xC0; // top two bits set: p*q has the full length
            bytes[0] |= 1; // odd
            var candidate = new BigInteger(bytes);

            if (candidate % 3 == 2 && IsProbablePrime(candidate))
            {
                return candidate;
            }
        }
    }

    private static readonly int[] SmallPrimes = [3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97];

    /// <summary>Miller-Rabin with 40 random bases (error probability below 2^-80).</summary>
    public static bool IsProbablePrime(BigInteger n)
    {
        if (n < 2)
        {
            return false;
        }

        foreach (var small in SmallPrimes)
        {
            if (n == small)
            {
                return true;
            }

            if (n % small == 0)
            {
                return false;
            }
        }

        var d = n - 1;
        var r = 0;

        while (d.IsEven)
        {
            d >>= 1;
            r++;
        }

        var length = n.GetByteCount(isUnsigned: true);
        var buffer = new byte[length + 1];

        for (var round = 0; round < 40; round++)
        {
            BigInteger a;

            do
            {
                RandomNumberGenerator.Fill(buffer.AsSpan(0, length));
                buffer[^1] = 0;
                a = new BigInteger(buffer) % (n - 3) + 2;
            }
            while (a < 2);

            var x = BigInteger.ModPow(a, d, n);

            if (x.IsOne || x == n - 1)
            {
                continue;
            }

            var composite = true;

            for (var i = 1; i < r; i++)
            {
                x = BigInteger.ModPow(x, 2, n);

                if (x == n - 1)
                {
                    composite = false;
                    break;
                }
            }

            if (composite)
            {
                return false;
            }
        }

        return true;
    }

    private static BigInteger ModInverse(BigInteger a, BigInteger m)
    {
        BigInteger oldR = a, r = m, oldS = 1, s = 0;

        while (!r.IsZero)
        {
            var quotient = oldR / r;
            (oldR, r) = (r, oldR - quotient * r);
            (oldS, s) = (s, oldS - quotient * s);
        }

        if (!oldR.IsOne)
        {
            throw new InvalidOperationException("Not invertible.");
        }

        return (oldS % m + m) % m;
    }
}
