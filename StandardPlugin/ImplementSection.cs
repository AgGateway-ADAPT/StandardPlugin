using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Equipment;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using NetTopologySuite.Geometries;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal class SectionDefinition
    {
        public SectionDefinition(DeviceElementUse deviceElementUse, DeviceElementConfiguration deviceElementConfiguration, DeviceElement deviceElement, OperationData operationData, Catalog catalog, List<TypeMapping> typeMappings)
        {
            DeviceElement = deviceElement;
            
            WorkstateDefinition = deviceElementUse.GetWorkingDatas().OfType<EnumeratedWorkingData>().FirstOrDefault(x => x.Representation.Code == "dtRecordingStatus");
            FactoredDefinitionsBySourceCodeByProduct = new Dictionary<string, Dictionary<string, FactoredWorkingData>>();

            WidthM = deviceElementConfiguration.WidthM();
            Offset = deviceElementConfiguration.AsOffset();

            //Add only the variables we can map to a standard type
            var numericWorkingDatas = deviceElementUse.GetWorkingDatas().OfType<NumericWorkingData>().Where(nwd => typeMappings.Any(m => m.Source == nwd.Representation.Code));
            var definitions = numericWorkingDatas.Select(nwd => new FactoredWorkingData(nwd, WidthM, WidthM, typeMappings.First(m => m.Source == nwd.Representation.Code)));

             //Track any variable product
            ProductIndexWorkingData = deviceElementUse.GetWorkingDatas().FirstOrDefault(wd => wd.Representation.Code == "vrProductIndex") as NumericWorkingData;
            
            if (ProductIndexWorkingData == null)
            {
                FactoredDefinitionsBySourceCodeByProduct.Add(string.Empty, definitions.ToDictionary(d => d.WorkingData.Representation.Code));
            }
            else 
            {
                FactoredDefinitionsBySourceCodeByProduct.Add(string.Empty, new Dictionary<string, FactoredWorkingData>());
                foreach (var productId in operationData.ProductIds)
                {
                    FactoredDefinitionsBySourceCodeByProduct.Add(productId.ToString(), new Dictionary<string, FactoredWorkingData>());
                }
                foreach (var definition in definitions)
                {
                    if (typeMappings.First(m => m.Source == definition.WorkingData.Representation.Code).IsMultiProductCapable)
                    {
                        foreach (var id in operationData.ProductIds)
                        {
                            FactoredDefinitionsBySourceCodeByProduct[id.ToString()].Add(definition.WorkingData.Representation.Code, definition);
                        }
                    }  
                    else
                    {
                        FactoredDefinitionsBySourceCodeByProduct[string.Empty].Add(definition.WorkingData.Representation.Code, definition);
                    }
                }
            }
        }
        public Offset Offset { get; set; }
        public DeviceElement DeviceElement { get; set; }
        public EnumeratedWorkingData WorkstateDefinition { get; set; }
        public Dictionary<string, Dictionary<string, FactoredWorkingData>> FactoredDefinitionsBySourceCodeByProduct { get; set; }

        public double WidthM { get; set; }

        public NumericWorkingData ProductIndexWorkingData { get; set; }

        public string GetDefinitionKey()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(DeviceElement?.Description ?? string.Empty);
            builder.Append(Offset.X?.ToString() ?? string.Empty);
            builder.Append(Offset.Y?.ToString() ?? string.Empty);
            builder.Append(WidthM.ToString());
            foreach(string productId in FactoredDefinitionsBySourceCodeByProduct.Keys)
            {
                foreach(var factoredDefinition in FactoredDefinitionsBySourceCodeByProduct[productId].Values)
                {
                    builder.Append(factoredDefinition.WorkingData.Representation.Code);
                }
            }

            return builder.ToString().AsMD5Hash();
        }

        public void AddAncestorWorkingDatas(DeviceElementUse ancestorUse, DeviceElementConfiguration ancestorConfig, OperationData operationData, List<TypeMapping> typeMappings)
        {
            var productIndex = ancestorUse.GetWorkingDatas().FirstOrDefault(wd => wd.Representation.Code == "vrProductIndex") as NumericWorkingData;
            if (productIndex != null && ProductIndexWorkingData == null)
            {   
                ProductIndexWorkingData = productIndex;
                foreach (var productId in operationData.ProductIds)
                {
                    if (!FactoredDefinitionsBySourceCodeByProduct.ContainsKey(productId.ToString()))
                    {
                        FactoredDefinitionsBySourceCodeByProduct.Add(productId.ToString(), new Dictionary<string, FactoredWorkingData>());
                    }
                }
                List<FactoredWorkingData> definitionMoved = new List<FactoredWorkingData>();
                foreach (var definition in FactoredDefinitionsBySourceCodeByProduct[string.Empty].Values.Where(d => typeMappings.First(m => m.Source == d.WorkingData.Representation.Code).IsMultiProductCapable))
                {
                    definitionMoved.Add(definition);
                    foreach (var productId in operationData.ProductIds)
                    {
                        FactoredDefinitionsBySourceCodeByProduct[productId.ToString()].Add(definition.WorkingData.Representation.Code, definition);
                    }
                }
                foreach (var definition in definitionMoved)
                {
                    FactoredDefinitionsBySourceCodeByProduct[string.Empty].Remove(definition.WorkingData.Representation.Code);
                }
            }

            foreach (var workingData in ancestorUse.GetWorkingDatas())
            {
                if (WorkstateDefinition == null &&
                    workingData.Representation.Code == "dtRecordingStatus" &&
                    workingData is EnumeratedWorkingData ewd)
                {
                    WorkstateDefinition = ewd;
                }
                else if (workingData is NumericWorkingData nwd &&
                        typeMappings.Any(m => m.Source == nwd.Representation.Code))
                {
                    if (typeMappings.First(m => m.Source == nwd.Representation.Code).IsMultiProductCapable)
                    {
                        foreach (var productId in FactoredDefinitionsBySourceCodeByProduct.Keys)
                        {
                            if (!FactoredDefinitionsBySourceCodeByProduct[productId].ContainsKey(nwd.Representation.Code))
                            {
                                FactoredDefinitionsBySourceCodeByProduct[productId].Add(nwd.Representation.Code, new FactoredWorkingData(nwd, ancestorConfig.WidthM(), WidthM, typeMappings.First(m => m.Source == nwd.Representation.Code)));
                            }
                        }
                    }
                    else
                    {
                        if (!FactoredDefinitionsBySourceCodeByProduct[string.Empty].ContainsKey(nwd.Representation.Code))
                        {
                            FactoredDefinitionsBySourceCodeByProduct[string.Empty].Add(nwd.Representation.Code, new FactoredWorkingData(nwd, ancestorConfig.WidthM(), WidthM, typeMappings.First(m => m.Source == nwd.Representation.Code)));
                        }
                    }
                }
            }
        }

        public bool IsEngaged(SpatialRecord record)
        {
            if (WorkstateDefinition != null)
            {
                EnumeratedValue engagedValue = record.GetMeterValue(WorkstateDefinition) as EnumeratedValue;
                return engagedValue != null && (engagedValue.Value.Value == "dtiRecordingStatusOn" || engagedValue.Value.Value == "On");
            }
            return true;
        }

        private LeadingEdge _latestLeadingEdge;
        public bool TryGetCoveragePolygon(SpatialRecord record, SpatialRecord previousRecord, out Polygon polygon)
        {
            polygon = null;
            var adaptPoint = (ApplicationDataModel.Shapes.Point)record.Geometry;
            Point point = new Point(adaptPoint.X, adaptPoint.Y);

            if (point.X == 0 && point.Y == 0)
            {
                return false;
            }

            Point priorPoint = null;
            if (previousRecord != null)
            {
                var priorADAPTPoint = (ApplicationDataModel.Shapes.Point)previousRecord.Geometry;
                priorPoint = new Point(priorADAPTPoint.X, priorADAPTPoint.Y);
            }

            var sectionDefinitions = FactoredDefinitionsBySourceCodeByProduct[string.Empty];

            double bearing = 0d;
            if (sectionDefinitions.TryGetValue("vrHeading", out var headingDefinition))
            {
                var headingValue = ((NumericRepresentationValue)record.GetMeterValue(headingDefinition.WorkingData))?.Value?.Value;
                if (headingValue != null)
                {
                    bearing = headingValue.Value;
                }
                else if (priorPoint != null)
                {
                    bearing = GeometryExporter.HaversineBearing(priorPoint, point);
                }
            }
            else if (priorPoint != null)
            {
                bearing = GeometryExporter.HaversineBearing(priorPoint, point);
            }

            var x = point.Destination(Offset.X ?? 0d, bearing % 360d);
            var xy = x.Destination(Offset.Y ?? 0d, bearing + 90d % 360d);

            double? reportedDistance = null;
            if (sectionDefinitions.TryGetValue("vrDistanceTraveled", out var distanceDefinition) &&
                record.GetMeterValue(distanceDefinition.WorkingData) is NumericRepresentationValue distanceData)
            {
                reportedDistance = distanceData?.Value?.Value;
            }
            double? calculatedDistance = null;
            if (!(reportedDistance > 0d) && priorPoint != null)
            {
                calculatedDistance = GeometryExporter.HaversineDistance(priorPoint, point);
            }
            polygon = xy.AsCoveragePolygon(WidthM, ref _latestLeadingEdge, bearing, reportedDistance, calculatedDistance);
            if (polygon.IsEmpty || !polygon.IsValid)
            {
                return false;
            }
            return true;
        }

        public void ClearLeadingEdge()
        {
            _latestLeadingEdge = null;
        }
    }

    internal class LeadingEdge
    {
        public LeadingEdge(Point leadingPoint, double width, LeadingEdge priorLeadingEdge, double heading)
        {
            Heading = heading;
            double wh = width / 2d;
            Left = leadingPoint.Destination(wh, Extensions.HeadingLeft(heading));
            Center = leadingPoint;
            Right = leadingPoint.Destination(wh, Extensions.HeadingRight(heading));

            if (priorLeadingEdge != null && this.AsLineString().Intersects(priorLeadingEdge.AsLineString()))
            {
                //Implement is turning significantly
                bool turningLeft = priorLeadingEdge.Heading < heading;
                if (Math.Abs(priorLeadingEdge.Heading - heading) > 300d)
                {
                    //Bearings are on either side of zero
                    if (priorLeadingEdge.Heading - 360d > heading)
                    {
                        turningLeft = false;
                    }
                    else if (priorLeadingEdge.Heading - 360d < heading)
                    {
                        turningLeft = true;
                    }
                }
                if (turningLeft)
                {
                    Left = priorLeadingEdge.Left;
                }
                else
                {
                    Right = priorLeadingEdge.Right;
                }
            }
        }

        public Point Left { get; set; }
        public Point Center { get; set; }
        public Point Right { get; set; }    
        public double Heading { get; set; }

        public double GetBearing(Point nextPoint)
        {
            return GeometryExporter.HaversineBearing(Center, nextPoint);
        }
        public double GetDistance(Point nextPoint)
        {
            return GeometryExporter.HaversineDistance(Center, nextPoint);
        }

        public LineString AsLineString()
        {
            return new LineString(new Coordinate[]{Left.AsCoordinate(), Right.AsCoordinate()});
        }
    }

    internal class Offset
    {
        public Offset(double? x, double? y, double? z)
        {
            X = x;
            Y = y;
            Z = z;
        }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }

        public void Add(Offset other)
        {
            if (!X.HasValue) X = 0;
            X += other.X ?? 0;
            if (!Y.HasValue) Y = 0;
            Y += other.Y ?? 0;
            if (!Z.HasValue) Z = 0;
            Z += other.Z ?? 0;
        }
    }

    /// <summary>
    /// In cases where an implement reports a single total value, e.g., at the implement level and then models multiple sections for on/off values only
    /// The total must be factored down for the width of the section relative to the total width of the implement
    /// For rates, percentages, etc.  No factor is needed.
    /// </summary>
    internal class FactoredWorkingData
    {
        public FactoredWorkingData(NumericWorkingData workingData, double dataWidthM, double sectionWidthM, TypeMapping mapping)
        {
            WorkingData = workingData;
            
            if (mapping.ShouldFactor && 
                dataWidthM > 0d &&
                Math.Abs(dataWidthM - sectionWidthM) > .001) //Floating point values are sometimes not exact
            {
                Factor = sectionWidthM / dataWidthM;
            }
            else
            {
                Factor = 1d;
            }
        }
        public double Factor { get; set; }
        public NumericWorkingData WorkingData { get; set; }
    }
}