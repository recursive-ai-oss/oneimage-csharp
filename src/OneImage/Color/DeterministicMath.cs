namespace OneImage;

/// <summary>
/// Power and cube-root functions that give the same bits on every platform. The platform math library (<c>Math.Pow</c>,
/// <c>MathF.Pow</c>, <c>MathF.Cbrt</c>) rounds some results one unit in the last place differently on Linux, macOS, Windows and
/// WebAssembly, which showed up as different pixels. These use only IEEE-754 addition, subtraction, multiplication, division
/// and conversions, which every platform computes identically, so the result cannot depend on the machine.
/// </summary>
/// <remarks>
/// Method: the intermediate values are kept as an unevaluated sum of two doubles (about 106 bits) while taking
/// <c>log2(x)</c> by the atanh series, multiplying by the exponent and taking <c>2^t</c> by a Taylor series on a reduced argument,
/// so the result is accurate to about 2^-64 relative. The float versions round that to the nearest float (through
/// round-to-odd, so there is no double rounding): they return the correctly rounded float except when the exact value lies
/// within about 2^-60 of a rounding boundary, which does not happen for any float input of the sRGB curves (checked
/// exhaustively against a slower exact reference in the tests and in tools/TransferSweep). The double versions are within one unit in the last place and almost always correctly rounded.
/// No fused multiply-add is used: products are split exactly with Veltkamp's splitting instead, because a fused instruction
/// would give different bits on hardware without it.
/// Special values follow the C rules for finite and infinite operands; results below the smallest normal double flush to zero.
/// </remarks>
internal static class DeterministicMath
{
    private const double SplitFactor = 134217729.0; // 2^27 + 1

    private readonly struct DD(double hi, double lo)
    {
        public readonly double Hi = hi;
        public readonly double Lo = lo;
    }

    // Constants as hi + lo pairs, generated from 80-digit decimal values.
    private static readonly DD s_ln2 = new(0.6931471805599453, 2.3190468138462996e-17);
    private static readonly DD s_log2e = new(1.4426950408889634, 2.0355273740931033e-17);
    private static readonly DD s_twoThirds = new(0.6666666666666666, 3.700743415417188e-17);

    /// <summary>x raised to y in double precision, deterministic on every platform.</summary>
    public static double Pow(double x, double y)
    {
        if (PowSpecial(x, y, out double special))
        {
            return special;
        }

        bool negate = x < 0 && Math.Abs(y % 2.0) == 1.0;
        PowCore(Math.Abs(x), y, out double hi, out double lo);
        _ = lo;
        return negate ? -hi : hi;
    }

    /// <summary>x raised to y for floats: the correctly rounded float result (see the type remarks), deterministic on every platform.</summary>
    public static float PowF(float x, float y)
    {
        if (PowSpecial(x, y, out double special))
        {
            return (float)special;
        }

        if (x > 0 && TryFastPowF(x, y, out float fast))
        {
            return fast;
        }

        bool negate = x < 0 && Math.Abs((double)y % 2.0) == 1.0;
        PowCore(Math.Abs((double)x), y, out double hi, out double lo);
        float r = ToSingle(hi, lo);
        return negate ? -r : r;
    }

    /// <summary><see cref="PowF"/> without the fast path (always the double-double computation); tests compare the two.</summary>
    internal static float PowFReference(float x, float y)
    {
        if (PowSpecial(x, y, out double special))
        {
            return (float)special;
        }

        PowCore(Math.Abs((double)x), y, out double hi, out double lo);
        return ToSingle(hi, lo);
    }

    /// <summary>Cube root in double precision, deterministic on every platform.</summary>
    public static double Cbrt(double x)
    {
        if (!(x > 0) || double.IsInfinity(x))
        {
            return x < 0 ? -Cbrt(-x) : x; // zero, NaN and infinity map to themselves
        }

        CbrtCore(x, out double hi, out _);
        return hi;
    }

    /// <summary>Cube root for floats: the correctly rounded float result, deterministic on every platform.</summary>
    public static float CbrtF(float x)
    {
        if (!(x > 0) || float.IsInfinity(x))
        {
            return x < 0 ? -CbrtF(-x) : x;
        }

        CbrtCore(x, out double hi, out double lo);
        return ToSingle(hi, lo);
    }

    // C rules for zero, infinite, NaN and negative operands. Returns true when the answer is final.
    private static bool PowSpecial(double x, double y, out double result)
    {
        if (y == 0 || x == 1)
        {
            result = 1;
            return true;
        }

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            result = double.NaN;
            return true;
        }

        bool yInteger = Math.Floor(y) == y && !double.IsInfinity(y);
        bool yOdd = yInteger && Math.Abs(y % 2.0) == 1.0;
        if (x == 0)
        {
            bool negZero = double.IsNegative(x) && yOdd;
            result = y > 0 ? (negZero ? -0.0 : 0.0) : (negZero ? double.NegativeInfinity : double.PositiveInfinity);
            return true;
        }

        if (double.IsInfinity(y))
        {
            double ax = Math.Abs(x);
            result = ax == 1 ? 1 : ((ax > 1) == (y > 0) ? double.PositiveInfinity : 0.0);
            return true;
        }

        if (x < 0 && !yInteger)
        {
            result = double.NaN;
            return true;
        }

        if (double.IsInfinity(x))
        {
            double mag = y > 0 ? double.PositiveInfinity : 0.0;
            result = x < 0 && yOdd ? -mag : mag;
            return true;
        }

        result = 0;
        return false;
    }

    // The float rounding of the approximation is accepted only when the whole interval of relative width 2^-43 around it rounds
    // to the same float, which is the case for all but about 2 in a million inputs; the rest take the slow path. Either way the
    // result is the correctly rounded float, so both paths agree, and both use only IEEE operations.
    private static bool TryFastPowF(float xf, float yf, out float result)
    {
        result = 0;
        if (!TryPowApprox(xf, yf, out double v))
        {
            return false;
        }

        const double Margin = 1.0 / 8796093022208.0; // 2^-43
        float lo = (float)(v * (1.0 - Margin));
        float hi = (float)(v * (1.0 + Margin));
        if (lo != hi)
        {
            return false;
        }

        result = lo;
        return true;
    }

    /// <summary>
    /// x raised to y by plain double arithmetic, about 4 times faster than <see cref="Pow"/> but only accurate to a relative
    /// 2^-45 (the actual error is below 2^-49). Deterministic on every platform. Returns false (and the caller uses
    /// <see cref="Pow"/>) outside x in the normal float range or when |log2 x| or |y log2 x| exceeds 32.
    /// </summary>
    internal static bool TryPowApprox(double x, double y, out double value)
    {
        value = 0;
        if (!(x >= 1.1754943508222875e-38 && x <= 3.4028234663852886e38))
        {
            return false; // subnormal, zero, infinity or NaN
        }

        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = (int)(bits >> 52) - 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > 1.4142135623730951)
        {
            m *= 0.5;
            exp++;
        }

        double f = (m - 1.0) / (m + 1.0);
        double z = f * f;
        double poly = 2.0 / 21;
        poly = (poly * z) + (2.0 / 19);
        poly = (poly * z) + (2.0 / 17);
        poly = (poly * z) + (2.0 / 15);
        poly = (poly * z) + (2.0 / 13);
        poly = (poly * z) + (2.0 / 11);
        poly = (poly * z) + (2.0 / 9);
        poly = (poly * z) + (2.0 / 7);
        poly = (poly * z) + (2.0 / 5);
        poly = (poly * z) + (2.0 / 3);
        double ln = (2.0 * f) + (f * z * poly);
        double l2 = exp + (ln * 1.4426950408889634);
        double t = l2 * y;
        if (!(Math.Abs(l2) <= 32.0 && Math.Abs(t) <= 32.0))
        {
            return false;
        }

        double n = Math.Round(t);
        double u = (t - n) * 0.6931471805599453;
        double e = 1.0 / 87178291200.0; // 1/14!
        e = (e * u) + (1.0 / 6227020800.0);
        e = (e * u) + (1.0 / 479001600.0);
        e = (e * u) + (1.0 / 39916800.0);
        e = (e * u) + (1.0 / 3628800.0);
        e = (e * u) + (1.0 / 362880.0);
        e = (e * u) + (1.0 / 40320.0);
        e = (e * u) + (1.0 / 5040.0);
        e = (e * u) + (1.0 / 720.0);
        e = (e * u) + (1.0 / 120.0);
        e = (e * u) + (1.0 / 24.0);
        e = (e * u) + (1.0 / 6.0);
        e = (e * u) + 0.5;
        e = (e * u) + 1.0;
        e = (e * u) + 1.0;
        value = e * Pow2((int)n);
        return true;
    }

    // |x|^y for finite nonzero x, finite nonzero y: hi + lo with the power of two folded in.
    private static void PowCore(double x, double y, out double hi, out double lo)
    {
        DD t = Mul(Log2(x), y);
        if (t.Hi > 1100)
        {
            hi = double.PositiveInfinity;
            lo = 0;
            return;
        }

        if (t.Hi < -1100)
        {
            hi = 0;
            lo = 0;
            return;
        }

        double nn = Math.Round(t.Hi);
        DD r = TwoSum(t.Hi - nn, t.Lo); // |r| <= 0.5 + tiny
        DD u = Mul(r, s_ln2);

        // exp(u) with u cut to u/8 so that a short Taylor series reaches 2^-66, then squared three times.
        DD v = new(u.Hi * 0.125, u.Lo * 0.125);
        double w = v.Hi;
        double series = 1.0 / 39916800;
        series = (series * w) + (1.0 / 3628800);
        series = (series * w) + (1.0 / 362880);
        series = (series * w) + (1.0 / 40320);
        series = (series * w) + (1.0 / 5040);
        series = (series * w) + (1.0 / 720);
        series = (series * w) + (1.0 / 120);
        series = (series * w) + (1.0 / 24);
        series = (series * w) + (1.0 / 6);
        double tail = w * w * w * series;
        DD half = Mul(v, v);
        half = new DD(half.Hi * 0.5, half.Lo * 0.5);
        DD e = Add(Add(new DD(1.0, 0.0), v), half);
        e = Add(e, new DD(tail, 0.0));
        e = Mul(e, e);
        e = Mul(e, e);
        e = Mul(e, e);

        int n = (int)nn;
        if (n > 1023)
        {
            hi = double.PositiveInfinity;
            lo = 0;
            return;
        }

        // Scale by 2^n in two exact steps when n is below the normal range of one factor.
        if (n >= -1022)
        {
            double s = Pow2(n);
            hi = e.Hi * s;
            lo = e.Lo * s;
        }
        else
        {
            double s = Pow2(n + 200) * Pow2(-200);
            hi = e.Hi * s;
            lo = 0;
        }
    }

    // log2 of a finite positive double as hi + lo (about 2^-66 absolute error).
    private static DD Log2(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = 0;
        if ((bits >> 52) == 0)
        {
            x *= 18014398509481984.0; // 2^54: bring subnormals into the normal range
            bits = BitConverter.DoubleToInt64Bits(x);
            exp = -54;
        }

        exp += (int)(bits >> 52) - 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);
        if (m > 1.4142135623730951)
        {
            m *= 0.5;
            exp++;
        }

        // ln m = 2 atanh(f), f = (m - 1) / (m + 1), |f| < 0.172: 2f + 2f^3/3 + 2f^5 (1/5 + z/7 + z^2/9 + ...), z = f^2.
        DD f = Div(new DD(m - 1.0, 0.0), TwoSum(m, 1.0));
        DD f2 = Mul(f, f);
        DD f3 = Mul(f2, f);
        double z = f2.Hi;
        double poly = 1.0 / 25;
        poly = (poly * z) + (1.0 / 23);
        poly = (poly * z) + (1.0 / 21);
        poly = (poly * z) + (1.0 / 19);
        poly = (poly * z) + (1.0 / 17);
        poly = (poly * z) + (1.0 / 15);
        poly = (poly * z) + (1.0 / 13);
        poly = (poly * z) + (1.0 / 11);
        poly = (poly * z) + (1.0 / 9);
        poly = (poly * z) + (1.0 / 7);
        poly = (poly * z) + (1.0 / 5);
        double tail = 2.0 * f3.Hi * z * poly;
        DD ln = Add(new DD(2.0 * f.Hi, 2.0 * f.Lo), Mul(f3, s_twoThirds));
        ln = Add(ln, new DD(tail, 0.0));
        return Add(Mul(ln, s_log2e), new DD(exp, 0.0));
    }

    // Cube root of a finite positive double as hi + lo (about 2^-100 relative error).
    private static void CbrtCore(double x, out double hi, out double lo)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = 0;
        if ((bits >> 52) == 0)
        {
            x *= 18014398509481984.0;
            bits = BitConverter.DoubleToInt64Bits(x);
            exp = -54;
        }

        exp += (int)(bits >> 52) - 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000FFFFFFFFFFFFFL) | 0x3FF0000000000000L);

        // x = w * 2^(3q) with w in [1, 8).
        int q = exp >= 0 ? exp / 3 : -((2 - exp) / 3);
        int rem = exp - (3 * q);
        double w = m * Pow2(rem);

        // Newton's iteration from a chord guess, then one correction step with the residual taken in double-double.
        double y = 1.0 + ((w - 1.0) / 7.0);
        for (int i = 0; i < 5; i++)
        {
            y = ((2.0 * y) + (w / (y * y))) / 3.0;
        }

        DD y2 = TwoProd(y, y);
        DD y3 = Mul(y2, y);
        DD resid = Add(new DD(w, 0.0), new DD(-y3.Hi, -y3.Lo));
        double corr = resid.Hi / (3.0 * y * y);
        DD r = QuickTwoSum(y, corr);
        double s = Pow2(q);
        hi = r.Hi * s;
        lo = r.Lo * s;
    }

    // 2^n for -1022 <= n <= 1023, exactly.
    private static double Pow2(int n) => BitConverter.Int64BitsToDouble((long)(n + 1023) << 52);

    // Round hi + lo to the nearest float without double rounding: make the double the odd neighbour when it is inexact.
    private static float ToSingle(double hi, double lo)
    {
        if (lo != 0 && !double.IsInfinity(hi) && hi != 0)
        {
            long b = BitConverter.DoubleToInt64Bits(hi);
            if ((b & 1) == 0)
            {
                b += (lo > 0) == (hi > 0) ? 1 : -1;
                hi = BitConverter.Int64BitsToDouble(b);
            }
        }

        return (float)hi;
    }

    private static DD TwoSum(double a, double b)
    {
        double s = a + b;
        double bb = s - a;
        return new DD(s, (a - (s - bb)) + (b - bb));
    }

    private static DD QuickTwoSum(double a, double b)
    {
        double s = a + b;
        return new DD(s, b - (s - a));
    }

    private static DD TwoProd(double a, double b)
    {
        double p = a * b;
        double t = SplitFactor * a;
        double ah = t - (t - a);
        double al = a - ah;
        t = SplitFactor * b;
        double bh = t - (t - b);
        double bl = b - bh;
        return new DD(p, ((((ah * bh) - p) + (ah * bl)) + (al * bh)) + (al * bl));
    }

    private static DD Add(DD a, DD b)
    {
        DD s = TwoSum(a.Hi, b.Hi);
        DD t = TwoSum(a.Lo, b.Lo);
        DD u = QuickTwoSum(s.Hi, s.Lo + t.Hi);
        return QuickTwoSum(u.Hi, u.Lo + t.Lo);
    }

    private static DD Mul(DD a, DD b)
    {
        DD p = TwoProd(a.Hi, b.Hi);
        return QuickTwoSum(p.Hi, p.Lo + ((a.Hi * b.Lo) + (a.Lo * b.Hi)));
    }

    private static DD Mul(DD a, double b)
    {
        DD p = TwoProd(a.Hi, b);
        return QuickTwoSum(p.Hi, p.Lo + (a.Lo * b));
    }

    private static DD Div(DD a, DD b)
    {
        double q1 = a.Hi / b.Hi;
        DD m1 = Mul(b, q1);
        DD r = Add(a, new DD(-m1.Hi, -m1.Lo));
        double q2 = r.Hi / b.Hi;
        DD m2 = Mul(b, q2);
        r = Add(r, new DD(-m2.Hi, -m2.Lo));
        double q3 = r.Hi / b.Hi;
        return Add(QuickTwoSum(q1, q2), new DD(q3, 0.0));
    }
}
