namespace OneImage.Tests.WebP;

/// <summary>Image quality metrics on RGBA buffers (luma based).</summary>
internal static class Quality
{
    private static double Luma(byte[] rgba, int i) => (0.299 * rgba[i * 4]) + (0.587 * rgba[(i * 4) + 1]) + (0.114 * rgba[(i * 4) + 2]);

    public static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        int n = a.Length / 4;
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                double d = a[(i * 4) + c] - b[(i * 4) + c];
                sum += d * d;
            }
        }

        double mse = sum / (n * 3.0);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Mean structural similarity of the luma plane over 8x8 windows moved in steps of 2 pixels.</summary>
    public static double Ssim(byte[] a, byte[] b, int w, int h)
    {
        const double C1 = 6.5025;
        const double C2 = 58.5225;
        double total = 0;
        long count = 0;
        for (int y = 0; y + 8 <= h; y += 2)
        {
            for (int x = 0; x + 8 <= w; x += 2)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int j = 0; j < 8; j++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        int p = ((y + j) * w) + x + i;
                        double va = Luma(a, p);
                        double vb = Luma(b, p);
                        sa += va;
                        sb += vb;
                        saa += va * va;
                        sbb += vb * vb;
                        sab += va * vb;
                    }
                }

                double ma = sa / 64, mb = sb / 64;
                double va2 = (saa / 64) - (ma * ma);
                double vb2 = (sbb / 64) - (mb * mb);
                double cov = (sab / 64) - (ma * mb);
                total += (((2 * ma * mb) + C1) * ((2 * cov) + C2)) / (((ma * ma) + (mb * mb) + C1) * (va2 + vb2 + C2));
                count++;
            }
        }

        return count == 0 ? 1 : total / count;
    }
}
