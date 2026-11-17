using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Common;
using AgGateway.ADAPT.ApplicationDataModel.Documents;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Products;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.Standard;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal class WorkRecordExporter
    {
        // Same window as operation merging. Work records compare the gap between logged-data intervals.
        internal const double ContemporaneousHours = 36d;
        private readonly SourceGeometryPosition _geometryPositition;
        private readonly SourceDeviceDefinition _deviceDefinition;
        private readonly CommonExporters _commonExporters;
        private readonly Standard.Root _root;
        private readonly string _exportPath;
        private readonly Properties _properties;
        private readonly bool _multithread;
        private readonly int _maxParallelism;
        private readonly ExportIdAllocator _idAllocator;
        private WorkRecordExporter(Root root, string exportPath, Properties properties)
        {
            _properties = properties;
            _multithread = ExportSettings.UseMultithread(properties);
            _maxParallelism = ExportSettings.GetMaxParallelism(properties);
            _idAllocator = new ExportIdAllocator();
            _exportPath = exportPath;
            _root = root;
            _root.Documents.WorkRecords = new List<WorkRecordElement>();
            _commonExporters = new CommonExporters(root);

            _geometryPositition = SourceGeometryPosition.GNSSReceiver;
            _deviceDefinition = SourceDeviceDefinition.DeviceElementHierarchy;
            if (properties != null)
            {
                var geomString = properties.GetProperty("SourceGeometryPosition");
                var dvcString = properties.GetProperty("SourceDeviceDefinition");
                if (geomString != null && Enum.TryParse(geomString, out SourceGeometryPosition position))
                {
                    _geometryPositition = position;
                }
                if (dvcString != null && Enum.TryParse(dvcString, out SourceDeviceDefinition definition))
                {
                    _deviceDefinition = definition;
                }
            }
        }

        public static IEnumerable<IError> Export(ApplicationDataModel.ADM.ApplicationDataModel model, Root root, string exportPath, Properties properties)
        {
            WorkRecordExporter exporter = new WorkRecordExporter(root, exportPath, properties);
            if (model.Documents?.LoggedData != null)
            {
                return exporter.Export(model);
            }
            else
            {
                return new List<IError>();
            }
        }

        private IEnumerable<OperationData> GetMappableOperations(LoggedData loggedData)
        {
            return loggedData.OperationData.Where(od => od.OperationType != OperationTypeEnum.DataCollection && //Data Collection always represents tractor data in production plugins and won't map to ADAPT Standard types
                                                        od.OperationType != OperationTypeEnum.Transport &&
                                                        od.GetSpatialRecords().Any());
        }

        private IEnumerable<IError> Export(ApplicationDataModel.ADM.ApplicationDataModel model)
        {
            List<IError> errors = new List<IError>();
            Directory.CreateDirectory(_exportPath);
            var fieldGroups = model.Documents.LoggedData.GroupBy(ld => ld.FieldId).Select((group, index) => new { group, index }).ToList();

            for (int i = 0; i < fieldGroups.Count; i++)
            {
                var fieldResult = ExportFieldGroup(fieldGroups[i].group, fieldGroups[i].index, model);
                _root.Documents.WorkRecords.AddRange(fieldResult.WorkRecords);

                errors.AddRange(fieldResult.Errors);
                // Release this field after its operations have finished, including when they ran
                // concurrently. The next field does not start until this release returns.
                foreach (var loggedData in fieldResult.LoggedDataToRelease)
                {
                    loggedData.ReleaseSpatialData?.Invoke();
                }
            }

            if (!_root.Documents.WorkRecords.Any())
            {
                _root.Documents.WorkRecords = null;
            }

            errors.AddRange(_commonExporters.Errors);
            return errors;
        }

        private enum WorkRecordGroup
        {
            Application,
            Harvest,
            Tillage
        }

        private sealed class LoggedDataSlice
        {
            internal LoggedData LoggedData { get; set; }
            internal List<OperationData> Operations { get; set; }
            internal WorkRecordGroup Group { get; set; }
            internal bool SplitFromLoggedData { get; set; }
            internal int? CropId { get; set; }
            internal bool HasCropConflict { get; set; }
            internal DateTime? Start { get; set; }
            internal DateTime? End { get; set; }
        }

        private FieldExportResult ExportFieldGroup(IGrouping<int?, LoggedData> fieldIdGroupBy, int fieldIndex, ApplicationDataModel.ADM.ApplicationDataModel model)
        {
            FieldExportResult result = new FieldExportResult { FieldIndex = fieldIndex };
            foreach (var cluster in ClusterSlices(BuildSlices(fieldIdGroupBy, model.Catalog)))
            {
                var workRecord = ExportCluster(cluster, model, result.Errors);
                if (workRecord != null)
                {
                    result.WorkRecords.Add(workRecord);
                }
            }

            result.LoggedDataToRelease.AddRange(fieldIdGroupBy);
            return result;
        }

        // Harvest and tillage each get their own records so a following pass does not join them.
        // Planting, fertilizing, and crop protection stay together when their intervals are close
        // and they do not report two different crops.
        private static WorkRecordGroup ClassifyOperation(OperationTypeEnum operationType)
        {
            switch (operationType)
            {
                case OperationTypeEnum.Harvesting:
                case OperationTypeEnum.ForageHarvesting:
                case OperationTypeEnum.Baling:
                    return WorkRecordGroup.Harvest;
                case OperationTypeEnum.Tillage:
                    return WorkRecordGroup.Tillage;
                default:
                    return WorkRecordGroup.Application;
            }
        }

        private List<LoggedDataSlice> BuildSlices(IEnumerable<LoggedData> loggedDataItems, ApplicationDataModel.ADM.Catalog catalog)
        {
            var slices = new List<LoggedDataSlice>();
            foreach (var loggedData in loggedDataItems)
            {
                int? zoneCropId = CropFromZone(catalog, loggedData.CropZoneId);
                bool loggedDataHasInterval = TryGetActualInterval(loggedData.TimeScopes, out DateTime loggedDataStart, out DateTime loggedDataEnd);
                var pending = new List<LoggedDataSlice>();
                foreach (var group in GetMappableOperations(loggedData).GroupBy(operation => ClassifyOperation(operation.OperationType)))
                {
                    var operationsByCrop = new Dictionary<int, List<OperationData>>();
                    var operationsWithoutCrop = new List<OperationData>();
                    foreach (var operation in group)
                    {
                        var cropIds = ReportedCropIds(operation, zoneCropId, catalog);
                        if (cropIds.Count > 1)
                        {
                            pending.Add(CreateSlice(loggedData, group.Key, new List<OperationData> { operation }, null, true));
                        }
                        else if (cropIds.Count == 1)
                        {
                            int cropId = cropIds.First();
                            if (!operationsByCrop.TryGetValue(cropId, out var operations))
                            {
                                operations = new List<OperationData>();
                                operationsByCrop.Add(cropId, operations);
                            }

                            operations.Add(operation);
                        }
                        else
                        {
                            operationsWithoutCrop.Add(operation);
                        }
                    }

                    foreach (var crop in operationsByCrop)
                    {
                        pending.Add(CreateSlice(loggedData, group.Key, crop.Value, crop.Key, false));
                    }

                    if (operationsWithoutCrop.Any())
                    {
                        pending.Add(CreateSlice(loggedData, group.Key, operationsWithoutCrop, null, false));
                    }
                }

                foreach (var slice in pending)
                {
                    if (loggedDataHasInterval)
                    {
                        slice.Start = loggedDataStart;
                        slice.End = loggedDataEnd;
                    }
                    else if (TryGetFirstSpatialInterval(slice.Operations, out DateTime operationStart, out DateTime operationEnd))
                    {
                        slice.Start = operationStart;
                        slice.End = operationEnd;
                    }
                }

                bool split = pending.Count > 1;
                foreach (var slice in pending)
                {
                    slice.SplitFromLoggedData = split;
                    slices.Add(slice);
                }
            }

            return slices;
        }

        private static LoggedDataSlice CreateSlice(LoggedData loggedData, WorkRecordGroup group, List<OperationData> operations, int? cropId, bool hasCropConflict)
        {
            return new LoggedDataSlice
            {
                LoggedData = loggedData,
                Operations = operations,
                Group = group,
                CropId = cropId,
                HasCropConflict = hasCropConflict
            };
        }

        // A crop zone and a variety or harvested-commodity product both report a crop.
        // A fertilizer or other product with no crop does not.
        private static List<int> ReportedCropIds(OperationData operation, int? zoneCropId, ApplicationDataModel.ADM.Catalog catalog)
        {
            var cropIds = new HashSet<int>();
            if (zoneCropId.HasValue)
            {
                cropIds.Add(zoneCropId.Value);
            }

            if (operation.ProductIds != null && catalog?.Products != null)
            {
                foreach (var productId in operation.ProductIds)
                {
                    var product = catalog.Products.FirstOrDefault(candidate => candidate.Id.ReferenceId == productId);
                    foreach (var cropId in ProductCropIds(product, catalog))
                    {
                        cropIds.Add(cropId);
                    }
                }
            }

            return cropIds.ToList();
        }

        private static IEnumerable<int> ProductCropIds(Product product, ApplicationDataModel.ADM.Catalog catalog)
        {
            switch (product)
            {
                case CropVarietyProduct variety:
                    yield return variety.CropId;
                    yield break;
                case HarvestedCommodityProduct commodity:
                    yield return commodity.CropId;
                    yield break;
                case null:
                    yield break;
            }

            if (product.ProductComponents == null)
            {
                yield break;
            }

            foreach (var component in product.ProductComponents)
            {
                if (component == null || !component.IsProduct || catalog?.Products == null)
                {
                    continue;
                }

                var componentProduct = catalog.Products.FirstOrDefault(candidate => candidate.Id.ReferenceId == component.IngredientId);
                switch (componentProduct)
                {
                    case CropVarietyProduct variety:
                        yield return variety.CropId;
                        break;
                    case HarvestedCommodityProduct commodity:
                        yield return commodity.CropId;
                        break;
                }
            }
        }

        private static int? CropFromZone(ApplicationDataModel.ADM.Catalog catalog, int? cropZoneId)
        {
            if (!cropZoneId.HasValue || catalog?.CropZones == null)
            {
                return null;
            }

            return catalog.CropZones.FirstOrDefault(zone => zone.Id.ReferenceId == cropZoneId.Value)?.CropId;
        }

        // Without logged-data scopes, the first spatial point of each operation is the whole interval.
        private static bool TryGetFirstSpatialInterval(IEnumerable<OperationData> operations, out DateTime start, out DateTime end)
        {
            start = DateTime.MaxValue;
            end = DateTime.MinValue;
            bool found = false;
            foreach (var operation in operations)
            {
                if (TryGetFirstSpatialTimestamp(operation, out DateTime point))
                {
                    found = IncludeActualTimestamp(point, ref start, ref end) || found;
                }
            }

            return found;
        }

        private static bool TryGetFirstSpatialTimestamp(OperationData operation, out DateTime timestamp)
        {
            timestamp = default;
            var records = operation?.GetSpatialRecords?.Invoke();
            if (records == null)
            {
                return false;
            }

            var first = records.Take(1).FirstOrDefault();
            if (first == null)
            {
                return false;
            }

            timestamp = first.Timestamp;
            return true;
        }

        private static bool TryGetActualInterval(IEnumerable<TimeScope> timeScopes, out DateTime start, out DateTime end)
        {
            start = DateTime.MaxValue;
            end = DateTime.MinValue;
            if (timeScopes == null)
            {
                return false;
            }

            bool found = false;
            foreach (var timeScope in timeScopes)
            {
                if (timeScope == null ||
                    (timeScope.DateContext != DateContextEnum.ActualStart && timeScope.DateContext != DateContextEnum.ActualEnd))
                {
                    continue;
                }

                if (timeScope.TimeStamp1.HasValue)
                {
                    found = IncludeActualTimestamp(timeScope.TimeStamp1.Value, ref start, ref end) || found;
                }

                if (timeScope.TimeStamp2.HasValue)
                {
                    found = IncludeActualTimestamp(timeScope.TimeStamp2.Value, ref start, ref end) || found;
                }
            }

            return found;
        }

        private static bool IncludeActualTimestamp(DateTime timestamp, ref DateTime start, ref DateTime end)
        {
            if (timestamp < start)
            {
                start = timestamp;
            }

            if (timestamp > end)
            {
                end = timestamp;
            }

            return true;
        }

        private static List<List<LoggedDataSlice>> ClusterSlices(List<LoggedDataSlice> slices)
        {
            var clusters = new List<List<LoggedDataSlice>>();
            foreach (var group in slices.GroupBy(slice => slice.Group))
            {
                clusters.AddRange(ClusterGroup(group.ToList()));
            }

            return clusters
                .OrderBy(cluster => cluster.Select(slice => slice.Start).Min() ?? DateTime.MaxValue)
                .ThenBy(cluster => cluster[0].Group)
                .ThenBy(cluster => cluster.Select(slice => slice.CropId).FirstOrDefault(cropId => cropId.HasValue) ?? int.MaxValue)
                .ToList();
        }

        private static List<List<LoggedDataSlice>> ClusterGroup(List<LoggedDataSlice> slices)
        {
            var clusters = new List<List<LoggedDataSlice>>();
            var timed = slices.Where(slice => !slice.HasCropConflict && slice.Start.HasValue && slice.End.HasValue).ToList();
            var cropClusters = new List<List<LoggedDataSlice>>();
            foreach (var crop in timed.Where(slice => slice.CropId.HasValue).GroupBy(slice => slice.CropId.Value))
            {
                cropClusters.AddRange(ClusterByTime(crop.ToList()));
            }

            foreach (var freeCluster in ClusterByTime(timed.Where(slice => !slice.CropId.HasValue).ToList()))
            {
                var matches = cropClusters.Where(cropCluster => GapHours(freeCluster, cropCluster) < ContemporaneousHours).ToList();
                if (matches.Count == 1)
                {
                    matches[0].AddRange(freeCluster);
                }
                else
                {
                    clusters.Add(freeCluster);
                }
            }

            clusters.AddRange(cropClusters);
            foreach (var slice in slices.Where(slice => slice.HasCropConflict || !slice.Start.HasValue || !slice.End.HasValue))
            {
                clusters.Add(new List<LoggedDataSlice> { slice });
            }

            return clusters;
        }

        private static List<List<LoggedDataSlice>> ClusterByTime(List<LoggedDataSlice> slices)
        {
            var clusters = new List<List<LoggedDataSlice>>();
            List<LoggedDataSlice> current = null;
            DateTime clusterEnd = default;
            foreach (var slice in slices.OrderBy(slice => slice.Start.Value).ThenBy(slice => slice.LoggedData.Id.ReferenceId))
            {
                if (current == null || (slice.Start.Value - clusterEnd).TotalHours >= ContemporaneousHours)
                {
                    current = new List<LoggedDataSlice> { slice };
                    clusters.Add(current);
                    clusterEnd = slice.End.Value;
                }
                else
                {
                    current.Add(slice);
                    if (slice.End.Value > clusterEnd)
                    {
                        clusterEnd = slice.End.Value;
                    }
                }
            }

            return clusters;
        }

        private static double GapHours(List<LoggedDataSlice> left, List<LoggedDataSlice> right)
        {
            DateTime leftStart = left.Min(slice => slice.Start.Value);
            DateTime leftEnd = left.Max(slice => slice.End.Value);
            DateTime rightStart = right.Min(slice => slice.Start.Value);
            DateTime rightEnd = right.Max(slice => slice.End.Value);
            if (leftStart <= rightEnd && rightStart <= leftEnd)
            {
                return 0d;
            }

            return rightStart > leftEnd
                ? (rightStart - leftEnd).TotalHours
                : (leftStart - rightEnd).TotalHours;
        }

        private WorkRecordElement ExportCluster(List<LoggedDataSlice> cluster, ApplicationDataModel.ADM.ApplicationDataModel model, List<IError> errors)
        {
            List<OperationDefinition> groupedOperations = new List<OperationDefinition>();
            foreach (var slice in cluster)
            {
                foreach (var operationData in slice.Operations)
                {
                    Implement implement = new Implement(operationData, model.Catalog, _geometryPositition, _deviceDefinition, _commonExporters.TypeMappings, errors);

                    OperationDefinition operationDefinition = new OperationDefinition(implement, operationData, slice.LoggedData);
                    var matchingOperation = groupedOperations.FirstOrDefault(x => x.IsMatchingOperation(operationDefinition));
                    if (matchingOperation != null)
                    {
                        matchingOperation.SourceOperations.Add(new ConstituentSpatialOperation(operationData, implement));
                        matchingOperation.SourceLoggedDatas.Add(slice.LoggedData);
                    }
                    else
                    {
                        errors.AddRange(operationDefinition.ColumnData.AddOperationData(operationData, model.Catalog, implement, _commonExporters));

                        if (operationDefinition.ColumnData.Columns.Any())
                        {
                            groupedOperations.Add(operationDefinition);
                        }
                        else
                        {
                            continue;
                        }

                        VariableElement timestamp = new VariableElement()
                        {
                            Name = "Timestamp",
                            DefinitionCode = "Timestamp",
                            Id = new Id() { ReferenceId = string.Concat(operationDefinition.Key(), "-timestamp") },
                            GeoParquetColumnName = "Timestamp"
                        };
                        operationDefinition.VariablesByOutputName.Add("Timestamp", timestamp);
                        foreach (var dataColumn in operationDefinition.ColumnData.Columns)
                        {
                            if (!operationDefinition.VariablesByOutputName.ContainsKey(dataColumn.TargetName))
                            {
                                VariableElement variable = new VariableElement()
                                {
                                    Name = dataColumn.SrcName,
                                    DefinitionCode = dataColumn.TargetCode,
                                    ProductId = dataColumn.ProductId,
                                    Id = _commonExporters.ExportID(dataColumn.SrcWorkingData.Id),
                                    GeoParquetColumnName = dataColumn.TargetName
                                };
                                operationDefinition.VariablesByOutputName.Add(dataColumn.TargetName, variable);
                            }
                        }
                    }
                }
            }

            var loggedDatas = cluster.Select(slice => slice.LoggedData).Distinct().ToList();
            HashSet<Summary> sourceSummaries = new HashSet<Summary>();
            foreach (var loggedData in loggedDatas)
            {
                var summary = model.Documents.Summaries.FirstOrDefault(x => x.LoggedDataIds.Contains(loggedData.Id.ReferenceId) || x.Id.ReferenceId == loggedData.SummaryId);
                if (summary != null)
                {
                    sourceSummaries.Add(summary);
                }
            }
            foreach (var sourceSummary in sourceSummaries)
            {
                if (sourceSummary.OperationSummaries != null &&
                    sourceSummary.OperationSummaries.Any())
                {
                    foreach (var srcOpSummary in sourceSummary.OperationSummaries)
                    {
                        foreach (var groupedOperation in groupedOperations)
                        {
                            if (groupedOperation.IsMatchingOperationSummary(srcOpSummary))
                            {
                                if (!groupedOperation.SourceSummaryValuesByProductId.ContainsKey(srcOpSummary.ProductId))
                                {
                                    groupedOperation.SourceSummaryValuesByProductId.Add(srcOpSummary.ProductId, new List<StampedMeteredValues>());
                                }
                                groupedOperation.SourceSummaryValuesByProductId[srcOpSummary.ProductId].AddRange(srcOpSummary.Data);
                                break;
                            }
                        }
                    }
                }
                else if (groupedOperations.Count == 1 &&
                      groupedOperations.First().IsMatchingSummary(sourceSummary))
                {
                    groupedOperations.First().SourceSummaryValuesWithoutProduct.AddRange(sourceSummary.SummaryData);
                }
                else if (!groupedOperations.Any())
                {
                }
                else
                {
                    errors.Add(new Error
                    {
                        Id = sourceSummary.Id.ReferenceId.ToString(CultureInfo.InvariantCulture),
                        Description = "Did not convert summary; could not map to an operation.",
                    });
                }
            }

            if (!groupedOperations.Any())
            {
                return null;
            }

            var cropZoneIds = loggedDatas.Select(loggedData => loggedData.CropZoneId).Distinct().ToList();
            var cropZoneId = cropZoneIds.Count == 1 && cropZoneIds[0].HasValue
                ? cropZoneIds[0].Value.ToString(CultureInfo.InvariantCulture)
                : null;
            var notes = loggedDatas.SelectMany(loggedData => loggedData.Notes).Any()
                ? loggedDatas.SelectMany(loggedData => loggedData.Notes).Select(note => note.Description).ToList()
                : null;
            List<TimeScopeElement> timeScopeElements = new List<TimeScopeElement>();
            string seasonId = null;
            foreach (var loggedData in loggedDatas)
            {
                var timeScopes = _commonExporters.ExportTimeScopes(loggedData.TimeScopes, out var seasonIds);
                if (timeScopes != null)
                {
                    timeScopeElements.AddRange(timeScopes);
                }
                if (seasonIds.Any())
                {
                    seasonId = seasonIds.First();
                }
            }

            var fieldWorkRecord = new WorkRecordElement
            {
                Operations = new List<OperationElement>(),
                FieldId = groupedOperations.First().FieldId,
                Id = CreateWorkRecordId(cluster, loggedDatas),
                CropZoneId = cropZoneId,
                Name = string.Join("_", loggedDatas.Select(loggedData => loggedData.Description)),
                Notes = notes,
                TimeScopes = timeScopeElements.Any() ? timeScopeElements : null,
                SeasonId = seasonId
            };

            // Ids and file names are reserved in list order before any writer opens.
            // Spatial export may then run concurrently. Operation elements, including total-N
            // variable ids, are built afterward in that same order.
            ReserveOperationOutputs(groupedOperations, model);
            if (!_multithread || groupedOperations.Count <= 1)
            {
                foreach (var operationDefinition in groupedOperations)
                {
                    ExportOperationSpatialData(operationDefinition, model);
                }
            }
            else
            {
                try
                {
                    Parallel.For(0, groupedOperations.Count, new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism }, operationIndex =>
                    {
                        ExportOperationSpatialData(groupedOperations[operationIndex], model);
                    });
                }
                catch (AggregateException aggregate) when (aggregate.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(aggregate.InnerExceptions[0]).Throw();
                }
            }

            foreach (var operationDefinition in groupedOperations)
            {
                if (operationDefinition.ColumnData.RowCount > 0 ||
                    operationDefinition.SourceSummaryValuesByProductId.Any() ||
                    operationDefinition.SourceSummaryValuesWithoutProduct.Any())
                {
                    fieldWorkRecord.Operations.Add(ExportOperation(operationDefinition, model, fieldWorkRecord, errors));
                }
            }

            return fieldWorkRecord;
        }

        private Id CreateWorkRecordId(List<LoggedDataSlice> cluster, List<LoggedData> loggedDatas)
        {
            if (loggedDatas.Count == 1 && !cluster[0].SplitFromLoggedData)
            {
                return _commonExporters.ExportID(loggedDatas[0].Id);
            }

            string fieldId = loggedDatas[0].FieldId?.ToString(CultureInfo.InvariantCulture) ?? CatalogExporter.UnknownFieldId;
            DateTime? clusterStart = null;
            foreach (var slice in cluster)
            {
                if (slice.Start.HasValue && (clusterStart == null || slice.Start.Value < clusterStart.Value))
                {
                    clusterStart = slice.Start;
                }
            }

            string startToken = clusterStart.HasValue
                ? clusterStart.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
                : "unscoped";
            return new Id
            {
                ReferenceId = string.Concat("field_", fieldId, "_", cluster[0].Group.ToString(), "_", startToken, "_", CropToken(cluster), "_WorkRecord")
            };
        }

        private static string CropToken(List<LoggedDataSlice> cluster)
        {
            if (cluster.Any(slice => slice.HasCropConflict))
            {
                return string.Concat("mixed_", cluster[0].Operations[0].Id.ReferenceId.ToString(CultureInfo.InvariantCulture));
            }

            var cropIds = cluster.Select(slice => slice.CropId).Where(cropId => cropId.HasValue).Select(cropId => cropId.Value).Distinct().ToList();
            return cropIds.Count == 1
                ? cropIds[0].ToString(CultureInfo.InvariantCulture)
                : "none";
        }


        private void ExportOperationSpatialData(OperationDefinition operationDefinition, ApplicationDataModel.ADM.ApplicationDataModel model)
        {
            using (var writer = new ADAPTParquetWriter(
                operationDefinition.ColumnData,
                includeTimestamps: true,
                openFile: () => Path.Combine(_exportPath, operationDefinition.SpatialRecordsFile)))
            {
                foreach (var constituentSpatialOperation in operationDefinition.SourceOperations)
                {
                    ExportOperationSpatialRecords(writer, constituentSpatialOperation.Implement, constituentSpatialOperation.OperationData);
                }

                writer.Complete();
            }
        }

        private OperationElement ExportOperation(OperationDefinition operationDefinition, ApplicationDataModel.ADM.ApplicationDataModel model, WorkRecordElement workRecord, List<IError> errors)
        {
            // The operation id and file name were reserved before spatial export. An operation
            // with no written rows keeps the id and does not name a parquet file.
            string operationType = Enum.GetName(typeof(AgGateway.ADAPT.ApplicationDataModel.Common.OperationTypeEnum), operationDefinition.OperationType);
            string referenceId = operationDefinition.OperationReferenceId;
            string outputFileName = operationDefinition.SpatialRecordsFile;
            if (referenceId == null)
            {
                outputFileName = CreateOperationFileName(operationDefinition, model, out referenceId);
            }

            var id = new Id() { ReferenceId = referenceId };
            var variables = operationDefinition.VariablesByOutputName.Values.ToList();
            string joinedName = string.Join("_", operationDefinition.SourceOperations.Select(x => x.OperationData.Description));
            string name = string.IsNullOrEmpty(joinedName) ? workRecord.Name + "_" + operationType : joinedName;
            if (name.All(x => x == '_'))
            {
                name = id.ReferenceId;
            }
            List<ContextItemElement> contextItems = new List<ContextItemElement>();
            foreach (var srcOp in operationDefinition.SourceOperations)
            {
                var items = _commonExporters.ExportContextItems(srcOp.OperationData.ContextItems);
                if (items != null)
                {
                    contextItems.AddRange(items);
                }
            }
            List<PartyRoleElement> partyRoles = new List<PartyRoleElement>();  
            List<GuidanceAllocationElement> guidanceAllocations = new List<GuidanceAllocationElement>();
            foreach (var loggedData in operationDefinition.SourceLoggedDatas)
            {
                var guidance = _commonExporters.ExportGuidanceAllocations(loggedData.GuidanceAllocationIds, model);
                if (guidance != null)
                {
                    guidanceAllocations.AddRange(guidance);
                }
                var exportedRoles = _commonExporters.ExportPersonRoles(model.Catalog.PersonRoles.Where(x => loggedData.PersonRoleIds.Contains(x.Id.ReferenceId)).ToList());
                if (exportedRoles != null)
                {
                    partyRoles.AddRange(exportedRoles);
                }
            }

            Standard.OperationElement outputOperation = new OperationElement()
            {
                Id = id,
                OperationTypeCode = _commonExporters.ExportOperationType(operationDefinition.OperationType),
                ContextItems = contextItems.Any() ? contextItems : null,
                Name = name,
                Variables = variables.Any() ? variables : null,
                ProductIds = operationDefinition.ProductIds.Any() ? operationDefinition.ProductIds.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList() : null,
                SpatialRecordsFile = operationDefinition.ColumnData.RowCount > 0 ? outputFileName : null,
                GuidanceAllocations = guidanceAllocations.Any() ? guidanceAllocations : null,
                PartyRoles = partyRoles.Any() ? partyRoles : null,
                SummaryValues = ExportSummaryValues(operationDefinition, variables, errors)
            };



            ExportLoad(operationDefinition.SourceOperations.Select(x => x.OperationData), model.Documents.Loads, outputOperation);

            if (operationDefinition.ColumnData.RowCount > 0)
            {
                if (outputOperation.TimeScopes == null)
                {
                    outputOperation.TimeScopes = new();
                }
                outputOperation.TimeScopes.Add(new TimeScopeElement
                {
                    DateContextCode = "ACTUAL",
                    Start = operationDefinition.ColumnData.MinTimestamp?.ToString("O", CultureInfo.InvariantCulture),
                    End = operationDefinition.ColumnData.MaxTimestamp?.ToString("O", CultureInfo.InvariantCulture)
                });
            }
            return outputOperation;
        }

        private void ReserveOperationOutputs(IReadOnlyList<OperationDefinition> groupedOperations, ApplicationDataModel.ADM.ApplicationDataModel model)
        {
            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_exportPath))
            {
                foreach (string existingPath in Directory.EnumerateFiles(_exportPath))
                {
                    reservedNames.Add(Path.GetFileName(existingPath));
                }
            }

            foreach (OperationDefinition operationDefinition in groupedOperations)
            {
                string allocatedFileName = CreateOperationFileName(operationDefinition, model, out string referenceId);
                string outputFileName = allocatedFileName;
                string stem = Path.GetFileNameWithoutExtension(allocatedFileName);
                string extension = Path.GetExtension(allocatedFileName);
                int duplicateIndex = 1;
                while (reservedNames.Contains(outputFileName))
                {
                    outputFileName = string.Concat(stem, "_", duplicateIndex.ToString(CultureInfo.InvariantCulture), extension);
                    duplicateIndex++;
                }

                reservedNames.Add(outputFileName);
                operationDefinition.OperationReferenceId = referenceId;
                operationDefinition.SpatialRecordsFile = outputFileName;
            }
        }

        private string CreateOperationFileName(OperationDefinition operationDefinition, ApplicationDataModel.ADM.ApplicationDataModel model, out string referenceId)
        {
            string operationType = Enum.GetName(typeof(AgGateway.ADAPT.ApplicationDataModel.Common.OperationTypeEnum), operationDefinition.OperationType);
            string products = "";
            if (operationDefinition.ProductIds.Any())
            {
                products = "_" + operationDefinition.ProductIds
                            .Select(r =>
                                model.Catalog.Products.First(p =>
                                    p.Id.ReferenceId == r).Description)
                            .Aggregate((i, j) => i + "_" + j);
            }
            string candidateName = string.Concat(operationType, products);
            referenceId = _idAllocator.AllocateOperationId(candidateName);
            return SanitizeFileName(string.Concat(referenceId, ".parquet"));
        }

        private void AppendSummaryValue(StampedMeteredValues srcValues, List<VariableElement> variables, List<SummaryValueElement> output, List<IError> errors, int? productId = null)
        {
            foreach (var summaryItem in srcValues.Values.Select(x => x.Value).OfType<NumericRepresentationValue>())
            {
                var variable = GetOrCreateVariableElement(variables, summaryItem.Representation.Code, productId);
                if (variable != null)
                {
                    var targetUoM = _commonExporters.StandardDataTypes.Definitions.First(x => x.DefinitionCode == variable.DefinitionCode).NumericDataTypeDefinitionAttributes.UnitOfMeasureCode;
                    var value = summaryItem.AsConvertedDouble(targetUoM);
                    if (value.HasValue)
                    {
                        SummaryValueElement summaryValueElement = new SummaryValueElement()
                        {
                            ValueText = value.Value.ToString(),
                            VariableId = variable.Id.ReferenceId
                        };
                        output.Add(summaryValueElement);
                    }
                    else
                    {
                        errors.Add(new Error
                        {
                            Id = summaryItem.Representation.Code,
                            Description = "Failed to convert summary data; was the product represented in spatial data?  Summary only operations not yet supported.",
                        });
                    }
                }
                else
                {
                    errors.Add(new Error
                    {
                        Id = summaryItem.Representation.Code,
                        Description = "Failed to convert summary data; could not map type.",
                    });
                }
            }
        }

        private List<SummaryValueElement> ExportSummaryValues(OperationDefinition operationDefinition, List<VariableElement> variables, List<IError> errors)
        {
            List<SummaryValueElement> output = new List<SummaryValueElement>();
            foreach (var summaryKvp in operationDefinition.SourceSummaryValuesByProductId)
            {
                foreach (var srcValue in summaryKvp.Value)
                {
                    AppendSummaryValue(srcValue, variables, output, errors, summaryKvp.Key);
                }
            }
            foreach (var generalSummary in operationDefinition.SourceSummaryValuesWithoutProduct)
            {
                AppendSummaryValue(generalSummary, variables, output, errors);
            }

            return output.Any() ? output : null;
        }

        private VariableElement GetOrCreateVariableElement(List<VariableElement> variables, string srcVariableName, int? productId = null)
        {
            if (variables == null)
            {
                return null;
            }
            var variableElement = variables.FirstOrDefault(x => x.Name == srcVariableName && x.ProductId == productId?.ToString());
            if (variableElement == null)
            {
                if (!_commonExporters.TypeMappings.Any(m => m.Source == srcVariableName))
                {
                    //Possible enhancement - custom data type definitions.
                    return null;
                }

                variableElement = new VariableElement
                {
                    DefinitionCode = _commonExporters.TypeMappings.First(m => m.Source == srcVariableName).Target,
                    Name = srcVariableName,
                    Id = new Id { ReferenceId = string.Format(CultureInfo.InvariantCulture, "total-{0}", _idAllocator.AllocateVariableId()) },
                    ProductId = productId?.ToString()
                };
                variables.Add(variableElement);
            }
            return variableElement;
        }

        private void ExportLoad(IEnumerable<OperationData> srcOperations, IEnumerable<Load> srcloads, OperationElement operationElement)
        {
            var loadId = srcOperations.Select(x => x.LoadId).FirstOrDefault(x => x.HasValue);
            var srcLoad = loadId.HasValue
                ? srcloads.FirstOrDefault(x => x.Id.ReferenceId == loadId)
                : null;
            if (srcLoad?.LoadQuantity != null)
            {
                var variableElement = GetOrCreateVariableElement(operationElement.Variables, srcLoad.LoadQuantity.Representation.Code)
                    ?? operationElement.Variables.FirstOrDefault(x => x.Name.EqualsIgnoreCase(srcLoad.LoadNumber));
                if (variableElement == null)
                {
                    variableElement = new VariableElement
                    {
                        Name = srcLoad.LoadNumber,
                        Id = new Id { ReferenceId = string.Format(CultureInfo.InvariantCulture, "total-{0}", _idAllocator.AllocateVariableId()) }
                    };
                    operationElement.Variables.Add(variableElement);
                }

                var loadQuantity = string.IsNullOrEmpty(variableElement.DefinitionCode)
                    ? srcLoad.LoadQuantity.Value.Value
                    : srcLoad.LoadQuantity.AsConvertedDouble(_commonExporters.StandardDataTypes.Definitions
                                                                    .First(x => x.DefinitionCode == variableElement.DefinitionCode)
                                                                    .NumericDataTypeDefinitionAttributes.UnitOfMeasureCode);

                var summary = new SummaryValueElement
                {
                    VariableId = variableElement.Id.ReferenceId,
                    ValueText = loadQuantity?.ToString(CultureInfo.InvariantCulture)
                };
                if (operationElement.SummaryValues == null)
                {
                    operationElement.SummaryValues = new List<SummaryValueElement>();
                }
                operationElement.SummaryValues.Add(summary);
            }

            operationElement.HarvestLoadIdentifier = srcLoad?.LoadNumber;
        }

        private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

        private static string SanitizeFileName(string outputFileName)
        {
            char[] sanitized = new char[outputFileName.Length];
            int length = 0;
            foreach (char character in outputFileName)
            {
                if (Array.IndexOf(InvalidFileNameCharacters, character) < 0)
                {
                    sanitized[length++] = character;
                }
            }

            if (length > 255)
            {
                length = 255;
            }

            return new string(sanitized, 0, length);
        }

        private void ExportOperationSpatialRecords(ADAPTParquetWriter writer, Implement implement, OperationData operationData)
        {
            SectionSpatialWriter[] sectionWriters = SectionSpatialWriter.CreateWriters(implement, writer.ColumnData);
            if (sectionWriters.Length == 0)
            {
                return;
            }

            var sectionValues = new double?[sectionWriters.Length][];
            for (int sectionIndex = 0; sectionIndex < sectionWriters.Length; sectionIndex++)
            {
                sectionValues[sectionIndex] = new double?[sectionWriters[sectionIndex].ColumnWriters.Length];
            }

            SpatialRecord priorSpatialRecord = null;
            foreach (var record in operationData.GetSpatialRecords())
            {
                double timeDelta = 0d;
                if (priorSpatialRecord != null)
                {
                    timeDelta = (record.Timestamp - priorSpatialRecord.Timestamp).TotalSeconds;
                }

                if (!CoverageRecordContext.TryCreate(record, priorSpatialRecord, out CoverageRecordContext context))
                {
                    for (int sectionIndex = 0; sectionIndex < sectionWriters.Length; sectionIndex++)
                    {
                        sectionWriters[sectionIndex].Section.ClearLeadingEdge();
                    }

                    continue;
                }

                for (int sectionIndex = 0; sectionIndex < sectionWriters.Length; sectionIndex++)
                {
                    var sectionWriter = sectionWriters[sectionIndex];
                    var section = sectionWriter.Section;
                    double? previousHeading = section.TryGetLeadingHeading(out double leadingHeading) ? leadingHeading : null;
                    double bearing = context.ResolveBearing(record, sectionWriter.HeadingDefinition, previousHeading);
                    double? reportedDistance = context.ResolveReportedDistance(record, sectionWriter.DistanceDefinition);
                    double? calculatedDistance = context.ResolveCalculatedDistance(reportedDistance);
                    if (!section.IsEngaged(record) || timeDelta >= 5d)
                    {
                        section.ClearLeadingEdge();
                        continue;
                    }

                    if (section.TryGetCoverageWkb(context, bearing, reportedDistance, calculatedDistance, out byte[] wkb, out CoverageBounds bounds))
                    {
                        var values = sectionValues[sectionIndex];
                        for (int columnIndex = 0; columnIndex < sectionWriter.ColumnWriters.Length; columnIndex++)
                        {
                            values[columnIndex] = sectionWriter.ColumnWriters[columnIndex].GetValue(record);
                        }

                        writer.AddRow(record.Timestamp, values, wkb, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
                    }
                }

                priorSpatialRecord = record;
            }
        }
    }

    internal class OperationDefinition
    {
        internal OperationDefinition(Implement implement, OperationData operationData, LoggedData loggedData)
        {
            ImplementKey = implement.GetImplementDefinitionKey();
            SourceOperations = new List<ConstituentSpatialOperation>() { new ConstituentSpatialOperation(operationData, implement) };
            LoadId = operationData.LoadId ?? 0;
            PrescriptionId = operationData.PrescriptionId ?? 0;
            WorkItemOperationId = operationData.WorkItemOperationId ?? 0;
            ProductIds = operationData.ProductIds.ToList();
            OperationType = operationData.OperationType;
            VariablesByOutputName = new Dictionary<string, VariableElement>();
            ColumnData = new ADAPTParquetColumnData();
            SourceLoggedDatas = new HashSet<LoggedData>() { loggedData };
            FieldId = loggedData.FieldId?.ToString(CultureInfo.InvariantCulture) ?? CatalogExporter.UnknownFieldId;
            SourceSummaryValuesByProductId = new Dictionary<int, List<StampedMeteredValues>>();
            SourceSummaryValuesWithoutProduct = new List<StampedMeteredValues>();
        }

        internal string ImplementKey { get; private set; }
        internal List<ConstituentSpatialOperation> SourceOperations { get; set; }
        internal Dictionary<int, List<StampedMeteredValues>> SourceSummaryValuesByProductId { get; set; }
        internal List<StampedMeteredValues> SourceSummaryValuesWithoutProduct { get; set; }
        internal HashSet<LoggedData> SourceLoggedDatas { get; set; }
        internal int LoadId { get; set; }
        internal string FieldId { get; set; }
        internal int PrescriptionId { get; set; }
        internal int WorkItemOperationId { get; set; }
        internal List<int> ProductIds { get; set; }
        internal OperationTypeEnum OperationType { get; set; }
        internal Dictionary<string, VariableElement> VariablesByOutputName { get; set; }
        internal ADAPTParquetColumnData ColumnData { get; set; }
        internal string OperationReferenceId { get; set; }
        internal string SpatialRecordsFile { get; set; }
        internal string Key()
        {
            return string.Join(";", SourceOperations.Select(x => x.OperationData.Id.ReferenceId.ToString()));
        }
        internal string ProductKey()
        {
            return string.Join(";", ProductIds.OrderBy(x => x));
        }

        internal bool IsMatchingOperation(OperationDefinition other)
        {
            if (LoadId != other.LoadId ||
                PrescriptionId != other.PrescriptionId ||
                WorkItemOperationId != other.WorkItemOperationId ||
                OperationType != other.OperationType ||
                ProductKey() != other.ProductKey() ||
                other.ImplementKey != ImplementKey)
            {
                return false;
            }
            else
            {
                return SourceOperations.Any(x => DatesAreSimilar(x.Date, other.SourceOperations.First().Date)); //We expect "other" to have only a single source operation
            }
        }

        internal bool IsMatchingSummary(Summary summary)
        {
            return SourceLoggedDatas.Select(x => x.SummaryId == summary.Id.ReferenceId).Any() ||
                    SourceLoggedDatas.Select(x => x.Id.ReferenceId).Any(y => summary.LoggedDataIds.Contains(y));
        }

        internal bool IsMatchingOperationSummary(OperationSummary operationSummary)
        {
            return operationSummary.OperationType == OperationType &&
                   operationSummary.ProductId != 0 &&
                   ProductIds.Contains(operationSummary.ProductId);
        }

        private bool DatesAreSimilar(DateTime first, DateTime second)
        {
            return Math.Abs((first - second).TotalHours) < WorkRecordExporter.ContemporaneousHours;
        }
    }

    internal class ConstituentSpatialOperation 
    {
        public ConstituentSpatialOperation(OperationData operationData, Implement implement)
        {
            Implement = implement;
            OperationData = operationData;
            Date = operationData.GetSpatialRecords().First().Timestamp;
        }
        internal Implement Implement { get; set; }
        internal OperationData OperationData { get; set; }
        internal DateTime Date { get; set; }
    }
}