using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Prescriptions;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using ParquetSharp;

namespace AgGateway.ADAPT.StandardPlugin
{
    // One row group is the only row storage. A full group is written and cleared before another
    // is collected, and the file's bounding box is filled in when the writer closes.
    internal sealed class ADAPTParquetWriter : IDisposable
    {
        internal const int DefaultRowGroupSize = 65536;

        private readonly bool _includeTimestamps;
        private readonly int _rowGroupSize;
        private readonly Func<string> _openFile;
        private readonly string[] _columnNames;
        private readonly List<DateTime> _timestamps;
        private readonly List<double?>[] _values;
        private readonly List<byte[]> _geometries;
        private Dictionary<string, string> _keyValueMetadata;
        private ParquetFileWriter _writer;
        private string _outputPath;
        private bool _completed;
        private bool _disposed;
        private bool _hasBounds;
        private double _minX;
        private double _minY;
        private double _maxX;
        private double _maxY;

        internal ADAPTParquetWriter(ADAPTParquetColumnData columnData, bool includeTimestamps, Func<string> openFile, int rowGroupSize = DefaultRowGroupSize)
        {
            if (columnData == null)
            {
                throw new ArgumentNullException(nameof(columnData));
            }

            if (openFile == null)
            {
                throw new ArgumentNullException(nameof(openFile));
            }

            if (rowGroupSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rowGroupSize));
            }

            ColumnData = columnData;
            _includeTimestamps = includeTimestamps;
            _rowGroupSize = rowGroupSize;
            _openFile = openFile;
            _columnNames = columnData.Columns.Select(column => column.TargetName).ToArray();
            _timestamps = includeTimestamps ? new List<DateTime>(rowGroupSize) : null;
            _values = new List<double?>[_columnNames.Length];
            for (int columnIndex = 0; columnIndex < _columnNames.Length; columnIndex++)
            {
                _values[columnIndex] = new List<double?>(rowGroupSize);
            }

            _geometries = new List<byte[]>(rowGroupSize);
        }

        internal ADAPTParquetColumnData ColumnData { get; }

        internal int BufferedRowCount
        {
            get { return _geometries.Count; }
        }

        internal int WrittenRowCount { get; private set; }

        // Set only after ParquetFileWriter.Dispose() returns. Close() finalizes the file and does not release the native handle.
        internal bool NativeWriterDisposed { get; private set; }

        internal void AddRow(DateTime? timestamp, double?[] values, byte[] geometry, double minX, double minY, double maxX, double maxY)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Rows cannot be added after the parquet file is closed.");
            }

            if (values == null || values.Length != _values.Length)
            {
                throw new ArgumentException("Row values must match the column schema.", nameof(values));
            }

            if (_includeTimestamps)
            {
                if (!timestamp.HasValue)
                {
                    throw new ArgumentException("A timestamp is required for this file.", nameof(timestamp));
                }

                _timestamps.Add(timestamp.Value);
                if (!ColumnData.MinTimestamp.HasValue || timestamp.Value < ColumnData.MinTimestamp.Value)
                {
                    ColumnData.MinTimestamp = timestamp.Value;
                }

                if (!ColumnData.MaxTimestamp.HasValue || timestamp.Value > ColumnData.MaxTimestamp.Value)
                {
                    ColumnData.MaxTimestamp = timestamp.Value;
                }
            }

            for (int columnIndex = 0; columnIndex < _values.Length; columnIndex++)
            {
                _values[columnIndex].Add(values[columnIndex]);
            }

            _geometries.Add(geometry);
            ExpandBounds(minX, minY, maxX, maxY);
            if (_geometries.Count == _rowGroupSize)
            {
                FlushRowGroup();
            }
        }

        internal void Complete()
        {
            if (_disposed || _completed)
            {
                return;
            }

            try
            {
                if (_geometries.Count > 0)
                {
                    FlushRowGroup();
                }

                if (_writer != null)
                {
                    WriteGeoMetadata();
                }

                ReleaseNativeWriter(finalize: true);
                _completed = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_completed)
            {
                return;
            }

            // An exception while rows were being written must not leave a readable partial file.
            // Buffered rows are dropped here. Row groups already flushed are removed with the file.
            try
            {
                ReleaseNativeWriter(finalize: false);
            }
            finally
            {
                if (_outputPath != null && File.Exists(_outputPath))
                {
                    File.Delete(_outputPath);
                }
            }
        }

        private void FlushRowGroup()
        {
            int rowGroupCount = _geometries.Count;
            if (rowGroupCount == 0)
            {
                return;
            }

            EnsureOpen();
            using (RowGroupWriter rowGroup = _writer.AppendRowGroup())
            {
                if (_includeTimestamps)
                {
                    var timestampWriter = rowGroup.NextColumn().LogicalWriter<string>();
                    var timestampBatch = new string[rowGroupCount];
                    for (int rowIndex = 0; rowIndex < rowGroupCount; rowIndex++)
                    {
                        timestampBatch[rowIndex] = _timestamps[rowIndex].ToString("O", CultureInfo.InvariantCulture);
                    }

                    timestampWriter.WriteBatch(timestampBatch);
                }

                for (int columnIndex = 0; columnIndex < _values.Length; columnIndex++)
                {
                    var doubleWriter = rowGroup.NextColumn().LogicalWriter<double?>();
                    var valueBatch = new double?[rowGroupCount];
                    for (int rowIndex = 0; rowIndex < rowGroupCount; rowIndex++)
                    {
                        valueBatch[rowIndex] = _values[columnIndex][rowIndex];
                    }

                    doubleWriter.WriteBatch(valueBatch);
                }

                var geometryWriter = rowGroup.NextColumn().LogicalWriter<byte[]>();
                var geometryBatch = new byte[rowGroupCount][];
                for (int rowIndex = 0; rowIndex < rowGroupCount; rowIndex++)
                {
                    geometryBatch[rowIndex] = _geometries[rowIndex];
                }

                geometryWriter.WriteBatch(geometryBatch);
            }

            WrittenRowCount += rowGroupCount;
            ColumnData.RowCount += rowGroupCount;
            if (_timestamps != null)
            {
                _timestamps.Clear();
            }

            for (int columnIndex = 0; columnIndex < _values.Length; columnIndex++)
            {
                _values[columnIndex].Clear();
            }

            _geometries.Clear();
        }

        private void EnsureOpen()
        {
            if (_writer != null)
            {
                return;
            }

            var dataFields = new List<Column>();
            if (_includeTimestamps)
            {
                dataFields.Add(new Column<string>("timestamp"));
            }

            dataFields.AddRange(_columnNames.Select(name => new Column<double?>(name)));
            dataFields.Add(new Column<byte[]>("geometry"));
            _keyValueMetadata = new Dictionary<string, string>();
            _outputPath = _openFile();
            _writer = new ParquetFileWriter(_outputPath, dataFields.ToArray(), Compression.Zstd, keyValueMetadata: _keyValueMetadata);
        }

        private void WriteGeoMetadata()
        {
            var geoColumn = new GeoParquet.GeoColumn();
            geoColumn.Encoding = "WKB";
            geoColumn.Geometry_types.Add("Polygon");
            geoColumn.Bbox = new[] { _minX, _minY, _maxX, _maxY };
            foreach (var entry in GeoParquet.GeoMetadata.GetGeoMetadata(geoColumn))
            {
                _keyValueMetadata[entry.Key] = entry.Value;
            }
        }

        private void ReleaseNativeWriter(bool finalize)
        {
            if (_writer == null)
            {
                return;
            }

            var writer = _writer;
            _writer = null;
            try
            {
                if (finalize)
                {
                    writer.Close();
                }
            }
            finally
            {
                writer.Dispose();
                NativeWriterDisposed = true;
            }
        }

        private void ExpandBounds(double minX, double minY, double maxX, double maxY)
        {
            if (!_hasBounds)
            {
                _minX = minX;
                _minY = minY;
                _maxX = maxX;
                _maxY = maxY;
                _hasBounds = true;
                return;
            }

            if (minX < _minX)
            {
                _minX = minX;
            }

            if (minY < _minY)
            {
                _minY = minY;
            }

            if (maxX > _maxX)
            {
                _maxX = maxX;
            }

            if (maxY > _maxY)
            {
                _maxY = maxY;
            }
        }
    }

    internal class ADAPTParquetColumnData
    {
        public ADAPTParquetColumnData()
        {
            Columns = new List<ADAPTDataColumn>();
        }

        public void AddVectorPrescription(VectorPrescription rx, List<WorkOrderExportColumn> exportColumns, Catalog catalog, CommonExporters commonExporters)
        {
            foreach (var exportColumn in exportColumns)
            {
                string targetName = exportColumn.Variable.DefinitionCode;
                string targetUOMCode = commonExporters.StandardDataTypes.Definitions.First(x => x.DefinitionCode == targetName).NumericDataTypeDefinitionAttributes.UnitOfMeasureCode;
                string productName = catalog.Products.First(x => x.Id.ReferenceId == Int32.Parse(exportColumn.Variable.ProductId)).Description;

                Columns.Add(new ADAPTDataColumn(exportColumn.ProductLookup, targetName, targetUOMCode, exportColumn.Variable.ProductId, productName));
            }
        }

        public List<IError> AddOperationData(OperationData operationData, Catalog catalog, Implement implement, CommonExporters commonExporters)
        {
            List<IError> errors = new List<IError>();
            bool hasMultipleProducts = implement.Sections.Any(x => x.ProductIndexWorkingData != null);
            foreach (NumericWorkingData nwd in implement.GetDistinctWorkingDatas())
            {
                string targetName = commonExporters.TypeMappings.First(m => m.Source == nwd.Representation.Code).Target;
                string targetUOMCode = commonExporters.StandardDataTypes.Definitions.First(x => x.DefinitionCode == targetName).NumericDataTypeDefinitionAttributes.UnitOfMeasureCode;
                if (!nwd.CanConvertInto(targetUOMCode))
                {
                    errors.Add(new Error
                    {
                        Description = $"Omitting {nwd.Representation.Code} in {nwd.UnitOfMeasure.Code} because it cannot be converted to {targetUOMCode}",
                    });
                    continue;
                }
                if (hasMultipleProducts &&
                    (commonExporters.TypeMappings.FirstOrDefault(m => m.Source == nwd.Representation.Code)?.IsMultiProductCapable ?? false))
                {
                    foreach (var productId in operationData.ProductIds)
                    {
                        string productName = catalog.Products.First(p => p.Id.ReferenceId == productId).Description;
                        if (!Columns.Any(c => c.SrcName == nwd.Representation.Code && c.ProductId == productId.ToString()))
                        {
                            var column = new ADAPTDataColumn(nwd, targetName, targetUOMCode, productId.ToString(), productName);
                            column.InitializeConverter();
                            Columns.Add(column);
                        }
                    }
                }
                else
                {
                    if (!Columns.Any(c => c.SrcName == nwd.Representation.Code))
                    {
                        var column = new ADAPTDataColumn(nwd, targetName, targetUOMCode);
                        column.InitializeConverter();
                        Columns.Add(column);
                    }
                }
            }
            return errors;
        }

        public DateTime? MinTimestamp { get; set; }

        public DateTime? MaxTimestamp { get; set; }

        public List<ADAPTDataColumn> Columns { get; set; }

        public int RowCount { get; set; }

        public int GetDataColumnIndex(ADAPTDataColumn dataColumn)
        {
            return Columns.IndexOf(dataColumn) + 1;  //Index is 1-based
        }
    }

    internal class ADAPTDataColumn
    {
        public ADAPTDataColumn(NumericWorkingData numericWorkingData, string targetName, string targetUOM, string productId = null, string productName = null)
        {
            SrcWorkingData = numericWorkingData;
            SrcName = numericWorkingData.Representation.Code;
            SrcUOMCode = numericWorkingData.UnitOfMeasure.Code;
            TargetCode = targetName;
            TargetName = targetName;
            TargetUOMCode = targetUOM;
            ProductId = productId;
            if (productName != null)
            {
                TargetName = $"{TargetName}_{productName}";
            }
        }

        internal ADAPTDataColumn(string targetName)
        {
            SrcName = targetName;
            TargetCode = targetName;
            TargetName = targetName;
        }

        public ADAPTDataColumn(RxProductLookup lookup, string targetName, string targetUOM, string productId = null, string productName = null)
        {
            SrcProductLookup = lookup;
            SrcName = lookup.Representation.Code;
            SrcUOMCode = lookup.UnitOfMeasure.Code;
            TargetCode = targetName;
            TargetName = targetName;
            TargetUOMCode = targetUOM;
            ProductId = productId;
            if (productName != null)
            {
                TargetName = $"{TargetCode}_{productName}";
            }

            InitializeConverter();
        }

        public string SrcName { get; set; }

        public string SrcUOMCode { get; set; }

        public string ProductId { get; set; }

        public string TargetUOMCode { get; set; }

        public string TargetCode { get; set; }

        public string TargetName { get; set; }

        public NumericWorkingData SrcWorkingData { get; set; }

        public RxProductLookup SrcProductLookup { get; set; }

        internal Extensions.ValueConverter ConvertValue { get; private set; }

        internal void InitializeConverter()
        {
            ConvertValue = Extensions.BuildValueConverter(SrcName, SrcUOMCode, TargetUOMCode);
        }

        internal double? ConvertMeterValue(NumericRepresentationValue value)
        {
            if (value == null)
            {
                return null;
            }

            if (value.Value.UnitOfMeasure == null)
            {
                return value.Value.Value;
            }

            var srcUnitCode = value.Value.UnitOfMeasure.Code;
            // The cached scale was built from the working-data unit. A meter value in that same
            // unit uses it. A value in a different unit is converted from that unit, and that pair is cached too.
            if (srcUnitCode == SrcUOMCode)
            {
                return ConvertValue(value.Value.Value);
            }

            if (!value.Value.UnitOfMeasure.CanConvertInto(TargetUOMCode))
            {
                if (Extensions.TryGetRemappedUnitCodes(SrcName, srcUnitCode, TargetUOMCode, out var remappedSource, out var remappedTarget))
                {
                    srcUnitCode = remappedSource;
                    return value.Value.Value.ConvertValue(srcUnitCode, remappedTarget);
                }

                return null;
            }

            return value.Value.Value.ConvertValue(srcUnitCode, TargetUOMCode);
        }
    }
}
