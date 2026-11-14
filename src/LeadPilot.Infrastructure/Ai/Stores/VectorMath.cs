namespace LeadPilot.Infrastructure.Ai.Stores;

public static class VectorMath
{
    public static float[] Normalize(float[] v)
    {
        double sum = 0; foreach (var x in v) sum += (double)x * x;
        var norm = Math.Sqrt(sum);
        if (norm < 1e-12) return v.ToArray();
        var r = new float[v.Length];
        for (var i = 0; i < v.Length; i++) r[i] = (float)(v[i] / norm);
        return r;
    }

    public static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return double.NaN;
        double s = 0; for (var i = 0; i < a.Length; i++) s += (double)a[i] * b[i];
        return s;
    }
}

public sealed class VectorStoreException(string message, Exception? inner = null) : Exception(message, inner);
