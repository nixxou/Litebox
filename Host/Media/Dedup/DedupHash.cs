// 64-bit perceptual fingerprints — dHash / pHash, algorithmically identical to the python `imagededup`
// package (ported from the cross-validated ImageDedupCli project). Bit placement is internal (we only
// compare our own hashes with each other); what matters is the decision rule: same pixels/coefficients,
// same median. Dependency-free math; the pixel matrices come from DedupPreprocess (Magick decode).

#nullable enable

using System;
using System.Numerics;

namespace LbApiHost.Host.Media.Dedup;

internal static class DedupHash
{
    // ── dHash: target (width=9, height=8), horizontal diff ───────────────────
    // imagededup: hash_mat = image[:, 1:] > image[:, :-1]
    public static ulong DHash(double[,] g /* [8, 9] */)
    {
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                if (g[y, x + 1] > g[y, x]) hash |= 1UL << bit;
                bit++;
            }
        return hash;
    }

    // ── pHash: target 32x32, DCT2, top-left 8x8 block, median excluding DC ───
    // imagededup:
    //   dct_coef = dct(dct(img, axis=0), axis=1)
    //   reduced  = dct_coef[:8, :8]
    //   median   = np.median(flatten(reduced)[1:])   # excludes the DC term [0,0]
    //   hash_mat = reduced >= median                 # all 64 coefs, DC included
    public static ulong PHash(double[,] g /* [32, 32] */)
    {
        double[,] dct = Dct8x8(g);

        // Median of the 63 coefficients EXCLUDING DC (0,0).
        Span<double> vals = stackalloc double[63];
        int idx = 0;
        for (int k = 0; k < 8; k++)
            for (int l = 0; l < 8; l++)
            {
                if (k == 0 && l == 0) continue;
                vals[idx++] = dct[k, l];
            }
        vals.Sort();
        double median = vals[31]; // 63 sorted elements -> median = index 31

        ulong hash = 0;
        int bit = 0;
        for (int k = 0; k < 8; k++)
            for (int l = 0; l < 8; l++)
            {
                if (dct[k, l] >= median) hash |= 1UL << bit;
                bit++;
            }
        return hash;
    }

    // Separable 2D DCT-II, computing only the low-frequency 8x8 block. The scale factor (scipy type-2,
    // norm=None) has no effect: we only compare against the median of the same coefficients.
    private static double[,] Dct8x8(double[,] g)
    {
        const int N = 32;
        double[,] cos = CosTable;

        // Transform over axis 0 (row index n0) -> temp[k0, n1], k0 in 0..7
        double[,] temp = new double[8, N];
        for (int k0 = 0; k0 < 8; k0++)
            for (int n1 = 0; n1 < N; n1++)
            {
                double sum = 0;
                for (int n0 = 0; n0 < N; n0++) sum += cos[k0, n0] * g[n0, n1];
                temp[k0, n1] = sum;
            }

        // Transform over axis 1 (column index n1) -> out[k0, k1]
        double[,] outc = new double[8, 8];
        for (int k0 = 0; k0 < 8; k0++)
            for (int k1 = 0; k1 < 8; k1++)
            {
                double sum = 0;
                for (int n1 = 0; n1 < N; n1++) sum += cos[k1, n1] * temp[k0, n1];
                outc[k0, k1] = sum;
            }
        return outc;
    }

    private static readonly double[,] CosTable = BuildCosTable(32);

    private static double[,] BuildCosTable(int n)
    {
        var t = new double[8, n];
        for (int k = 0; k < 8; k++)
            for (int i = 0; i < n; i++)
                t[k, i] = Math.Cos(Math.PI / n * (i + 0.5) * k);
        return t;
    }

    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
