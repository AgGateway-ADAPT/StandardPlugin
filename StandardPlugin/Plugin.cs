using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.Standard;
using Newtonsoft.Json;

namespace AgGateway.ADAPT.StandardPlugin
{

    public enum SourceGeometryPosition
    {
        GNSSReceiver = 0, 
        ImplementReferencePoint = 1,   
    }

    public enum SourceDeviceDefinition
    {
        DeviceElementHierarchy = 0, 
        Machine0Implement1Section2 = 1, 
    }

    public class Plugin : IPlugin
    {
        private Properties _properties;
        private const string Property_FileName = "FileName";
        public Plugin()
        {
            Errors = new List<IError>();
        }

        public string Name => "ADAPT Standard Plugin";

        public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;

        public string Owner => "AgGateway";

        public IList<IError> Errors { get; set; }

        public void Export(ApplicationDataModel.ADM.ApplicationDataModel dataModel, string exportPath, Properties properties = null)
        {
            var root = new Root
            {
                Catalog = new Standard.Catalog(),
                Documents = new Standard.Documents(),
                RootSchemaVersion = "2.0.0"
            };

            var catalogErrors = CatalogExporter.Export(dataModel, root, properties);
            var workOrderErrors = WorkOrderExporter.Export(dataModel, root, exportPath, properties);
            var workRecordErrors = WorkRecordExporter.Export(dataModel, root, exportPath, properties);

            var errors = Errors as List<IError>;
            errors.AddRange(catalogErrors);
            errors.AddRange(workOrderErrors);
            errors.AddRange(workRecordErrors);

            Directory.CreateDirectory(exportPath);
            var outputFileName = Path.Combine(exportPath, "adapt.json");
            WriteAdaptJson(root, outputFileName, properties);
        }

        private static void WriteAdaptJson(Root root, string outputFileName, Properties properties)
        {
            // CompactJson writes the same object graph with Formatting.None. Omitted stays indented.
            var settings = AgGateway.ADAPT.Standard.Converter.CreateSettings(ExportSettings.UseCompactJson(properties));
            using (var streamWriter = new StreamWriter(outputFileName, false, Encoding.UTF8))
            using (var jsonWriter = new JsonTextWriter(streamWriter))
            {
                jsonWriter.Formatting = settings.Formatting;
                var serializer = JsonSerializer.Create(settings);
                serializer.Serialize(jsonWriter, root);
            }
        }


        public Properties GetProperties(string dataPath)
        {
            if (_properties == null)
            {
                _properties = new Properties();
            }
            return _properties;
        }


        public IList<ApplicationDataModel.ADM.ApplicationDataModel> Import(string dataPath, Properties properties = null)
        {
            throw new NotImplementedException("This plugin only supports exporting from the ADAPT Fraemwork to the ADAPT Standard.");
        }

        public void Initialize(string args = null)
        {
            //Nothing to initialize
        }

        public bool IsDataCardSupported(string dataPath, Properties properties = null)
        {
            throw new NotImplementedException("This plugin only supports exporting from the ADAPT Fraemwork to the ADAPT Standard.");
        }

        public IList<IError> ValidateDataOnCard(string dataPath, Properties properties = null)
        {
            throw new NotImplementedException("This plugin only supports exporting from the ADAPT Fraemwork to the ADAPT Standard.");
        }
    }
}
