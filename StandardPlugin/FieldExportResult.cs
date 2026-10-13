using System.Collections.Generic;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.Standard;

namespace AgGateway.ADAPT.StandardPlugin
{
    // One field's export, kept off the shared document lists until that field's operations have finished.
    // LoggedDataToRelease is invoked before the next field starts, so a plugin's spatial reader
    // is not asked for two fields at once.
    internal sealed class FieldExportResult
    {
        internal int FieldIndex { get; set; }
        internal WorkRecordElement WorkRecord { get; set; }
        internal List<IError> Errors { get; set; } = new List<IError>();
        internal List<LoggedData> LoggedDataToRelease { get; set; } = new List<LoggedData>();
    }
}
