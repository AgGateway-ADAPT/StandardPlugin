using System.Collections.Generic;
using System.Threading;

namespace AgGateway.ADAPT.StandardPlugin
{
    // Operation ids are reserved in list order before spatial export starts.
    // Variable ids are assigned after spatial export, also in list order.
    // The lock and interlocked counter keep a single exporter safe if a caller still allocates concurrently.
    internal sealed class ExportIdAllocator
    {
        private readonly HashSet<string> _operationIds = new HashSet<string>();
        private int _variableCounter;
        private readonly object _syncRoot = new object();

        internal string AllocateOperationId(string candidateName)
        {
            lock (_syncRoot)
            {
                string uniqueName = candidateName;
                int opNameCounter = 1;
                while (_operationIds.Contains(uniqueName))
                {
                    uniqueName = string.Concat(candidateName, "_", opNameCounter++);
                }

                _operationIds.Add(uniqueName);
                return uniqueName;
            }
        }

        internal string AllocateVariableId()
        {
            return Interlocked.Increment(ref _variableCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
