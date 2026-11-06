using System;
using UnityEngine;

namespace YLFramework.Overlap
{
    /// <summary>
    /// Caller-owned, main-thread geometry storage for Sector queries. No Unity object references or geometry cache.
    /// Defaults: 256 native shapes/collider, 4096 vertices/shape, 1e-5 world-unit narrow-phase tolerance.
    /// Limits are checked against trusted type bounds BEFORE GetShapes; capacities never silently grow.
    /// </summary>
    public sealed class OverlapQueryWorkspace2D : IDisposable
    {
        public int MaxShapesPerCollider
        {
            get;
        }
        public int MaxVerticesPerShape
        {
            get;
        }
        public float GeometryTolerance
        {
            get;
        }
        private PhysicsShapeGroup2D _group;
        private Point[] _vertices;
        private bool _inUse;
        private const double Roundoff = 2.2204460492503131e-16;

        public OverlapQueryWorkspace2D(int maxShapesPerCollider = 256, int maxVerticesPerShape = 4096,
            float geometryTolerance = 0.00001f)
        {
            ValidateConfiguration(maxShapesPerCollider, maxVerticesPerShape, geometryTolerance);
            MaxShapesPerCollider = maxShapesPerCollider;
            MaxVerticesPerShape = maxVerticesPerShape;
            GeometryTolerance = geometryTolerance;
            _group = new PhysicsShapeGroup2D(1, maxVerticesPerShape);
            _vertices = new Point[maxVerticesPerShape];
        }

        internal static void ValidateConfiguration(int maxShapesPerCollider, int maxVerticesPerShape, float geometryTolerance)
        {
            if (maxShapesPerCollider <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxShapesPerCollider));
            }
            if (maxVerticesPerShape <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxVerticesPerShape));
            }
            if (!OverlapShape2D.IsFinite(geometryTolerance) || geometryTolerance <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(geometryTolerance));
            }
        }

        public void Dispose()
        {
            if (_inUse)
            {
                throw new InvalidOperationException("Cannot dispose a workspace during its query.");
            }
            _group = null;
            _vertices = null;
        }

        internal void Begin()
        {
            if (_group == null)
            {
                throw new ObjectDisposedException(nameof(OverlapQueryWorkspace2D));
            }
            if (_inUse)
            {
                throw new InvalidOperationException("A workspace cannot be shared by reentrant queries.");
            }
            _inUse = true;
        }

        internal void End()
        {
            _group.Clear();
            _inUse = false;
        }

        internal bool TryBroadRadius(float outerRadius, out float radius)
        {
            double expanded = (double)outerRadius + GeometryTolerance;
            radius = (float)expanded;
            if (!OverlapShape2D.IsFinite(radius))
            {
                return false;
            }
            // Round outward: the native broad phase must also include the declared narrow-phase tolerance.
            if (radius < expanded)
            {
                radius = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(radius) + 1);
            }
            return OverlapShape2D.IsFinite(radius);
        }

        internal OverlapQueryInvalidationReason Intersects(Collider2D collider, in OverlapShape2D sector, out bool hit)
        {
            hit = false;
            int shapeCount = collider.shapeCount;
            long vertexBound;
            if (collider is CircleCollider2D)
            {
                vertexBound = 1;
            }
            else if (collider is CapsuleCollider2D)
            {
                vertexBound = 2;
            }
            else if (collider is BoxCollider2D || collider is PolygonCollider2D)
            {
                vertexBound = Physics2D.MaxPolygonShapeVertices;
            }
            else if (collider is EdgeCollider2D edge)
            {
                vertexBound = edge.pointCount;
            }
            else if (collider is CompositeCollider2D composite)
            {
                if (composite.geometryType == CompositeCollider2D.GeometryType.Polygons)
                {
                    vertexBound = Physics2D.MaxPolygonShapeVertices;
                }
                else if (composite.geometryType == CompositeCollider2D.GeometryType.Outlines)
                {
                    // A closed native chain repeats its first point. Total authored points bound every path.
                    vertexBound = (long)composite.pointCount + 1;
                }
                else
                {
                    return OverlapQueryInvalidationReason.UnsupportedGeometry;
                }
            }
            else
            {
                // Tilemap/Custom/unknown types have no established pre-extraction per-shape vertex bound here.
                return OverlapQueryInvalidationReason.UnsupportedGeometry;
            }
            if (shapeCount <= 0 || vertexBound <= 0)
            {
                return OverlapQueryInvalidationReason.InvalidGeometry;
            }
            if (shapeCount > MaxShapesPerCollider || vertexBound > MaxVerticesPerShape)
            {
                return OverlapQueryInvalidationReason.GeometryBudgetExceeded;
            }
            var region = new Region(sector, GeometryTolerance);
            double rotation = (sector.RotationDegrees % 360d) * (Math.PI / 180d);
            double cosine = Math.Cos(rotation);
            double sine = Math.Sin(rotation);
            for (int i = 0; i < shapeCount; i++)
            {
                int read;
                try
                {
                    read = collider.GetShapes(_group, i, 1);
                }
                catch (ArgumentException)
                {
                    return OverlapQueryInvalidationReason.InvalidGeometry;
                }
                catch (InvalidOperationException)
                {
                    return OverlapQueryInvalidationReason.InvalidGeometry;
                }
                if (read != 1 || _group.shapeCount != 1)
                {
                    return OverlapQueryInvalidationReason.InvalidGeometry;
                }
                var shape = _group.GetShape(0);
                int count = shape.vertexCount;
                if (!OverlapShape2D.IsFinite(shape.radius) || shape.radius < 0 || count > vertexBound || count <= 0)
                {
                    return OverlapQueryInvalidationReason.InvalidGeometry;
                }
                switch (shape.shapeType)
                {
                    case PhysicsShapeType2D.Circle:
                        if (count != 1)
                        {
                            return OverlapQueryInvalidationReason.InvalidGeometry;
                        }
                        break;
                    case PhysicsShapeType2D.Capsule:
                        if (count != 2)
                        {
                            return OverlapQueryInvalidationReason.InvalidGeometry;
                        }
                        break;
                    case PhysicsShapeType2D.Polygon:
                        if (count < 3 || count > Physics2D.MaxPolygonShapeVertices)
                        {
                            return OverlapQueryInvalidationReason.InvalidGeometry;
                        }
                        break;
                    case PhysicsShapeType2D.Edges:
                        if (count < 2)
                        {
                            return OverlapQueryInvalidationReason.InvalidGeometry;
                        }
                        break;
                    default:
                        return OverlapQueryInvalidationReason.UnsupportedGeometry;
                }
                var matrix = _group.localToWorldMatrix;
                for (int j = 0; j < count; j++)
                {
                    var vertex = _group.GetShapeVertex(0, j);
                    // Exported vertices already include collider offset/scale; only the returned body pose is applied.
                    if (!TryRelativeCoordinate((double)matrix.m00 * vertex.x, (double)matrix.m01 * vertex.y,
                        matrix.m03, sector.Center.x, GeometryTolerance, out double x) ||
                        !TryRelativeCoordinate((double)matrix.m10 * vertex.x, (double)matrix.m11 * vertex.y,
                        matrix.m13, sector.Center.y, GeometryTolerance, out double y))
                    {
                        return OverlapQueryInvalidationReason.InvalidGeometry;
                    }
                    double rotatedX = cosine * x + sine * y;
                    double rotatedY = -sine * x + cosine * y;
                    double errorX = ProductErrorBound(cosine, x, cosine * x) + ProductErrorBound(sine, y, sine * y) +
                        Math.Abs(SumError(cosine * x, sine * y, rotatedX));
                    double errorY = ProductErrorBound(-sine, x, -sine * x) + ProductErrorBound(cosine, y, cosine * y) +
                        Math.Abs(SumError(-sine * x, cosine * y, rotatedY));
                    if (Math.Max(errorX, errorY) > GeometryTolerance / 16d)
                    {
                        return OverlapQueryInvalidationReason.InvalidGeometry;
                    }
                    _vertices[j] = new Point(rotatedX, rotatedY);
                }
                double margin = (double)shape.radius + GeometryTolerance;
                double squaredMargin = margin * margin;
                if (shape.shapeType == PhysicsShapeType2D.Circle)
                {
                    double distance = region.PointDistanceSquared(_vertices[0]);
                    if (double.IsNaN(distance) || double.IsInfinity(distance))
                    {
                        return OverlapQueryInvalidationReason.InvalidGeometry;
                    }
                    hit |= distance <= squaredMargin;
                }
                else
                {
                    int segments = shape.shapeType == PhysicsShapeType2D.Polygon ? count : count - 1;
                    for (int j = 0; j < segments; j++)
                    {
                        double distance = region.SegmentDistanceSquared(_vertices[j], _vertices[(j + 1) % count]);
                        if (double.IsNaN(distance) || double.IsInfinity(distance))
                        {
                            return OverlapQueryInvalidationReason.InvalidGeometry;
                        }
                        hit |= distance <= squaredMargin;
                    }
                    if (shape.shapeType == PhysicsShapeType2D.Polygon)
                    {
                        double area = 0;
                        bool positive = false;
                        bool negative = false;
                        var representative = new Point(((double)sector.InnerRadius + sector.OuterRadius) * 0.5d, 0);
                        for (int j = 0; j < count; j++)
                        {
                            var a = _vertices[j];
                            var b = _vertices[(j + 1) % count];
                            area += Point.Cross(a, b);
                            double side = Point.Cross(b - a, representative - a);
                            positive |= side > 0;
                            negative |= side < 0;
                        }
                        if (area == 0)
                        {
                            return OverlapQueryInvalidationReason.InvalidGeometry;
                        }
                        // Boundary distances alone miss a filled convex shape enclosing the entire region.
                        hit |= !(positive && negative);
                    }
                }
            }
            return OverlapQueryInvalidationReason.None;
        }

        private static bool TryRelativeCoordinate(double first, double second, double translation, double center,
            double tolerance, out double coordinate)
        {
            // Float matrix*vertex products are exact in double. Cancel the common translation BEFORE adding them.
            double delta = translation - center;
            double local = first + second;
            coordinate = delta + local;
            double error = Math.Abs(SumError(translation, -center, delta)) + Math.Abs(SumError(first, second, local)) +
                Math.Abs(SumError(delta, local, coordinate));
            return !double.IsNaN(coordinate) && !double.IsInfinity(coordinate) && error <= tolerance / 16d;
        }

        private static double SumError(double first, double second, double sum)
        {
            double recovered = sum - first;
            return (first - (sum - recovered)) + (second - recovered);
        }

        private static double ProductErrorBound(double first, double second, double product) =>
            first == 0 || Math.Abs(first) == 1 || second == 0 || Math.Abs(second) == 1 ? 0 :
            4 * Roundoff * Math.Abs(product);

        private readonly struct Point
        {
            internal readonly double X;
            internal readonly double Y;
            internal Point(double x, double y)
            {
                X = x;
                Y = y;
            }
            internal double Squared => X * X + Y * Y;
            public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
            public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y);
            public static Point operator *(Point a, double scale) => new Point(a.X * scale, a.Y * scale);
            internal static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y;
            internal static double Cross(Point a, Point b) => a.X * b.Y - a.Y * b.X;
        }

        private readonly struct Region
        {
            // NaN marks uncertified cancellation/interval decisions; the query converts it to InvalidGeometry, never a miss.
            private readonly double _outer;
            private readonly double _inner;
            private readonly double _halfAngle;
            private readonly bool _full;
            private readonly Point _start;
            private readonly Point _end;
            private readonly double _precisionBudget;

            internal Region(in OverlapShape2D sector, double tolerance)
            {
                _outer = sector.OuterRadius;
                _inner = sector.InnerRadius;
                _halfAngle = sector.AngleDegrees * (Math.PI / 360d);
                _full = sector.AngleDegrees == 360;
                _start = new Point(Math.Cos(_halfAngle), -Math.Sin(_halfAngle));
                _end = new Point(_start.X, -_start.Y);
                _precisionBudget = tolerance / 16d;
            }

            private bool InAngle(Point point) => _full || Math.Abs(Math.Atan2(point.Y, point.X)) <= _halfAngle;

            internal double PointDistanceSquared(Point point)
            {
                double radius = Math.Sqrt(point.Squared);
                if ((radius == 0 && _inner == 0) || radius >= _inner && radius <= _outer && InAngle(point))
                {
                    return 0;
                }
                double distance = PointArcDistanceSquared(point, _outer);
                if (_inner > 0)
                {
                    distance = Math.Min(distance, PointArcDistanceSquared(point, _inner));
                }
                if (!_full)
                {
                    distance = Math.Min(distance, PointSegmentDistanceSquared(point, _start * _inner, _start * _outer));
                    distance = Math.Min(distance, PointSegmentDistanceSquared(point, _end * _inner, _end * _outer));
                }
                return distance;
            }

            private double PointArcDistanceSquared(Point point, double radius)
            {
                if (InAngle(point) || point.Squared == 0)
                {
                    double distance = Math.Sqrt(point.Squared) - radius;
                    return distance * distance;
                }
                return Math.Min((point - _start * radius).Squared, (point - _end * radius).Squared);
            }

            internal double SegmentDistanceSquared(Point a, Point b)
            {
                // Once the running minimum is reliably zero, no remaining boundary can reduce it.
                double distance = Math.Min(PointDistanceSquared(a), PointDistanceSquared(b));
                if (distance == 0)
                {
                    return 0;
                }
                distance = Math.Min(distance, SegmentArcDistanceSquared(a, b, _outer));
                if (distance == 0)
                {
                    return 0;
                }
                if (_inner > 0)
                {
                    distance = Math.Min(distance, SegmentArcDistanceSquared(a, b, _inner));
                    if (distance == 0)
                    {
                        return 0;
                    }
                }
                if (!_full)
                {
                    distance = Math.Min(distance, SegmentSegmentDistanceSquared(a, b, _start * _inner, _start * _outer));
                    if (distance == 0)
                    {
                        return 0;
                    }
                    distance = Math.Min(distance, SegmentSegmentDistanceSquared(a, b, _end * _inner, _end * _outer));
                    if (distance == 0)
                    {
                        return 0;
                    }
                }
                return distance;
            }

            private double SegmentArcDistanceSquared(Point a, Point b, double radius)
            {
                double distance = Math.Min(PointArcDistanceSquared(a, radius), PointArcDistanceSquared(b, radius));
                distance = Math.Min(distance, PointSegmentDistanceSquared(_start * radius, a, b));
                distance = Math.Min(distance, PointSegmentDistanceSquared(_end * radius, a, b));
                if ((b - a).Squared == 0)
                {
                    return distance;
                }
                var line = new SegmentLine(a, b);
                if (!line.Reliable(radius, _precisionBudget))
                {
                    return double.NaN;
                }
                double radialGap = radius - Math.Abs(line.Offset);
                if (radialGap >= -line.OffsetError)
                {
                    double radialSum = radius + Math.Abs(line.Offset);
                    double squared = Math.Max(0, radialGap * radialSum);
                    double error = 8 * Roundoff * squared + 2 * Math.Abs(line.Offset) * line.OffsetError +
                        radialSum * Math.Abs(SumError(radius, -Math.Abs(line.Offset), radialGap)) +
                        Math.Abs(radialGap) * Math.Abs(SumError(radius, Math.Abs(line.Offset), radialSum));
                    double offset = line.Offset == 0 && line.OffsetError == 0 ? radius : Math.Sqrt(squared);
                    double rootError = line.OffsetError == 0 && (line.Offset == 0 || radialGap == 0) ? 0 :
                        Math.Sqrt(squared + error) - Math.Sqrt(Math.Max(0, squared - error)) + 8 * Roundoff * offset;
                    if (rootError > _precisionBudget || !line.RangeReliable(-offset, rootError, _precisionBudget) ||
                        !line.RangeReliable(offset, rootError, _precisionBudget))
                    {
                        return double.NaN;
                    }
                    // Do not reconstruct roots via a + direction*t: enormous segments can round both t values to 0.5.
                    var nearest = line.Normal * line.Offset;
                    if (-offset >= line.Start && -offset <= line.End && InAngle(nearest - line.Unit * offset) ||
                        offset >= line.Start && offset <= line.End && InAngle(nearest + line.Unit * offset))
                    {
                        return 0;
                    }
                }
                // Interior extrema have the circle radius perpendicular to the segment; both sides matter for >180 degrees.
                var normal = line.Normal * radius;
                if (InAngle(normal))
                {
                    distance = Math.Min(distance, PointSegmentDistanceSquared(normal, a, b));
                }
                normal = normal * -1;
                if (InAngle(normal))
                {
                    distance = Math.Min(distance, PointSegmentDistanceSquared(normal, a, b));
                }
                return distance;
            }

            private double PointSegmentDistanceSquared(Point point, Point a, Point b)
            {
                if ((b - a).Squared == 0)
                {
                    return (point - a).Squared;
                }
                var line = new SegmentLine(a, b);
                double along = Point.Dot(point, line.Unit);
                double alongError = line.ProjectionError(point, line.Unit);
                if (!line.Reliable(0, _precisionBudget) || !line.RangeReliable(along, alongError, _precisionBudget))
                {
                    return double.NaN;
                }
                if (along < line.Start)
                {
                    return (point - a).Squared;
                }
                if (along > line.End)
                {
                    return (point - b).Squared;
                }
                double projection = Point.Dot(point, line.Normal);
                double perpendicular = projection - line.Offset;
                if (line.ProjectionError(point, line.Normal) + line.OffsetError +
                    Math.Abs(SumError(projection, -line.Offset, perpendicular)) > _precisionBudget)
                {
                    return double.NaN;
                }
                return perpendicular * perpendicular;
            }

            private double SegmentSegmentDistanceSquared(Point a, Point b, Point c, Point d)
            {
                var first = b - a;
                var second = d - c;
                double cross = Point.Cross(first, second);
                if (cross != 0)
                {
                    double t = Point.Cross(c - a, second) / cross;
                    double u = Point.Cross(c - a, first) / cross;
                    if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
                    {
                        return 0;
                    }
                }
                return Math.Min(Math.Min(PointSegmentDistanceSquared(a, c, d), PointSegmentDistanceSquared(b, c, d)),
                    Math.Min(PointSegmentDistanceSquared(c, a, b), PointSegmentDistanceSquared(d, a, b)));
            }
        }

        private readonly struct SegmentLine
        {
            internal readonly Point Unit;
            internal readonly Point Normal;
            internal readonly double Offset;
            internal readonly double OffsetError;
            internal readonly double Start;
            internal readonly double End;
            private readonly double _unitError;
            private readonly double _startError;
            private readonly double _endError;

            internal SegmentLine(Point a, Point b)
            {
                var direction = b - a;
                if (direction.X == 0 || direction.Y == 0)
                {
                    Unit = direction.X == 0 ? new Point(0, Math.Sign(direction.Y)) : new Point(Math.Sign(direction.X), 0);
                    _unitError = 0;
                    Normal = new Point(-Unit.Y, Unit.X);
                    Offset = Point.Dot(a, Normal);
                    OffsetError = 0;
                }
                else
                {
                    double length = Math.Sqrt(direction.Squared);
                    Unit = direction * (1 / length);
                    _unitError = 16 * Roundoff + 4 * (Math.Abs(SumError(b.X, -a.X, direction.X)) +
                        Math.Abs(SumError(b.Y, -a.Y, direction.Y))) / length;
                    Normal = new Point(-Unit.Y, Unit.X);
                    double first = a.X * b.Y;
                    double second = a.Y * b.X;
                    double cross = first - second;
                    Offset = -cross / length;
                    OffsetError = (ProductErrorBound(a.X, b.Y, first) + ProductErrorBound(a.Y, b.X, second) +
                        Math.Abs(SumError(first, -second, cross))) / length + Math.Abs(Offset) * (_unitError + 4 * Roundoff);
                }
                Start = Point.Dot(a, Unit);
                End = Point.Dot(b, Unit);
                _startError = _endError = 0;
                _startError = ProjectionError(a, Unit);
                _endError = ProjectionError(b, Unit);
            }

            internal bool Reliable(double scale, double budget) =>
                !double.IsNaN(OffsetError) && !double.IsInfinity(OffsetError) &&
                OffsetError + _unitError * scale <= budget && Start <= End;

            internal double ProjectionError(Point point, Point axis)
            {
                double first = point.X * axis.X;
                double second = point.Y * axis.Y;
                return ProductErrorBound(point.X, axis.X, first) + ProductErrorBound(point.Y, axis.Y, second) +
                    Math.Abs(SumError(first, second, first + second)) + _unitError * (Math.Abs(point.X) + Math.Abs(point.Y));
            }

            internal bool RangeReliable(double value, double error, double budget) =>
                !(Math.Abs(value - Start) <= error + _startError && error + _startError > budget) &&
                !(Math.Abs(value - End) <= error + _endError && error + _endError > budget);
        }
    }
}
