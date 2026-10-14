using System.Collections.Generic;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;

namespace AgGateway.ADAPT.StandardPlugin
{
    // Factored working data and the product index are resolved once per section and column.
    // The value written for each row:
    // a product column (product id set, and the section has a product-index meter) writes the
    // converted value only when that meter's product id equals the column, and 0 otherwise.
    // A section that does not report that product's working data also writes 0.
    // Every other column writes the converted value, or null when the section does not report it.
    // Missing data stays null. It is not written as zero.
    internal sealed class SpatialColumnWriter
    {
        internal ADAPTDataColumn Column { get; set; }
        internal FactoredWorkingData FactoredDefinition { get; set; }
        internal NumericWorkingData ProductIndexWorkingData { get; set; }
        internal bool RequiresProductMatch { get; set; }

        internal double? GetValue(SpatialRecord record)
        {
            if (RequiresProductMatch)
            {
                if (FactoredDefinition == null)
                {
                    return 0d;
                }

                NumericRepresentationValue value = record.GetMeterValue(FactoredDefinition.WorkingData) as NumericRepresentationValue;
                var doubleVal = Column.ConvertMeterValue(value) * FactoredDefinition.Factor;
                NumericRepresentationValue productValue = record.GetMeterValue(ProductIndexWorkingData) as NumericRepresentationValue;
                var productValueData = productValue?.Value?.Value;
                if (productValueData != null && Column.ProductId == ((int)productValueData).ToString())
                {
                    return doubleVal;
                }

                return 0d;
            }

            if (FactoredDefinition == null)
            {
                return null;
            }

            NumericRepresentationValue sectionValue = record.GetMeterValue(FactoredDefinition.WorkingData) as NumericRepresentationValue;
            return Column.ConvertMeterValue(sectionValue) * FactoredDefinition.Factor;
        }
    }

    internal sealed class SectionSpatialWriter
    {
        internal SectionDefinition Section { get; set; }
        internal SpatialColumnWriter[] ColumnWriters { get; set; }
        internal FactoredWorkingData HeadingDefinition { get; set; }
        internal FactoredWorkingData DistanceDefinition { get; set; }

        internal static SectionSpatialWriter[] CreateWriters(Implement implement, ADAPTParquetColumnData columnData)
        {
            var writers = new SectionSpatialWriter[implement.Sections.Count];
            for (int sectionIndex = 0; sectionIndex < implement.Sections.Count; sectionIndex++)
            {
                var section = implement.Sections[sectionIndex];
                var columnWriters = new SpatialColumnWriter[columnData.Columns.Count];
                for (int columnIndex = 0; columnIndex < columnData.Columns.Count; columnIndex++)
                {
                    var column = columnData.Columns[columnIndex];
                    FactoredWorkingData factoredDefinition = null;
                    if (column.ProductId != null && section.ProductIndexWorkingData != null)
                    {
                        if (section.FactoredDefinitionsBySourceCodeByProduct.TryGetValue(column.ProductId, out var factoredDefinitionsForProduct))
                        {
                            factoredDefinitionsForProduct.TryGetValue(column.SrcName, out factoredDefinition);
                        }

                        columnWriters[columnIndex] = new SpatialColumnWriter
                        {
                            Column = column,
                            FactoredDefinition = factoredDefinition,
                            ProductIndexWorkingData = section.ProductIndexWorkingData,
                            RequiresProductMatch = true
                        };
                    }
                    else if (section.FactoredDefinitionsBySourceCodeByProduct.TryGetValue(string.Empty, out var factoredDefinitionsForSection))
                    {
                        factoredDefinitionsForSection.TryGetValue(column.SrcName, out factoredDefinition);
                        columnWriters[columnIndex] = new SpatialColumnWriter
                        {
                            Column = column,
                            FactoredDefinition = factoredDefinition,
                            RequiresProductMatch = false
                        };
                    }
                    else
                    {
                        columnWriters[columnIndex] = new SpatialColumnWriter
                        {
                            Column = column,
                            RequiresProductMatch = false
                        };
                    }
                }

                FactoredWorkingData headingDefinition = null;
                FactoredWorkingData distanceDefinition = null;
                if (section.FactoredDefinitionsBySourceCodeByProduct.TryGetValue(string.Empty, out var sectionDefinitions))
                {
                    sectionDefinitions.TryGetValue("vrHeading", out headingDefinition);
                    sectionDefinitions.TryGetValue("vrDistanceTraveled", out distanceDefinition);
                }

                writers[sectionIndex] = new SectionSpatialWriter
                {
                    Section = section,
                    ColumnWriters = columnWriters,
                    HeadingDefinition = headingDefinition,
                    DistanceDefinition = distanceDefinition
                };
            }

            return writers;
        }
    }
}
