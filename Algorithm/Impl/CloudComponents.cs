namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Exact radius-connected components. Consumed neighbors leave the spatial buckets.</summary>
internal static class CloudComponents
{
    public static List<List<int>> Find(PointCloud cloud,IEnumerable<int> selection,double radius,CancellationToken cancellation)
    {
        if(!double.IsFinite(radius)||radius<=0)throw new ArgumentException("点间距离必须为正有限数值");
        var cells=new Dictionary<(long,long,long),List<int>>();
        (long,long,long) Key(int i)
        {
            var x=Math.Floor(cloud.X[i]/radius);var y=Math.Floor(cloud.Y[i]/radius);var z=Math.Floor(cloud.Z[i]/radius);
            if(!double.IsFinite(x+y+z)||Math.Max(Math.Abs(x),Math.Max(Math.Abs(y),Math.Abs(z)))>9e18)throw new ArgumentException("点云含无效坐标或距离过小");
            return((long)x,(long)y,(long)z);
        }
        var indexes=selection.Distinct().ToArray();
        foreach(var i in indexes){var key=Key(i);if(!cells.TryGetValue(key,out var list))cells[key]=list=new();list.Add(i);}
        var visited=new bool[cloud.Count];var result=new List<List<int>>();var squared=radius*radius;
        foreach(var seed in indexes)
        {
            if(visited[seed])continue;cancellation.ThrowIfCancellationRequested();
            visited[seed]=true;var members=new List<int>{seed};
            for(int pos=0;pos<members.Count;pos++)
            {
                if((pos&1023)==0)cancellation.ThrowIfCancellationRequested();
                var at=members[pos];var (x,y,z)=Key(at);
                for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++)for(int dz=-1;dz<=1;dz++)
                {
                    var key=(x+dx,y+dy,z+dz);if(!cells.TryGetValue(key,out var candidates))continue;
                    for(int k=candidates.Count-1;k>=0;k--)
                    {
                        var next=candidates[k];var vx=(double)cloud.X[at]-cloud.X[next];var vy=(double)cloud.Y[at]-cloud.Y[next];var vz=(double)cloud.Z[at]-cloud.Z[next];
                        if(!visited[next]&&vx*vx+vy*vy+vz*vz>squared)continue;
                        if(!visited[next]){visited[next]=true;members.Add(next);}
                        candidates[k]=candidates[^1];candidates.RemoveAt(candidates.Count-1);
                    }
                    if(candidates.Count==0)cells.Remove(key);
                }
            }
            result.Add(members);
        }
        return result;
    }
}
