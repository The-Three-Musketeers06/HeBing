namespace AiVisionStudio.Algorithm;

/// <summary>An immutable selection of actual points, retaining its source cloud.</summary>
public sealed class PointSet3D
{
    public PointCloud Source { get; }
    public IReadOnlyList<int> Indices { get; }
    public int Count => Indices.Count;

    public PointSet3D(PointCloud source, IEnumerable<int> indices)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        var copy = indices.Distinct().OrderBy(index => index).ToArray();
        if (copy.Any(index => index < 0 || index >= source.Count)) throw new ArgumentOutOfRangeException(nameof(indices));
        Indices = Array.AsReadOnly(copy);
    }

    public List<int> InCloud(PointCloud target)
    {
        if (ReferenceEquals(Source, target)) return Indices.ToList();
        // Copy/crop operators retain exact float coordinates. Match those points,
        // not a bounding box that would also select unrelated interior points.
        var points = Indices.Select(index => (Source.X[index], Source.Y[index], Source.Z[index], Source.I[index])).ToHashSet();
        var selected = new List<int>();
        for (int i = 0; i < target.Count; i++)
            if (points.Contains((target.X[i], target.Y[i], target.Z[i], target.I[i]))) selected.Add(i);
        return selected;
    }
}
