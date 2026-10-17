using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace PipingSuite.Core;

/// <summary>
/// Keeps warnings from popping up one dialog per batch step, and records why Revit rolls a step back
/// so the report can say so instead of failing silently.
/// </summary>
internal sealed class FailureCollector : IFailuresPreprocessor
{
    public List<string> Errors { get; } = [];

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        foreach (var f in accessor.GetFailureMessages())
        {
            if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
            else Errors.Add(f.GetDescriptionText());
        }
        return Errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
