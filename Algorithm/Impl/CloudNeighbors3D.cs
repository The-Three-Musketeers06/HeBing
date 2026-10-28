namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Balanced exact k-d search; no fixed world-space radius or all-neighbor sorting.</summary>
internal sealed class CloudNeighbors3D
{
    private readonly PointCloud _cloud;
    private readonly int[] _order;
    public CloudNeighbors3D(PointCloud cloud, CancellationToken cancellation)
    {
        _cloud = cloud; _order = Enumerable.Range(0, cloud.Count).ToArray();
        Build(0, _order.Length, 0);
        void Build(int first, int end, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (end - first <= 1) return;
            int middle = (first + end) / 2, axis = depth % 3;
            // Three-way quickselect keeps duplicate coordinates linear per level.
            int lo = first, hi = end - 1;
            while (lo < hi)
            {
                double pivot = Axis(_order[(lo + hi) / 2], axis);
                int left = lo, cursor = lo, right = hi;
                while (cursor <= right)
                {
                    var value = Axis(_order[cursor], axis);
                    if (value < pivot) { Swap(left++, cursor++); }
                    else if (value > pivot) Swap(cursor, right--);
                    else cursor++;
                }
                if (middle < left) hi = left - 1;
                else if (middle > right) lo = right + 1;
                else break;
            }
            Build(first, middle, depth + 1); Build(middle + 1, end, depth + 1);
        }
        void Swap(int a, int b) => (_order[a], _order[b]) = (_order[b], _order[a]);
    }
    private double Axis(int index, int axis) => axis == 0 ? _cloud.X[index] : axis == 1 ? _cloud.Y[index] : _cloud.Z[index];
    public double NearestSquared(double x,double y,double z)
    {
        double best=double.PositiveInfinity;Search(0,_order.Length,0);return best;
        void Search(int first,int end,int depth)
        {
            if(first>=end)return;var middle=(first+end)/2;var point=_order[middle];var axis=depth%3;
            double dx=x-_cloud.X[point],dy=y-_cloud.Y[point],dz=z-_cloud.Z[point];best=Math.Min(best,dx*dx+dy*dy+dz*dz);
            var delta=axis==0?dx:axis==1?dy:dz;
            if(delta<0){Search(first,middle,depth+1);if(delta*delta<=best)Search(middle+1,end,depth+1);}
            else{Search(middle+1,end,depth+1);if(delta*delta<=best)Search(first,middle,depth+1);}
        }
    }
    private double DistanceSquared(int a, int b)
    {
        double x = (double)_cloud.X[a] - _cloud.X[b], y = (double)_cloud.Y[a] - _cloud.Y[b], z = (double)_cloud.Z[a] - _cloud.Z[b];
        return x * x + y * y + z * z;
    }
    public double MeanDistance(int query, double[] heap)
    {
        int size = 0; Search(0, _order.Length, 0);
        double sum = 0; for (int i = 0; i < size; i++) sum += Math.Sqrt(heap[i]);
        return size == 0 ? 0 : sum / size;
        void Offer(double value)
        {
            if (size < heap.Length)
            {
                int at = size++;
                while (at > 0)
                { int parent = (at - 1) / 2; if (heap[parent] >= value) break; heap[at] = heap[parent]; at = parent; }
                heap[at] = value;
            }
            else if (value < heap[0])
            {
                int at = 0;
                while (at * 2 + 1 < size)
                {
                    int child = at * 2 + 1;
                    if (child + 1 < size && heap[child + 1] > heap[child]) child++;
                    if (heap[child] <= value) break;
                    heap[at] = heap[child]; at = child;
                }
                heap[at] = value;
            }
        }
        void Search(int first, int end, int depth)
        {
            if (first >= end) return;
            int middle = (first + end) / 2, point = _order[middle], axis = depth % 3;
            double delta = Axis(query, axis) - Axis(point, axis);
            if (point != query) Offer(DistanceSquared(query, point));
            if (delta < 0)
            {
                Search(first, middle, depth + 1);
                if (size < heap.Length || delta * delta < heap[0]) Search(middle + 1, end, depth + 1);
            }
            else
            {
                Search(middle + 1, end, depth + 1);
                if (size < heap.Length || delta * delta < heap[0]) Search(first, middle, depth + 1);
            }
        }
    }
    public bool HasNeighbors(int query, double radius, int minimum)
    {
        int found = 0; var squared = radius * radius;
        Search(0, _order.Length, 0); return found >= minimum;
        void Search(int first, int end, int depth)
        {
            if (first >= end || found >= minimum) return;
            int middle = (first + end) / 2, point = _order[middle], axis = depth % 3;
            double delta = Axis(query, axis) - Axis(point, axis);
            if (point != query && DistanceSquared(query, point) <= squared) found++;
            if (delta <= radius) Search(first, middle, depth + 1);
            if (delta >= -radius) Search(middle + 1, end, depth + 1);
        }
    }
}

internal static class CloudOutlierRemoval
{
    public static PointCloud Run(OpContext ctx, ParamBag p, PointCloud cloud)
    {
        for (int i = 0; i < cloud.Count; i++)
            if (!float.IsFinite(cloud.X[i]) || !float.IsFinite(cloud.Y[i]) || !float.IsFinite(cloud.Z[i]))
                throw new ArgumentException("离群点滤波输入包含非有限坐标");
        var method = p.Str("Method", "Neighborhood-based outlier removal");
        var tree = new CloudNeighbors3D(cloud, ctx.CancellationToken);
        var keep = new bool[cloud.Count];
        var options = new ParallelOptions { CancellationToken = ctx.CancellationToken, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) };
        if (method == "Neighborhood-based outlier removal")
        {
            double number = p.GroupDbl(method, "Number of Neighbors", P.D(p, 8, "Neighbor Count", "K", "MeanK"));
            double ratio = p.GroupDbl(method, "Multiple of Standard Deviation", P.D(p, 1.5, "Std Ratio", "StdDev", "Std Mul"));
            if (!double.IsFinite(number + ratio) || number < 1 || number > 100000 || number != Math.Truncate(number) || ratio < 0)
                throw new ArgumentException("离群点滤波的邻居数量或标准差倍数无效");
            int k = Math.Max(1, Math.Min((int)number, cloud.Count - 1));
            var distances = new double[cloud.Count];
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, cloud.Count, 2048), options, range => {
                var heap = new double[k];
                for (int i = range.Item1; i < range.Item2; i++)
                { if ((i & 255) == 0) ctx.CancellationToken.ThrowIfCancellationRequested(); distances[i] = tree.MeanDistance(i, heap); }
            });
            double mean = distances.Average();
            double std = cloud.Count > 1 ? Math.Sqrt(distances.Sum(d => (d - mean) * (d - mean)) / (cloud.Count - 1)) : 0;
            double limit = mean + ratio * std;
            for (int i = 0; i < cloud.Count; i++) keep[i] = distances[i] <= limit;
        }
        else if (method == "Radius outlier removal")
        {
            var radius = p.GroupDbl(method, "Search Radius", .5);
            var minimum = p.GroupDbl(method, "Minimum Points In Radius", 1);
            if (!double.IsFinite(radius + minimum) || radius <= 0 || minimum < 1 || minimum > int.MaxValue || minimum != Math.Truncate(minimum))
                throw new ArgumentException("离群点滤波半径或最少点数无效");
            Parallel.For(0, cloud.Count, options, i => keep[i] = tree.HasNeighbors(i, radius, (int)minimum));
        }
        else throw new ArgumentException("无效的离群点滤波方法：" + method);
        var result = new PointCloud(); var colors = cloud.ColorsRgb?.Length == cloud.Count ? new List<uint>() : null;
        for (int i = 0; i < cloud.Count; i++)
        {
            if (!keep[i]) continue;
            result.Add(cloud.X[i], cloud.Y[i], cloud.Z[i], i < cloud.I.Count ? cloud.I[i] : 0); colors?.Add(cloud.ColorsRgb[i]);
        }
        result.ColorsRgb = colors?.ToArray(); return result;
    }
}
