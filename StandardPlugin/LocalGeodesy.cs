using NetTopologySuite.Geometries;
using System;

namespace AgGateway.ADAPT.StandardPlugin
{
    /// <summary>
    /// Temporary east/north meter grid centered on a geographic position.
    ///
    /// ADAPT and GeoParquet coordinates remain longitude/latitude in degrees. Coverage geometry
    /// is calculated as meter offsets from the current GPS fix, then converted back to degrees.
    ///
    /// This is a local equirectangular projection: latitude uses R * pi / 180 meters per degree;
    /// longitude uses that scale times cos(origin latitude), accounting for meridians converging
    /// toward the poles. The origin is reset for every fix, so the calculation stays near the
    /// point where this flat approximation is most accurate. Unlike UTM, it needs no zone and is
    /// never used as a persistent projected coordinate system.
    ///
    /// Within <see cref="MaxValidatedDisplacementM"/>, this agrees with the plugin's spherical
    /// haversine calculations to about one centimeter; longer distance and bearing calculations
    /// use haversine instead.
    ///
    /// Headings are compass bearings: 0 degrees is north and 90 degrees is east. Therefore a
    /// distance at a heading resolves to east = distance * sin(heading) and north = distance *
    /// cos(heading). Bearings use atan2(east, north) to preserve that compass convention and the
    /// correct quadrant.
    /// </summary>
    internal readonly struct LocalGeodesy
    {
        // GeometryExporter uses haversine beyond this validated local range.
        internal const double MaxValidatedDisplacementM = 100d;

        private readonly double _originLonDeg;
        private readonly double _originLatDeg;
        private readonly double _metersPerDegreeLon;
        private readonly double _metersPerDegreeLat;

        internal LocalGeodesy(double originLonDeg, double originLatDeg)
        {
            _originLonDeg = originLonDeg;
            _originLatDeg = originLatDeg;
            double cosLat = Math.Cos(GeometryExporter.DegreesToRads(originLatDeg));
            _metersPerDegreeLon = GeometryExporter.EarthRadiusM * cosLat * (Math.PI / 180d);
            _metersPerDegreeLat = GeometryExporter.EarthRadiusM * (Math.PI / 180d);
        }

        internal bool IsWithinValidatedEnvelope(double displacementM)
        {
            return displacementM <= MaxValidatedDisplacementM;
        }

        internal void ToLocal(double lonDeg, double latDeg, out double eastM, out double northM)
        {
            eastM = (lonDeg - _originLonDeg) * _metersPerDegreeLon;
            northM = (latDeg - _originLatDeg) * _metersPerDegreeLat;
        }

        internal void ToGeographic(double eastM, double northM, out double lonDeg, out double latDeg)
        {
            lonDeg = _originLonDeg + (eastM / _metersPerDegreeLon);
            latDeg = _originLatDeg + (northM / _metersPerDegreeLat);
        }

        internal Point ToGeographic(double eastM, double northM)
        {
            ToGeographic(eastM, northM, out double lonDeg, out double latDeg);
            return new Point(lonDeg, latDeg);
        }

        internal LocalPoint ToLocalPoint(double lonDeg, double latDeg)
        {
            ToLocal(lonDeg, latDeg, out double eastM, out double northM);
            return new LocalPoint(eastM, northM);
        }

        internal Point DestinationFromOrigin(double distanceM, double headingDeg)
        {
            double headingRad = GeometryExporter.DegreesToRads(headingDeg);
            double eastM = distanceM * Math.Sin(headingRad);
            double northM = distanceM * Math.Cos(headingRad);
            return ToGeographic(eastM, northM);
        }

        internal double DistanceBetween(double lon1, double lat1, double lon2, double lat2)
        {
            ToLocal(lon1, lat1, out double east1, out double north1);
            ToLocal(lon2, lat2, out double east2, out double north2);
            double deltaEast = east2 - east1;
            double deltaNorth = north2 - north1;
            return Math.Sqrt((deltaEast * deltaEast) + (deltaNorth * deltaNorth));
        }

        internal double BearingBetween(double lon1, double lat1, double lon2, double lat2)
        {
            ToLocal(lon1, lat1, out double east1, out double north1);
            ToLocal(lon2, lat2, out double east2, out double north2);
            return GeometryExporter.RadsToDegrees(Math.Atan2(east2 - east1, north2 - north1));
        }

        internal LocalPoint DestinationLocal(LocalPoint start, double distanceM, double headingDeg)
        {
            double headingRad = GeometryExporter.DegreesToRads(headingDeg);
            return new LocalPoint(
                start.EastM + (distanceM * Math.Sin(headingRad)),
                start.NorthM + (distanceM * Math.Cos(headingRad)));
        }

        // Cross-product orientation detects crossings and edge touches, which identify invalid quads.
        internal static bool SegmentsIntersect(LocalPoint a1, LocalPoint a2, LocalPoint b1, LocalPoint b2)
        {
            static double Orientation(LocalPoint p, LocalPoint q, LocalPoint r)
            {
                return ((q.EastM - p.EastM) * (r.NorthM - p.NorthM)) - ((q.NorthM - p.NorthM) * (r.EastM - p.EastM));
            }

            static bool OnSegment(LocalPoint p, LocalPoint q, LocalPoint r)
            {
                return q.EastM <= Math.Max(p.EastM, r.EastM) + 1e-9 &&
                       q.EastM + 1e-9 >= Math.Min(p.EastM, r.EastM) &&
                       q.NorthM <= Math.Max(p.NorthM, r.NorthM) + 1e-9 &&
                       q.NorthM + 1e-9 >= Math.Min(p.NorthM, r.NorthM);
            }

            double o1 = Orientation(a1, a2, b1);
            double o2 = Orientation(a1, a2, b2);
            double o3 = Orientation(b1, b2, a1);
            double o4 = Orientation(b1, b2, a2);

            if ((o1 > 0 && o2 < 0 || o1 < 0 && o2 > 0) &&
                (o3 > 0 && o4 < 0 || o3 < 0 && o4 > 0))
            {
                return true;
            }

            if (Math.Abs(o1) < 1e-9 && OnSegment(a1, b1, a2))
            {
                return true;
            }

            if (Math.Abs(o2) < 1e-9 && OnSegment(a1, b2, a2))
            {
                return true;
            }

            if (Math.Abs(o3) < 1e-9 && OnSegment(b1, a1, b2))
            {
                return true;
            }

            if (Math.Abs(o4) < 1e-9 && OnSegment(b1, a2, b2))
            {
                return true;
            }

            return false;
        }
    }

    internal readonly struct LocalPoint
    {
        internal LocalPoint(double eastM, double northM)
        {
            EastM = eastM;
            NorthM = northM;
        }

        internal double EastM { get; }
        internal double NorthM { get; }
    }
}
