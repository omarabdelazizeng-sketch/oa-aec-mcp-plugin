using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using Serilog;

namespace OaAecMcpPlugin.Services;

/// <summary>
///     Runs a modeling change in ONE transaction for the batch commands.
///     Revit warnings are captured and returned instead of showing a modal dialog (a dialog would
///     block the main thread and time the request out). Any error-level failure rolls everything back.
///
///     Must be called from a command's Execute, i.e. on Revit's main thread inside the ExternalEvent handler.
/// </summary>
public static class RevitTx
{
    public static List<string> Run(Document doc, string name, Action body)
    {
        var failures = new CollectingFailures(name);
        using var tx = new Transaction(doc, name);
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(failures);
        options.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(options);

        tx.Start();
        try
        {
            body();
            var status = tx.Commit();
            if (status != TransactionStatus.Committed)
                throw new InvalidOperationException(
                    $"{name}: transaction ended with status {status}; Revit errors: {string.Join(" | ", failures.Errors)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Transaction} failed; rolling back", name);
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            throw;
        }

        Log.Information("{Transaction} committed with {Warnings} Revit warnings", name, failures.Warnings.Count);
        return failures.Warnings;
    }

    private sealed class CollectingFailures : IFailuresPreprocessor
    {
        private readonly string _name;
        public CollectingFailures(string name) => _name = name;
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var hasError = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                var text = f.GetDescriptionText();
                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    Warnings.Add(text);
                    accessor.DeleteWarning(f);
                }
                else
                {
                    Errors.Add(text);
                    hasError = true;
                }
            }
            if (hasError) Log.Error("{Transaction}: Revit errors, rolling back: {Errors}", _name, string.Join(" | ", Errors));
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}

/// <summary>Name-based lookups of levels and types, built fresh per request (never cached across requests).</summary>
public sealed class ModelLookup
{
    private readonly Document _doc;
    private readonly Dictionary<string, Level> _levels;

    public ModelLookup(Document doc)
    {
        _doc = doc;
        _levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .ToDictionary(l => l.Name, StringComparer.Ordinal);
    }

    public Level GetLevel(string name) =>
        _levels.TryGetValue(name, out var level) ? level : throw new ArgumentException($"level '{name}' not found");

    public FamilySymbol GetSymbol(BuiltInCategory category, string family, string type)
    {
        var symbols = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).OfCategory(category)
            .Cast<FamilySymbol>().Where(s => s.FamilyName == family).ToList();
        if (symbols.Count == 0)
            throw new ArgumentException($"family '{family}' is not loaded in category {category}");
        return symbols.FirstOrDefault(s => s.Name == type)
               ?? throw new ArgumentException($"type '{type}' not found in family '{family}' (have: {string.Join(", ", symbols.Select(s => s.Name))})");
    }

    public T GetHostType<T>(string name) where T : HostObjAttributes =>
        new FilteredElementCollector(_doc).OfClass(typeof(T)).Cast<T>().FirstOrDefault(t => t.Name == name)
        ?? throw new ArgumentException($"{typeof(T).Name} '{name}' not found");

    public ElementId GetMaterialId(string? name)
    {
        if (name == null) return ElementId.InvalidElementId;
        var m = new FilteredElementCollector(_doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
        return m?.Id ?? throw new ArgumentException($"material '{name}' not found");
    }

    public static string Mark(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";

    public static void SetMark(Element e, string mark)
    {
        var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                ?? throw new InvalidOperationException($"element {e.Id.ToLong()} has no Mark parameter");
        if (p.AsString() != mark) p.Set(mark);
    }
}
