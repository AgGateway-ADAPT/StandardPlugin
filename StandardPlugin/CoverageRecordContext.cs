using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal readonly struct CoverageRecordContext
    {
        internal CoverageRecordContext(
            double lon,
            double lat,
            double? priorLon,
            double? priorLat,
            LocalGeodesy geodesy)
        {
            Lon = lon;
            Lat = lat;
            PriorLon = priorLon;
            PriorLat = priorLat;
            Geodesy = geodesy;
        }

        internal double Lon { get; }
        internal double Lat { get; }
        internal double? PriorLon { get; }
        internal double? PriorLat { get; }
        internal LocalGeodesy Geodesy { get; }

        internal static bool TryCreate(
            SpatialRecord record,
            SpatialRecord priorRecord,
            out CoverageRecordContext context)
        {
            context = default;
            if (!(record.Geometry is ApplicationDataModel.Shapes.Point adaptPoint))
            {
                return false;
            }

            double lon = adaptPoint.X;
            double lat = adaptPoint.Y;
            if (lon == 0d && lat == 0d)
            {
                return false;
            }

            double? priorLon = null;
            double? priorLat = null;
            if (priorRecord?.Geometry is ApplicationDataModel.Shapes.Point priorPoint)
            {
                priorLon = priorPoint.X;
                priorLat = priorPoint.Y;
            }

            // Recenter the meter grid on this fix. Section offsets are a few meters from here,
            // which is where the equirectangular grid is closest to the sphere.
            // Heading and distance stay out of this shared fix; each section resolves its own.
            context = new CoverageRecordContext(
                lon,
                lat,
                priorLon,
                priorLat,
                new LocalGeodesy(lon, lat));
            return true;
        }

        internal double ResolveBearing(SpatialRecord record, FactoredWorkingData headingDefinition, double? previousHeading = null)
        {
            if (headingDefinition != null)
            {
                var headingValue = ((NumericRepresentationValue)record.GetMeterValue(headingDefinition.WorkingData))?.Value?.Value;
                if (headingValue != null)
                {
                    return headingValue.Value;
                }
            }

            if (PriorLon.HasValue)
            {
                // Identical positions have no direction. Bearing() of that pair is 0, which would
                // swing a leading edge to north during a stop that did not report a heading.
                if (previousHeading.HasValue &&
                    GeometryExporter.Distance(PriorLon.Value, PriorLat.Value, Lon, Lat) == 0d)
                {
                    return previousHeading.Value;
                }

                return GeometryExporter.Bearing(PriorLon.Value, PriorLat.Value, Lon, Lat);
            }

            return previousHeading ?? 0d;
        }

        internal double? ResolveReportedDistance(SpatialRecord record, FactoredWorkingData distanceDefinition)
        {
            if (distanceDefinition != null &&
                record.GetMeterValue(distanceDefinition.WorkingData) is NumericRepresentationValue distanceData)
            {
                return distanceData?.Value?.Value;
            }

            return null;
        }

        internal double? ResolveCalculatedDistance(double? reportedDistance)
        {
            if (!(reportedDistance > 0d) && PriorLon.HasValue)
            {
                return GeometryExporter.Distance(PriorLon.Value, PriorLat.Value, Lon, Lat);
            }

            return null;
        }
    }
}
