namespace AiVisionStudio.Algorithm;

/// <summary>Original 1D renderer coordinates: X/Z in PCD source order.</summary>
public sealed class ProfileData
{
    public float[] X { get; }
    public float[] Z { get; }
    public int Count => X.Length;
    public ProfileData(float[] x, float[] z)
    {
        ArgumentNullException.ThrowIfNull(x); ArgumentNullException.ThrowIfNull(z);
        if (x.Length != z.Length) throw new ArgumentException("轮廓 X/Z 数量不一致");
        X = x; Z = z;
    }
    public ProfileData Clone() => new((float[])X.Clone(), (float[])Z.Clone());
}
