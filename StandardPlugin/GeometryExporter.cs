using AgGateway.ADAPT.Standard;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Linq;
using AdaptShapes = AgGateway.ADAPT.ApplicationDataModel.Shapes;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal static class GeometryExporter
    {
        internal const double EarthRadiusM = 6378137;

        private static LinearRing ConvertToLinearRing(AdaptShapes.LinearRing srcRing)
        {
            if (srcRing == null || srcRing.Points.IsNullOrEmpty())
            {
                return null;
            }

            var coordinates = new List<CoordinateZ>(srcRing.Points.Count);
            foreach (var frameworkPoint in srcRing.Points)
            {
                var coordinate = new CoordinateZ(frameworkPoint.X, frameworkPoint.Y)
                {
                    Z = frameworkPoint.Z ?? Coordinate.NullOrdinate
                };
                coordinates.Add(coordinate);
            }
            if (coordinates.First() != coordinates.Last())
            {
                coordinates.Add(coordinates.First());
            }
            return new LinearRing(coordinates.ToArray());
        }

        private static LineString ConvertToLineString(AdaptShapes.LineString srcLine)
        {
            var coordinates = new List<CoordinateZ>();
            foreach (var frameworkLine in srcLine.Points)
            {
                var coordinate = new CoordinateZ(frameworkLine.X, frameworkLine.Y)
                {
                    Z = frameworkLine.Z ?? Coordinate.NullOrdinate
                };
                coordinates.Add(coordinate);
            }

            return new LineString(coordinates.ToArray());
        }

        private static LinearRing ConvertShapeToLinearRing(AdaptShapes.Shape srcShape)
        {
            if (srcShape is AdaptShapes.LinearRing srcLinearRing)
            {
                return ConvertToLinearRing(srcLinearRing);
            }

            return null;
        }

        internal static MultiPolygon ExportMultiPolygon(AdaptShapes.MultiPolygon srcMultiPolygon)
        {
            if (srcMultiPolygon == null || srcMultiPolygon.Polygons.IsNullOrEmpty())
            {
                return null;
            }

            var polygons = new List<Polygon>(srcMultiPolygon.Polygons.Count);
            foreach (var frameworkPolygon in srcMultiPolygon.Polygons)
            {
                var outerRing = ConvertToLinearRing(frameworkPolygon.ExteriorRing);
                var innerRings = frameworkPolygon.InteriorRings.Select(ConvertToLinearRing).ToArray();
                polygons.Add(new Polygon(outerRing, innerRings));
            }

            return new MultiPolygon(polygons.ToArray());
        }

        internal static string ExportMultiPolygonWKT(AdaptShapes.MultiPolygon srcMultiPolygon)
        {
            if (srcMultiPolygon == null)
            {
                return null;
            }
            var output = ExportMultiPolygon(srcMultiPolygon);
            if (output != null)
            {
                return output.AsText();
            }
            return null;
        }

        internal static byte[] ExportMultiPolygonWKB(AdaptShapes.MultiPolygon srcMultiPolygon)
        {
            if (srcMultiPolygon == null)
            {
                return null;
            }
            var output = ExportMultiPolygon(srcMultiPolygon);
            if (output != null)
            {
                return output.AsBinary();
            }
            return null;
        }

        internal static string ExportLineString(AdaptShapes.LineString srcLine)
        {
            if (srcLine == null || srcLine.Points.IsNullOrEmpty())
            {
                return null;
            }

            return ConvertToLineString(srcLine).ToText();
        }

        internal static string ExportPoint(AdaptShapes.Point srcPoint)
        {
            if (srcPoint == null)
            {
                return null;
            }

            var point = new Point(srcPoint.X, srcPoint.Y, srcPoint.Z ?? Coordinate.NullOrdinate);
            return point.ToText();
        }

        internal static string ExportLineStrings(List<AdaptShapes.LineString> srcLineStrings)
        {
            if (srcLineStrings.IsNullOrEmpty())
            {
                return null;
            }

            return new MultiLineString(srcLineStrings.Select(ConvertToLineString).ToArray()).ToText();
        }

        internal static Point HaversineDestination(Point startPoint, double distanceM, double headingDeg)
        {
            double latRad = DegreesToRads(startPoint.Y);
            double lonRad = DegreesToRads(startPoint.X);
            double headingRad = DegreesToRads(headingDeg);
            double length = distanceM / EarthRadiusM;
            double otherLat = Math.Asin(Math.Sin(latRad) *
                                        Math.Cos(length) +
                                        Math.Cos(latRad) *
                                        Math.Sin(length) *
                                        Math.Cos(headingRad));
            double otherLon = lonRad +
                              Math.Atan2(
                                   Math.Sin(headingRad) *
                                   Math.Sin(length) *
                                   Math.Cos(latRad),
                                   Math.Cos(length) -
                                   Math.Sin(latRad) *
                                   Math.Sin(otherLat)
                                  );
            return new Point(RadsToDegrees(otherLon), RadsToDegrees(otherLat));
        }

        internal static double DegreesToRads(double d)
        {
            return d * (Math.PI / 180d);
        }

        internal static double RadsToDegrees(double r)
        {
            return r * (180d / Math.PI);
        }

        internal static double Distance(Point point1, Point point2)
        {
            return Distance(point1.X, point1.Y, point2.X, point2.Y);
        }

        // Use the local grid for short spans; retain haversine outside its validated range.
        internal static double Distance(double lon1, double lat1, double lon2, double lat2)
        {
            var geodesy = new LocalGeodesy(lon2, lat2);
            double distanceM = geodesy.DistanceBetween(lon1, lat1, lon2, lat2);
            if (geodesy.IsWithinValidatedEnvelope(distanceM))
            {
                return distanceM;
            }

            return HaversineDistance(lon1, lat1, lon2, lat2);
        }

        internal static double Bearing(Point point1, Point point2)
        {
            return Bearing(point1.X, point1.Y, point2.X, point2.Y);
        }

        // Use the same local/haversine boundary as Distance.
        internal static double Bearing(double lon1, double lat1, double lon2, double lat2)
        {
            var geodesy = new LocalGeodesy(lon2, lat2);
            double distanceM = geodesy.DistanceBetween(lon1, lat1, lon2, lat2);
            if (geodesy.IsWithinValidatedEnvelope(distanceM))
            {
                return geodesy.BearingBetween(lon1, lat1, lon2, lat2);
            }

            return HaversineBearing(lon1, lat1, lon2, lat2);
        }

        // Coverage offsets use the local grid; longer destinations retain haversine.
        internal static Point Destination(Point startPoint, double distanceM, double headingDeg)
        {
            var geodesy = new LocalGeodesy(startPoint.X, startPoint.Y);
            if (geodesy.IsWithinValidatedEnvelope(distanceM))
            {
                return geodesy.DestinationFromOrigin(distanceM, headingDeg);
            }

            return HaversineDestination(startPoint, distanceM, headingDeg);
        }

        internal static double HaversineDistance(Point point1, Point point2)
        {
            return HaversineDistance(point1.X, point1.Y, point2.X, point2.Y);
        }

        internal static double HaversineDistance(double lon1, double lat1, double lon2, double lat2)
        {
            double latRad1 = DegreesToRads(lat1);
            double latRad2 = DegreesToRads(lat2);
            double deltaLat = DegreesToRads(lat2 - lat1);
            double deltaLon = DegreesToRads(lon2 - lon1);
            double a = Math.Pow(Math.Sin(deltaLat / 2d), 2) + Math.Cos(latRad1) * Math.Cos(latRad2) * Math.Pow(Math.Sin(deltaLon / 2d), 2);
            double c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return c * EarthRadiusM;
        }

        internal static double HaversineBearing(Point point1, Point point2)
        {
            return HaversineBearing(point1.X, point1.Y, point2.X, point2.Y);
        }

        internal static double HaversineBearing(double lon1, double lat1, double lon2, double lat2)
        {
            double latRad1 = DegreesToRads(lat1);
            double latRad2 = DegreesToRads(lat2);
            double lonRad1 = DegreesToRads(lon1);
            double lonRad2 = DegreesToRads(lon2);
            double a = Math.Sin(lonRad2 - lonRad1) * Math.Cos(latRad2);
            double b = Math.Cos(latRad1) * Math.Sin(latRad2) - Math.Sin(latRad1) * Math.Cos(latRad2) * Math.Cos(lonRad2 - lonRad1);
            double rad = Math.Atan2(a, b);
            return RadsToDegrees(rad);
        }
    }
}
