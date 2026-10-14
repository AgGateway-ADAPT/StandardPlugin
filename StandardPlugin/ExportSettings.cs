using System;
using AgGateway.ADAPT.ApplicationDataModel.ADM;

namespace AgGateway.ADAPT.StandardPlugin
{
    // Plugin properties are strings on the Properties object passed to Export.
    // Multithread defaults to false: omitted or unparsable stays serial, which is what
    // plugins with a non-thread-safe spatial reader require. ParallelThreadCount is read
    // only when Multithread is true; omitted means the processor count. CompactJson
    // defaults to indented adapt.json.
    internal static class ExportSettings
    {
        internal const string ParallelThreadCountProperty = "ParallelThreadCount";
        internal const string MultithreadProperty = "Multithread";
        internal const string CompactJsonProperty = "CompactJson";

        internal static int GetMaxParallelism(Properties properties)
        {
            if (!UseMultithread(properties))
            {
                return 1;
            }

            if (properties != null)
            {
                var parallelismString = properties.GetProperty(ParallelThreadCountProperty);
                if (parallelismString != null && int.TryParse(parallelismString, out int configuredParallelism))
                {
                    return configuredParallelism <= 0 ? 1 : configuredParallelism;
                }
            }

            return Environment.ProcessorCount;
        }

        internal static bool UseMultithread(Properties properties)
        {
            if (properties == null)
            {
                return false;
            }

            var multithread = properties.GetProperty(MultithreadProperty);
            return bool.TryParse(multithread, out bool useMultithread) && useMultithread;
        }

        internal static bool UseCompactJson(Properties properties)
        {
            if (properties == null)
            {
                return false;
            }

            var compactJson = properties.GetProperty(CompactJsonProperty);
            return bool.TryParse(compactJson, out bool useCompactJson) && useCompactJson;
        }
    }
}
