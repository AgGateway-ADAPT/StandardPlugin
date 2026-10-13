using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Documents;
using AgGateway.ADAPT.ApplicationDataModel.Logistics;
using AgGateway.ADAPT.ApplicationDataModel.Prescriptions;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.Standard;
using BitMiracle.LibTiff.Classic;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal class WorkOrderExporter
    {
        private readonly string _exportPath;
        private readonly Standard.Documents _documents;
        private readonly CommonExporters _commonExporters;
        private readonly bool _multithread;
        private readonly int _maxParallelism;
        private static readonly object _tiffSyncRoot = new object();
        private Tiff.TiffExtendProc _parentTagExtender;

        private WorkOrderExporter(Root root, string exportPath, Properties properties)
        {
            _exportPath = exportPath;
            _documents = root.Documents;
            _multithread = ExportSettings.UseMultithread(properties);
            _maxParallelism = ExportSettings.GetMaxParallelism(properties);

            _documents.WorkOrders = new List<WorkOrderElement>();

            _commonExporters = new CommonExporters(root);
        }

        internal static IEnumerable<IError> Export(ApplicationDataModel.ADM.ApplicationDataModel dataModel, Root root, string exportPath, Properties properties = null)
        {
            var exporter = new WorkOrderExporter(root, exportPath, properties);
            return exporter.Export(dataModel);
        }

        private sealed class PrescriptionExportResult
        {
            internal IEnumerable<VariableElement> Variables { get; set; }
            internal string SpatialFile { get; set; }
            internal List<SummaryValueElement> SummaryValues { get; set; }
            internal List<IError> Errors { get; set; }
        }

        private PrescriptionExportResult[] ExportPrescriptionsInParallel(IReadOnlyList<(WorkItem WorkItem, WorkItemOperation WorkItemOperation, Prescription Prescription)> prescriptions, ApplicationDataModel.ADM.Catalog catalog)
        {
            var results = new PrescriptionExportResult[prescriptions.Count];
            var firstIndexByPrescriptionId = new Dictionary<int, int>();
            var distinctIndexes = new List<int>();
            for (int i = 0; i < prescriptions.Count; i++)
            {
                int prescriptionId = prescriptions[i].Prescription.Id.ReferenceId;
                if (firstIndexByPrescriptionId.ContainsKey(prescriptionId))
                {
                    continue;
                }

                firstIndexByPrescriptionId.Add(prescriptionId, i);
                distinctIndexes.Add(i);
            }

            // Repeated references to one prescription share one file. Distinct prescriptions
            // export concurrently only when more than one remains.
            if (!_multithread || distinctIndexes.Count <= 1)
            {
                for (int distinctIndex = 0; distinctIndex < distinctIndexes.Count; distinctIndex++)
                {
                    int sourceIndex = distinctIndexes[distinctIndex];
                    results[sourceIndex] = ExportPrescriptionResult(prescriptions[sourceIndex].Prescription, catalog);
                }
            }
            else
            {
                try
                {
                    Parallel.For(0, distinctIndexes.Count, new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism }, distinctIndex =>
                    {
                        int sourceIndex = distinctIndexes[distinctIndex];
                        results[sourceIndex] = ExportPrescriptionResult(prescriptions[sourceIndex].Prescription, catalog);
                    });
                }
                catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(aggregate.InnerExceptions[0]).Throw();
                }
            }

            for (int i = 0; i < prescriptions.Count; i++)
            {
                int firstIndex = firstIndexByPrescriptionId[prescriptions[i].Prescription.Id.ReferenceId];
                if (i == firstIndex)
                {
                    continue;
                }

                PrescriptionExportResult exported = results[firstIndex];
                results[i] = new PrescriptionExportResult
                {
                    Variables = exported.Variables,
                    SpatialFile = exported.SpatialFile,
                    SummaryValues = exported.SummaryValues,
                    Errors = new List<IError>()
                };
            }

            return results;
        }

        private PrescriptionExportResult ExportPrescriptionResult(Prescription prescription, ApplicationDataModel.ADM.Catalog catalog)
        {
            (IEnumerable<VariableElement> variables, string spatialFile, List<SummaryValueElement> summaryValues, List<IError> errors) = ExportPrescription(prescription, catalog);
            return new PrescriptionExportResult
            {
                Variables = variables,
                SpatialFile = spatialFile,
                SummaryValues = summaryValues,
                Errors = errors
            };
        }

        private IEnumerable<IError> Export(ApplicationDataModel.ADM.ApplicationDataModel dataModel)
        {
            List<IError> outErrors = new List<IError>();
            //Track the prescriptions we export via workItems
            List<int> nonOrphanedRxs = new List<int>();

            //Categorize all the objects by field or cropzone
            Dictionary<int, List<(WorkItem, WorkItemOperation, Prescription)>> flatByField = new Dictionary<int, List<(WorkItem, WorkItemOperation, Prescription)>>();
            Dictionary<int, List<(WorkItem, WorkItemOperation, Prescription)>> flatByCropZone = new Dictionary<int, List<(WorkItem, WorkItemOperation, Prescription)>>();
            if (dataModel.Documents?.WorkItems != null)
            {
                foreach (var workItem in dataModel.Documents.WorkItems)
                {
                    foreach (int workItemOperationId in workItem.WorkItemOperationIds)
                    {
                        var workItemOperation = dataModel.Documents.WorkItemOperations.FirstOrDefault(w => w.Id.ReferenceId == workItemOperationId);
                        if (workItemOperation != null)
                        {
                            Prescription rx = null;
                            if (workItemOperation.PrescriptionId.HasValue)
                            {
                                rx = dataModel.Catalog.Prescriptions.FirstOrDefault(p => p.Id.ReferenceId == workItemOperation.PrescriptionId.Value);
                                if (rx != null) //If no prescription, there is nothing substantive to export.
                                {
                                    var tuple = (workItem, workItemOperation, rx); //All 3 will be non-null
                                    if (workItem.CropZoneId.HasValue)
                                    {
                                        //Add only to the specified cropzone
                                        var id = workItem.CropZoneId.Value;
                                        if (!flatByCropZone.ContainsKey(id))
                                        {
                                            flatByCropZone.Add(id, new List<(WorkItem, WorkItemOperation, Prescription)>());
                                        }
                                        flatByCropZone[id].Add(tuple);
                                    }
                                    else if (workItem.FieldId.HasValue)
                                    {
                                        //Add only to the specified field
                                        var id = workItem.FieldId.Value;
                                        if (!flatByField.ContainsKey(id))
                                        {
                                            flatByField.Add(id, new List<(WorkItem, WorkItemOperation, Prescription)>());
                                        }
                                        flatByField[id].Add(tuple);
                                    }
                                    else if (dataModel.Documents.WorkOrders.Any(w => w.WorkItemIds.Contains(workItem.Id.ReferenceId)))
                                    {
                                        //if this workitem is part of a parent work order, apply it across fields or crop zones
                                        var srcWorkOrder = dataModel.Documents.WorkOrders.First(w => w.WorkItemIds.Contains(workItem.Id.ReferenceId));
                                        if (srcWorkOrder.CropZoneIds.Any())
                                        {
                                            //Add the data to all specified crop zones
                                            foreach (var id in srcWorkOrder.CropZoneIds)
                                            {
                                                if (!flatByCropZone.ContainsKey(id))
                                                {
                                                    flatByCropZone.Add(id, new List<(WorkItem, WorkItemOperation, Prescription)>());
                                                }
                                                flatByCropZone[id].Add(tuple);
                                            }
                                        }
                                        else if (srcWorkOrder.FieldIds.Any())
                                        {
                                            //Add the data to all specified fields
                                            foreach (var id in srcWorkOrder.FieldIds)
                                            {
                                                if (!flatByField.ContainsKey(id))
                                                {
                                                    flatByField.Add(id, new List<(WorkItem, WorkItemOperation, Prescription)>());
                                                }
                                                flatByField[id].Add(tuple);
                                            }

                                        }
                                        else if (srcWorkOrder.FarmIds.Any())
                                        {
                                            //Add the data to all fields in specified farms
                                            foreach (var farm in dataModel.Catalog.Farms.Where(f => srcWorkOrder.FarmIds.Contains(f.Id.ReferenceId)))
                                            {
                                                foreach (Field field in dataModel.Catalog.Fields.Where(f => f.FarmId == farm.Id.ReferenceId))
                                                {
                                                    var cropZones = dataModel.Catalog.CropZones.Where(c => c.FieldId == field.Id.ReferenceId);
                                                    if (cropZones.Any())
                                                    {
                                                        foreach (CropZone cropZone in cropZones)
                                                        {
                                                            var id = cropZone.Id.ReferenceId;
                                                            if (!flatByCropZone.ContainsKey(id))
                                                            {
                                                                flatByCropZone.Add(id, new List<(WorkItem, WorkItemOperation, Prescription)>());
                                                            }
                                                            flatByCropZone[id].Add(tuple);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        var id = field.Id.ReferenceId;
                                                        if (!flatByField.ContainsKey(id))
                                                        {
                                                            flatByField.Add(id, new List<(WorkItem, WorkItemOperation, Prescription)>());
                                                        }
                                                        flatByField[id].Add(tuple);
                                                    }
                                                }
                                            }
                                        }
                                        else if (srcWorkOrder.GrowerId.HasValue)
                                        {
                                            foreach (var farm in dataModel.Catalog.Farms.Where(f => f.GrowerId == srcWorkOrder.GrowerId.Value))
                                            {
                                                foreach (Field field in dataModel.Catalog.Fields.Where(f => f.FarmId == farm.Id.ReferenceId))
                                                {
                                                    var cropZones = dataModel.Catalog.CropZones.Where(c => c.FieldId == field.Id.ReferenceId);
                                                    if (cropZones.Any())
                                                    {
                                                        foreach (CropZone cropZone in cropZones)
                                                        {
                                                            var id = cropZone.Id.ReferenceId;
                                                            if (!flatByCropZone.ContainsKey(id))
                                                            {
                                                                flatByCropZone.Add(id, new List<(WorkItem, WorkItemOperation, Prescription)>());
                                                            }
                                                            flatByCropZone[id].Add(tuple);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        var id = field.Id.ReferenceId;
                                                        if (!flatByField.ContainsKey(id))
                                                        {
                                                            flatByField.Add(id, new List<(WorkItem, WorkItemOperation, Prescription)>());
                                                        }
                                                        flatByField[id].Add(tuple);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    //else there is nothing to map this data to
                                }
                            }
                        }
                    }
                }

                foreach (int key in flatByField.Keys)
                {
                    var field = dataModel.Catalog.Fields.FirstOrDefault(f => f.Id.ReferenceId == key);
                    if (field != null)
                    {
                        WorkOrderElement dstWorkOrder = new WorkOrderElement()
                        {
                            Id = new Id() { ReferenceId = string.Concat("WorkOrder", key) },
                            FieldId = key.ToString(),
                            Name = string.Concat(field.Description, ": Work Order"),
                            Operations = new List<OperationElement>()
                        };
                        var prescriptions = flatByField[key];
                        var prescriptionResults = ExportPrescriptionsInParallel(prescriptions, dataModel.Catalog);
                        for (int prescriptionIndex = 0; prescriptionIndex < prescriptions.Count; prescriptionIndex++)
                        {
                            var (workItem, workItemOperation, rx) = prescriptions[prescriptionIndex];
                            var prescriptionResult = prescriptionResults[prescriptionIndex];
                            outErrors.AddRange(prescriptionResult.Errors);
                            nonOrphanedRxs.Add(rx.Id.ReferenceId);

                            OperationElement operationElement = new OperationElement()
                            {
                                Id = _commonExporters.ExportID(workItem.Id),
                                Name = string.Concat(workItemOperation.Description, "_", workItemOperation.OperationType.ToString()),
                                TimeScopes = _commonExporters.ExportTimeScopes(rx.TimeScopes, out _),
                                OperationTypeCode = _commonExporters.ExportOperationType(workItemOperation.OperationType),
                                GuidanceAllocations = _commonExporters.ExportGuidanceAllocations(workItem.GuidanceAllocationIds, dataModel),
                                ReferenceLayerIds = workItem.ReferenceLayerIds.Select(x => x.ToString()).ToList(),
                                PartyRoles = _commonExporters.ExportPersonRoles(rx.PersonRoles),
                                ContextItems = _commonExporters.ExportContextItems(rx.ContextItems),
                                Variables = prescriptionResult.Variables.ToList(),
                                ProductIds = prescriptionResult.Variables.Select(x => x.ProductId).Distinct().ToList(),
                                SpatialRecordsFile = prescriptionResult.SpatialFile,
                                SummaryValues = prescriptionResult.SummaryValues,
                            };
                            dstWorkOrder.Operations.Add(operationElement);
                        }
                        _documents.WorkOrders.Add(dstWorkOrder);
                    }
                }

                foreach (int key in flatByCropZone.Keys)
                {
                    var cropZone = dataModel.Catalog.CropZones.FirstOrDefault(f => f.Id.ReferenceId == key);
                    if (cropZone != null)
                    {
                        WorkOrderElement dstWorkOrder = new WorkOrderElement()
                        {
                            Id = new Id() { ReferenceId = string.Concat("WorkOrder", key) },
                            CropZoneId = key.ToString(),
                            FieldId = cropZone.FieldId.ToString(),
                            Name = string.Concat(cropZone.Description, ": Work Order"),
                            Operations = new List<OperationElement>()
                        };
                        var prescriptions = flatByCropZone[key];
                        var prescriptionResults = ExportPrescriptionsInParallel(prescriptions, dataModel.Catalog);
                        for (int prescriptionIndex = 0; prescriptionIndex < prescriptions.Count; prescriptionIndex++)
                        {
                            var (workItem, workItemOperation, rx) = prescriptions[prescriptionIndex];
                            var prescriptionResult = prescriptionResults[prescriptionIndex];
                            outErrors.AddRange(prescriptionResult.Errors);
                            nonOrphanedRxs.Add(rx.Id.ReferenceId);

                            OperationElement operationElement = new OperationElement()
                            {
                                Id = _commonExporters.ExportID(workItem.Id),
                                Name = string.Concat(workItemOperation.Description, "_", rx.Description, "_", workItemOperation.OperationType.ToString()),
                                TimeScopes = _commonExporters.ExportTimeScopes(rx.TimeScopes, out _),
                                OperationTypeCode = _commonExporters.ExportOperationType(workItemOperation.OperationType),
                                GuidanceAllocations = _commonExporters.ExportGuidanceAllocations(workItem.GuidanceAllocationIds, dataModel),
                                ReferenceLayerIds = workItem.ReferenceLayerIds.Select(x => x.ToString()).ToList(),
                                PartyRoles = _commonExporters.ExportPersonRoles(rx.PersonRoles),
                                ContextItems = _commonExporters.ExportContextItems(rx.ContextItems),
                                Variables = prescriptionResult.Variables.ToList(),
                                ProductIds = prescriptionResult.Variables.Select(x => x.ProductId).Distinct().ToList(),
                                SpatialRecordsFile = prescriptionResult.SpatialFile,
                                SummaryValues = prescriptionResult.SummaryValues,
                            };
                            dstWorkOrder.Operations.Add(operationElement);
                        }
                        _documents.WorkOrders.Add(dstWorkOrder);
                    }
                }
            }

            //Some implementers may simply have created Prescriptions in the catalog without creating any document elements.
            var orphanedPrescriptionIds = dataModel.Catalog.Prescriptions.Select(x => x.Id.ReferenceId).Except(nonOrphanedRxs);
            foreach (int rxId in orphanedPrescriptionIds)
            {
                var rx = dataModel.Catalog.Prescriptions.First(x => x.Id.ReferenceId == rxId);
                (IEnumerable<VariableElement> variables, string spatialFile, List<SummaryValueElement> summaryValues, List<IError> errors) = ExportPrescription(rx, dataModel.Catalog);
                outErrors.AddRange(errors);

                WorkOrderElement dstWorkOrder = new WorkOrderElement()
                {
                    Id = new Id() { ReferenceId = string.Concat("WorkOrder", rx.Id.ReferenceId) },
                    CropZoneId = rx.CropZoneId.HasValue ? rx.CropZoneId.Value.ToString() : null,
                    FieldId = rx.FieldId.ToString(),
                    Name = string.Concat(rx.Description, ": Work Order"),
                    Operations = new List<OperationElement>()
                };

                OperationElement operationElement = new OperationElement()
                {
                    Id = _commonExporters.ExportID(rx.Id),
                    Name = string.Concat(rx.Description, "_", rx.OperationType.ToString()),
                    TimeScopes = _commonExporters.ExportTimeScopes(rx.TimeScopes, out _),
                    OperationTypeCode = _commonExporters.ExportOperationType(rx.OperationType),
                    PartyRoles = _commonExporters.ExportPersonRoles(rx.PersonRoles),
                    ContextItems = _commonExporters.ExportContextItems(rx.ContextItems),
                    Variables = variables.ToList(),
                    ProductIds = variables.Select(x => x.ProductId).Distinct().ToList(),
                    SpatialRecordsFile = spatialFile,
                    SummaryValues = summaryValues,
                };
                dstWorkOrder.Operations.Add(operationElement);
                _documents.WorkOrders.Add(dstWorkOrder);
            }

            if (!_documents.WorkOrders.Any())
            {
                _documents.WorkOrders = null;
            }

            outErrors.AddRange(_commonExporters.Errors);
            return outErrors;
        }

        private (IEnumerable<VariableElement>, string, List<SummaryValueElement>, List<IError> errors) ExportPrescription(Prescription rx, ApplicationDataModel.ADM.Catalog catalog)
        {
            List<VariableElement> variables = new List<VariableElement>();
            string spatialFile = null;
            List<SummaryValueElement> summaryValues = null;
            List<IError> errors = new List<IError>();
            if (rx is ManualPrescription manualRx)
            {
                summaryValues = new List<SummaryValueElement>();
                foreach (var productUse in manualRx.ProductUses)
                {
                    var product = catalog.Products.FirstOrDefault(x => x.Id.ReferenceId == productUse.ProductId);
                    if (product != null)
                    {
                        if (productUse.Rate != null)
                        {
                            var mapping = _commonExporters.TypeMappings.FirstOrDefault(x => x.Source == productUse.Rate.Representation?.Code);
                            if (mapping != null)
                            {
                                var definition = _commonExporters.StandardDataTypes.Definitions.FirstOrDefault(x => x.DefinitionCode == mapping.Target);
                                var variable = new VariableElement
                                {
                                    Id = new Id { ReferenceId = string.Concat(rx.Id.ReferenceId, "-ProductRate", productUse.ProductId) },
                                    Description = string.Concat(manualRx.Description, "_", product.Description),
                                    ProductId = productUse.ProductId.ToString(),
                                    DefinitionCode = definition.DefinitionCode,
                                };
                                variables.Add(variable);
                                SummaryValueElement summaryValue = new SummaryValueElement()
                                {
                                    VariableId = variable.Id.ReferenceId,
                                    ValueText = productUse.Rate.AsConvertedDouble(definition.NumericDataTypeDefinitionAttributes.UnitOfMeasureCode).ToString()
                                };
                                summaryValues.Add(summaryValue);
                            }
                        }
                        if (productUse.AppliedArea != null)
                        {
                            var mapping = _commonExporters.TypeMappings.FirstOrDefault(x => x.Source == productUse.AppliedArea.Representation?.Code);
                            if (mapping != null)
                            {
                                var definition = _commonExporters.StandardDataTypes.Definitions.FirstOrDefault(x => x.DefinitionCode == mapping.Target);
                                if (definition != null && definition.NumericDataTypeDefinitionAttributes != null)
                                {
                                    var variable = new VariableElement
                                    {
                                        Id = new Id { ReferenceId = string.Concat(rx.Id.ReferenceId, "-ProductArea", productUse.ProductId) },
                                        Description = string.Concat(manualRx.Description, "_", product.Description),
                                        ProductId = productUse.ProductId.ToString(),
                                        DefinitionCode = definition.DefinitionCode,
                                    };
                                    variables.Add(variable);
                                    SummaryValueElement summaryValue = new SummaryValueElement()
                                    {
                                        VariableId = variable.Id.ReferenceId,
                                        ValueText = productUse.AppliedArea.AsConvertedDouble(definition.NumericDataTypeDefinitionAttributes.UnitOfMeasureCode).ToString()
                                    };
                                    summaryValues.Add(summaryValue);
                                }
                            }
                        }
                        if (productUse.ProductTotal != null)
                        {
                            var mapping = _commonExporters.TypeMappings.FirstOrDefault(x => x.Source == productUse.ProductTotal.Representation?.Code);
                            if (mapping != null)
                            {
                                var definition = _commonExporters.StandardDataTypes.Definitions.FirstOrDefault(x => x.DefinitionCode == mapping.Target);
                                if (definition != null && definition.NumericDataTypeDefinitionAttributes != null)
                                {
                                    var variable = new VariableElement
                                    {
                                        Id = new Id { ReferenceId = string.Concat(rx.Id.ReferenceId, "-ProductTotal", productUse.ProductId) },
                                        Description = string.Concat(manualRx.Description, "_", product.Description),
                                        ProductId = productUse.ProductId.ToString(),
                                        DefinitionCode = definition.DefinitionCode,
                                    };
                                    variables.Add(variable);
                                    SummaryValueElement summaryValue = new SummaryValueElement()
                                    {
                                        VariableId = variable.Id.ReferenceId,
                                        ValueText = productUse.ProductTotal.AsConvertedDouble(definition.NumericDataTypeDefinitionAttributes.UnitOfMeasureCode).ToString()
                                    };
                                    summaryValues.Add(summaryValue);
                                }
                            }
                        }

                    }
                    else
                    {
                        errors.Add(new Error
                        {
                            Description = $"Omitting Manual Prescription Product Use.   No matching product id {productUse.ProductId.ToString()}",
                        });
                    }
                }

            }
            else if (rx.RxProductLookups.Any())
            {
                List<WorkOrderExportColumn> exportColumns = new List<WorkOrderExportColumn>();
                int index = 0;
                List<int> exportableLookupIds = rx.RxProductLookups.Where(rpl => _commonExporters.TypeMappings.Any(m => m.Source == rpl.Representation?.Code)).Select(x => x.Id.ReferenceId).ToList();    //If we can't map it to a standard data type definition, we won't export it.
                foreach (var frameworkRxProductLookup in rx.RxProductLookups.Where(rpl => exportableLookupIds.Contains(rpl.Id.ReferenceId)))
                {
                    var mapping = _commonExporters.TypeMappings.First(m => m.Source == frameworkRxProductLookup.Representation?.Code);
                    string targetUOMCode = _commonExporters.StandardDataTypes.Definitions.First(x => x.DefinitionCode == mapping.Target).NumericDataTypeDefinitionAttributes.UnitOfMeasureCode;
                    if (frameworkRxProductLookup.UnitOfMeasure.CanConvertInto(targetUOMCode))
                    {
                        string columnName = null;
                        int? bandIndex = null;
                        var productId = frameworkRxProductLookup.ProductId;
                        if (rx is RasterGridPrescription)
                        {
                            bandIndex = ++index;
                        }
                        else if (rx is VectorPrescription)
                        {
                            columnName = mapping.Target;
                            if (productId != null)
                            {
                                var product = catalog.Products.FirstOrDefault(p => p.Id.ReferenceId == productId);
                                if (product != null)
                                {
                                    columnName = string.Concat(columnName, "_", product.Description);
                                }
                            }
                        }
                        var variable = new VariableElement
                        {
                            Id = _commonExporters.ExportID(frameworkRxProductLookup.Id),
                            Name = frameworkRxProductLookup.Representation?.Code?.AsName("Variable", frameworkRxProductLookup.Id.ReferenceId.ToString()),
                            GeoParquetColumnName = columnName, 
                            GeoTiffBandIndex = bandIndex,
                            LossOfGnssRate = ExportNumericValue(frameworkRxProductLookup.LossOfGpsRate),
                            OutOfFieldRate = ExportNumericValue(frameworkRxProductLookup.OutOfFieldRate),
                            ProductId = productId?.ToString(CultureInfo.InvariantCulture),
                            DefinitionCode = mapping.Target,
                        };

                        exportColumns.Add(new WorkOrderExportColumn()
                        {
                            Variable = variable,
                            ConversionFactor = 1d.ConvertValue(frameworkRxProductLookup.UnitOfMeasure.Code, targetUOMCode).Value,
                            ProductLookup = frameworkRxProductLookup,
                        });
                    }
                    else
                    {
                        errors.Add(new Error
                        {
                            Description = $"Omitting {frameworkRxProductLookup.Representation.Code} in {frameworkRxProductLookup.UnitOfMeasure.Code} because it cannot be converted to {targetUOMCode}",
                        });
                    }
                }

                if (rx is RasterGridPrescription rasterRx)
                {
                    spatialFile = ExportRaster(rasterRx, exportColumns, errors);
                }
                else if (rx is VectorPrescription vectorRx)
                {
                    spatialFile = ExportVector(vectorRx, catalog, exportColumns);
                }
                variables = exportColumns.Select(x => x.Variable).ToList();
            }
            return (variables, spatialFile, summaryValues, errors);
        }

        private double? ExportNumericValue(NumericRepresentationValue srcNumericValue)
        {
            return srcNumericValue?.Value?.Value;
        }

        private string ExportVector(VectorPrescription srcRx, ApplicationDataModel.ADM.Catalog catalog, List<WorkOrderExportColumn> exportColumns)
        {
            if (srcRx.RxShapeLookups.IsNullOrEmpty())
            {
                return null;
            }

            var fileName = $"{srcRx.Description ?? ""}{srcRx.Id.ReferenceId}.parquet";
            Directory.CreateDirectory(_exportPath);
            string outputPath = Path.Combine(_exportPath, fileName);

            ADAPTParquetColumnData columnData = new ADAPTParquetColumnData();
            columnData.AddVectorPrescription(srcRx, exportColumns, catalog, _commonExporters);
            var values = new double?[columnData.Columns.Count];

            using (var writer = new ADAPTParquetWriter(columnData, includeTimestamps: false, openFile: () => outputPath, geometryType: "MultiPolygon"))
            {
                foreach (var shapeLookup in srcRx.RxShapeLookups)
                {
                    var geometry = GeometryExporter.ExportMultiPolygon(shapeLookup.Shape);
                    var envelope = geometry.EnvelopeInternal;
                    for (int columnIndex = 0; columnIndex < columnData.Columns.Count; columnIndex++)
                    {
                        var dataColumn = columnData.Columns[columnIndex];
                        var exportColumn = exportColumns.FirstOrDefault(x => x.Variable.GeoParquetColumnName == dataColumn.TargetName);
                        if (exportColumn != null)
                        {
                            var rate = shapeLookup.Rates.FirstOrDefault(x => x.RxProductLookupId == exportColumn.ProductLookup.Id.ReferenceId);
                            values[columnIndex] = rate.Rate * exportColumn.ConversionFactor;
                        }
                        else
                        {
                            values[columnIndex] = 0d;
                        }
                    }

                    writer.AddRow(null, values, geometry.ToBinary(), envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
                }

                writer.Complete();
            }

            return fileName;
        }

        private string ExportRaster(RasterGridPrescription srcRx, List<WorkOrderExportColumn> exportColumns, List<IError> errors)
        {
            if (srcRx.Rates.IsNullOrEmpty())
            {
                return null;
            }

            if (!ValidateInputs(srcRx, errors))
            {
                return null;
            }

            var fileName = $"{srcRx.Description ?? ""}{srcRx.Id.ReferenceId}.tiff";
            Directory.CreateDirectory(_exportPath);
            string outputPath = Path.Combine(_exportPath, fileName);
            // LibTiff's tag extender is process-global. The lock is static so two exports
            // cannot replace it, and it is held across the write.
            lock (_tiffSyncRoot)
            {
                _parentTagExtender = Tiff.SetTagExtender(TagExtender);
                try
                {
                    using (var tiff = Tiff.Open(outputPath, "w"))
                    {
                        var productCount = exportColumns.Count;
                        var columnCount = srcRx.ColumnCount;
                        var rowCount = srcRx.RowCount;
                        tiff.SetField(TiffTag.IMAGEWIDTH, columnCount);
                        tiff.SetField(TiffTag.IMAGELENGTH, rowCount);
                        tiff.SetField(TiffTag.SAMPLESPERPIXEL, productCount);
                        tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP);
                        tiff.SetField(TiffTag.BITSPERSAMPLE, 64);
                        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                        tiff.SetField(TiffTag.ROWSPERSTRIP, 1);
                        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);

                        tiff.SetField(TiffTag.COMPRESSION, Compression.DEFLATE);

                        // Rows are ordered bottom to top in RxPrescription but in GeoTIFF they are stored top to bottom.
                        // Set model tie point to north-west corner which corresponds to the last row in input data
                        // and reverse the order of cells later on.

                        // Model tie point specifies location of a point in raster space (first 3 numbers) in vector space (last 3 numbers)
                        var originX = srcRx.Origin.X;
                        var originY = srcRx.Origin.Y + srcRx.CellHeight.Value.Value;
                        double[] tiePoints = new double[] { 0, rowCount - 1, 0, originX, originY, 0 };
                        tiff.SetField(TiffTag.GEOTIFF_MODELTIEPOINTTAG, 6, tiePoints);

                        double[] pixelScale = new double[] { srcRx.CellWidth.Value.Value, srcRx.CellHeight.Value.Value, 0 };
                        tiff.SetField(TiffTag.GEOTIFF_MODELPIXELSCALETAG, 3, pixelScale);

                        // First line a header, defining version and how many values follows.
                        // Each next line consist of 4 fields: geo key, value location/type, how many values, actual value
                        short[] geoDir = new short[4 * 4]
                        {
                            // KeyDirectoryVersion (1), KeyRevision (1), MinorRevision (2), NumberOfKeys (3 - how many additional lines)
                              1,    1,  2,  3 ,
                            // GTModelTypeGeoKey (1024), TIFFTagLocation (0 - short), Count (1), ModelTypeGeographic (2 - Geographic latitude-longitude System)
                            1024,   0,  1,  2 ,
                            // GTRasterTypeGeoKey (1025), TIFFTagLocation (0 - short), Count (1), RasterPixelIsArea (1)
                            1025,   0,  1,  1 ,
                            // GeographicTypeGeoKey (2048), TIFFTagLocation (0 - short), Count (1), GCS_WGS_84 (4326)
                            2048,   0,  1,  4326
                        };
                        tiff.SetField(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG, geoDir.Length, geoDir);

                        int bytesPerRow = columnCount * productCount * sizeof(double);
                        byte[] rowBuffer = new byte[Math.Max(bytesPerRow, 0)];
                        if (columnCount > 0)
                        {
                            var sourceRowStarts = new List<int>();
                            for (int sourceRowStart = 0; sourceRowStart < srcRx.Rates.Count; sourceRowStart += columnCount)
                            {
                                sourceRowStarts.Add(sourceRowStart);
                            }

                            sourceRowStarts.Reverse();
                            for (int rowIndex = 0; rowIndex < sourceRowStarts.Count; rowIndex++)
                            {
                                int sourceRowStart = sourceRowStarts[rowIndex];
                                int cellsInRow = Math.Min(columnCount, srcRx.Rates.Count - sourceRowStart);
                                Span<byte> rowSpan = rowBuffer;
                                int byteOffset = 0;
                                for (int columnIndex = 0; columnIndex < cellsInRow; columnIndex++)
                                {
                                    var cell = srcRx.Rates[sourceRowStart + columnIndex];
                                    foreach (var exportColumn in exportColumns)
                                    {
                                        var rxRate = cell.RxRates.FirstOrDefault(x => x.RxProductLookupId == exportColumn.ProductLookup.Id.ReferenceId);
                                        double rate = rxRate != null ? rxRate.Rate * exportColumn.ConversionFactor : 0d;
                                        BinaryPrimitives.WriteDoubleLittleEndian(rowSpan.Slice(byteOffset, sizeof(double)), rate);
                                        byteOffset += sizeof(double);
                                    }
                                }

                                tiff.WriteEncodedStrip(rowIndex, rowBuffer, byteOffset);
                            }
                        }
                    }
                }
                finally
                {
                    Tiff.SetTagExtender(_parentTagExtender);
                    _parentTagExtender = null;
                }
            }

            return fileName;
        }

        private bool ValidateInputs(RasterGridPrescription srcRxPrescription, List<IError> errors)
        {
            var uom = srcRxPrescription.CellHeight.Value.UnitOfMeasure?.Code;
            if (uom != null && !uom.EqualsIgnoreCase("arcdeg"))
            {
                errors.Add(new Error
                {
                    Id = srcRxPrescription.Id.ReferenceId.ToString(CultureInfo.InvariantCulture),
                    Description = "CellHeight unif of measure should either be null or arcdeg.",
                });
                return false;
            }

            uom = srcRxPrescription.CellWidth.Value.UnitOfMeasure?.Code;
            if (uom != null && !uom.EqualsIgnoreCase("arcdeg"))
            {
                errors.Add(new Error
                {
                    Id = srcRxPrescription.Id.ReferenceId.ToString(CultureInfo.InvariantCulture),
                    Description = "CellWidth unif of measure should either be null or arcdeg.",
                });
                return false;
            }

            return true;
        }

        private void TagExtender(Tiff tiff)
        {
            TiffFieldInfo[] tiffFieldInfo =
            {
                new TiffFieldInfo(TiffTag.GEOTIFF_MODELTIEPOINTTAG, 6, 6, TiffType.DOUBLE, FieldBit.Custom, false, true, "MODELTILEPOINTTAG"),
                new TiffFieldInfo(TiffTag.GEOTIFF_MODELPIXELSCALETAG, 3, 3, TiffType.DOUBLE, FieldBit.Custom, false, true, "MODELPIXELSCALETAG"),
                new TiffFieldInfo(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG, 4 * 4, 4 * 4, TiffType.SHORT, FieldBit.Custom, false, true, "GEOKEYDIRECTORYTAG"),
            };

            tiff.MergeFieldInfo(tiffFieldInfo, tiffFieldInfo.Length);

            _parentTagExtender?.Invoke(tiff);
        }
    }
}

internal class WorkOrderExportColumn
{
    internal VariableElement Variable { get; set; }
    internal RxProductLookup ProductLookup { get; set; }
    internal double ConversionFactor { get; set; }
}