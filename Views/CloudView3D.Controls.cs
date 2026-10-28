using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AiVisionStudio.Algorithm;
using OpenCvSharp;
using Point = System.Windows.Point;

namespace AiVisionStudio.Views;

public sealed partial class CloudView3D
{
    public bool AxesVisible { get; set; } = true;
    public bool GridVisible { get; set; } = true;
    public bool BoundsVisible { get; set; }
    public bool OrientedBoundsVisible { get; set; }
    public bool ResultsVisible { get; set; } = true;
    public bool Perspective { get; set; }
    public bool PanWithLeftButton { get; set; }
    public bool DistanceMode { get; set; }
    public string ColorMode { get; set; } = "Height";
    private double? _rangeMinimum, _rangeMaximum;
    private Vector3D[] _orientedCorners;
    private readonly List<Vector3D> _distancePoints = new();
    public string DistanceText { get; private set; } = "";
    public PointCloud SnapshotCloud() => _cloud?.Clone();
    public void SetColorRange(double? minimum, double? maximum)
    {
        if (minimum.HasValue != maximum.HasValue || minimum.HasValue &&
            (!double.IsFinite(minimum.Value) || !double.IsFinite(maximum.Value) || minimum >= maximum))
            throw new ArgumentException("色带下限必须小于上限，且为有限数值");
        _rangeMinimum=minimum;_rangeMaximum=maximum;InvalidateVisual();
    }
    public (double Minimum,double Maximum) ColorRange => (_rangeMinimum ?? _colorMin,_rangeMaximum ?? _colorMax);
    public void SetView(string view)
    {
        (_yaw,_pitch)=view switch
        {
            "Top" => (0, 0),
            "Bottom" => (0, Math.PI),
            "Front" => (0, Math.PI / 2),
            "Back" => (Math.PI, Math.PI / 2),
            "Right" => (-Math.PI / 2, Math.PI / 2),
            "Left" => (Math.PI / 2, Math.PI / 2),
            "Isometric" => (-Math.PI/4,Math.PI/4),
            _ => (0,0)
        };
        _fitYaw = _yaw; _fitPitch = _pitch;
        _zoom=1;_panX=_panY=0;InvalidateVisual();
    }
    public void RotateBy(double angle, double pitch = 0)
    { if (!double.IsFinite(angle) || !double.IsFinite(pitch)) return; _yaw=NormalizeAngle(_yaw+angle);_pitch=NormalizeAngle(_pitch+pitch);InvalidateVisual(); }
    private sealed record CompassMarker(Point Position, string Label, string View, Color Color, bool Positive, double Depth);
    private IEnumerable<CompassMarker> CompassMarkers(double width, double height)
    {
        var origin = new Point(width - 55, height - 55);
        var axes = new[] { (X: 1d,Y: 0d,Z: 0d,Name: "X",Positive: "Right",Negative: "Left",Color: Colors.LightCoral),
            (X: 0d,Y: 1d,Z: 0d,Name: "Y",Positive: "Back",Negative: "Front",Color: Colors.LightGreen),
            (X: 0d,Y: 0d,Z: 1d,Name: "Z",Positive: "Top",Negative: "Bottom",Color: Colors.LightSkyBlue) };
        return axes.SelectMany(axis => new[] { -1, 1 }.Select(sign => {
            var p = Rotate(axis.X * sign, axis.Y * sign, axis.Z * sign);
            var position = new Point(origin.X + p.x * 32, origin.Y - p.y * 32);
            if (Math.Abs(p.x) + Math.Abs(p.y) < .2) position.X += sign * 13;
            return new CompassMarker(position, (sign > 0 ? "+" : "−") + axis.Name,
                sign > 0 ? axis.Positive : axis.Negative, axis.Color, sign > 0, p.depth);
        })).OrderBy(m => m.Depth);
    }
    private bool TryCompassView(Point point)
    {
        PrepareProjection(ActualWidth, ActualHeight);
        var target = CompassMarkers(ActualWidth, ActualHeight).OrderBy(m => (m.Position - point).LengthSquared).FirstOrDefault();
        if (target == null || (target.Position - point).Length > 11) return false;
        SetView(target.View); return true;
    }
    public void ClearMeasurement() { _distancePoints.Clear();DistanceText="";InvalidateVisual(); }
    public bool PickDistanceAt(Point screen)
    {
        if(!HasCloud)return false;
        PrepareProjection(ActualWidth,ActualHeight);
        var nearest=100d;var depth=double.NegativeInfinity;Vector3D? picked=null;
        var visible=_cloud.DisplayIndices;
        for(var position=0;position<(visible?.Length??_cloud.Count);position++)
        {
            var i=visible==null?position:visible[position];if(i<0||i>=_cloud.Count)continue;
            var x=_cloud.X[i];var y=_cloud.Y[i];var z=_cloud.Z[i];
            if(!float.IsFinite(x)||!float.IsFinite(y)||!float.IsFinite(z))continue;
            var q=Project(x,y,z);var d=(screen.X-q.x)*(screen.X-q.x)+(screen.Y-q.y)*(screen.Y-q.y);
            if(d<nearest-.01||Math.Abs(d-nearest)<.01&&q.depth>depth){nearest=d;depth=q.depth;picked=new(x,y,z);}
        }
        if(picked==null)return false;
        if(_distancePoints.Count==2)_distancePoints.Clear();
        _distancePoints.Add(picked.Value);
        DistanceText=_distancePoints.Count==2?(_distancePoints[1]-_distancePoints[0]).Length.ToString("0.###",CultureInfo.InvariantCulture)+" mm":"已选第一个点，请选择第二个点";
        InvalidateVisual();return true;
    }
    private void DrawViewBounds(DrawingContext dc)
    {
        var pen=new Pen(Brushes.LightSkyBlue,1);
        if(BoundsVisible)
        {
            var corners=new[]{new Vector3D(_x1,_y1,_z1),new(_x2,_y1,_z1),new(_x2,_y2,_z1),new(_x1,_y2,_z1),new(_x1,_y1,_z2),new(_x2,_y1,_z2),new(_x2,_y2,_z2),new(_x1,_y2,_z2)};
            Draw(corners);
        }
        if(OrientedBoundsVisible)
        {
            _orientedCorners??=ComputeOrientedBounds();pen=new Pen(Brushes.Yellow,1);if(_orientedCorners!=null)Draw(_orientedCorners);
        }
        void Draw(Vector3D[] corners){foreach(var(a,b)in Edges)dc.DrawLine(pen,Screen(Project(corners[a].X,corners[a].Y,corners[a].Z)),Screen(Project(corners[b].X,corners[b].Y,corners[b].Z)));}
    }
    private Vector3D[] ComputeOrientedBounds()
    {
        if(!HasCloud)return null;
        var indices=_cloud.DisplayIndices??Enumerable.Range(0,_cloud.Count).ToArray();
        var points=indices.Where(i=>i>=0&&i<_cloud.Count).Select(i=>new Vector3D(_cloud.X[i],_cloud.Y[i],_cloud.Z[i])).Where(p=>double.IsFinite(p.X)&&double.IsFinite(p.Y)&&double.IsFinite(p.Z)).ToArray();
        if(points.Length==0)return null;
        var center=points.Aggregate(new Vector3D(),(sum,p)=>sum+p)/points.Length;
        using var covariance=Mat.Zeros(3,3,MatType.CV_64FC1).ToMat();
        var matrix=new double[3,3];foreach(var p in points){var q=p-center;var c=new[]{q.X,q.Y,q.Z};for(var i=0;i<3;i++)for(var j=0;j<3;j++)matrix[i,j]+=c[i]*c[j];}
        for(var i=0;i<3;i++)for(var j=0;j<3;j++)covariance.Set(i,j,matrix[i,j]);
        using var values=new Mat();using var vectors=new Mat();Cv2.Eigen(covariance,values,vectors);
        var basis=Enumerable.Range(0,3).Select(i=>new Vector3D(vectors.At<double>(i,0),vectors.At<double>(i,1),vectors.At<double>(i,2))).ToArray();
        var lo=Enumerable.Repeat(double.PositiveInfinity,3).ToArray();var hi=Enumerable.Repeat(double.NegativeInfinity,3).ToArray();
        foreach(var p in points)for(var i=0;i<3;i++){var d=Vector3D.DotProduct(p-center,basis[i]);lo[i]=Math.Min(lo[i],d);hi[i]=Math.Max(hi[i],d);}
        return new[]{new[]{0,0,0},new[]{1,0,0},new[]{1,1,0},new[]{0,1,0},new[]{0,0,1},new[]{1,0,1},new[]{1,1,1},new[]{0,1,1}}
            .Select(c=>center+basis[0]*(c[0]==0?lo[0]:hi[0])+basis[1]*(c[1]==0?lo[1]:hi[1])+basis[2]*(c[2]==0?lo[2]:hi[2])).ToArray();
    }
    private void DrawMeasurement(DrawingContext dc)
    {
        if(_distancePoints.Count==0)return;
        var points=_distancePoints.Select(p=>Screen(Project(p.X,p.Y,p.Z))).ToArray();
        foreach(var p in points)dc.DrawEllipse(Brushes.Yellow,null,p,4,4);
        if(points.Length==2)dc.DrawLine(new Pen(Brushes.Yellow,2),points[0],points[1]);
        dc.DrawText(Text(DistanceText,13,Colors.Yellow,FontWeights.Normal),new Point(12,12));
    }
}
