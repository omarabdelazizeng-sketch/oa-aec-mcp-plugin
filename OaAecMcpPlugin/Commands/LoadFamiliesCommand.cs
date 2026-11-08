using Autodesk.Revit.DB;
using OaAecMcpPlugin.Services;
using Serilog;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Loads RFA families into the active project in one transaction, without saving or deleting files.
///     Params: { "families": [{ "path": "C:\\Families\\Door.rfa", "overwrite": false }] }.
///     An existing family (matched by the RFA filename without its extension) is skipped unless overwrite
///     is true. Execute is called on Revit's main thread by CommandExternalEventHandler.
/// </summary>
public class LoadFamiliesCommand : ICommand
{
    /// <summary>The JSON-RPC command name.</summary>
    public string Name => "load_families";

    private sealed record FamilySpec(string Path, bool Overwrite);

    /// <summary>Validates paths and loads the entire batch atomically into the open project.</summary>
    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null) throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument) throw new ArgumentException("load_families needs a project document");
        SpecReader.Obj(parameters);
        var specs = SpecReader.Items(parameters, "families", required: true).Select(item =>
        {
            SpecReader.Obj(item, "family");
            var path = SpecReader.Str(item, "path");
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException($"family path '{path}' must be absolute");
            if (!string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"family path '{path}' must end with .rfa");
            if (!File.Exists(path)) throw new ArgumentException($"family path '{path}' does not exist");
            return new FamilySpec(path, SpecReader.Bool(item, "overwrite", false));
        }).ToList();
        if (specs.Count == 0) throw new ArgumentException("'families' is empty");

        var results = new List<object>();
        RevitTx.Run(doc, "OA-AEC-MCP: load_families", () =>
        {
            foreach (var spec in specs)
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(spec.Path);
                    var existing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                        .Where(f => f.Name == name).ToList();
                    if (existing.Count > 1)
                        throw new ArgumentException($"several loaded families are named '{name}'; resolve the ambiguity first");
                    Family family;
                    string action;
                    if (existing.Count == 1 && !spec.Overwrite)
                    {
                        family = existing[0];
                        action = "already_loaded";
                    }
                    else
                    {
                        if (!doc.LoadFamily(spec.Path, new FamilyLoadOptions(spec.Overwrite), out var loaded) || loaded == null)
                            throw new InvalidOperationException("Revit did not confirm loading the family (it may already be identical); batch rolled back");
                        family = loaded;
                        action = "loaded";
                    }
                    doc.Regenerate();
                    var types = family.GetFamilySymbolIds().Select(id => ((FamilySymbol)doc.GetElement(id)).Name)
                        .OrderBy(n => n, StringComparer.Ordinal).ToList();
                    results.Add(new { path = spec.Path, family = family.Name, action, types });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Family load failed for {Path}", spec.Path);
                    var message = $"family path '{spec.Path}': {ex.Message}";
                    if (ex is ArgumentException) throw new ArgumentException(message, ex);
                    throw new InvalidOperationException(message, ex);
                }
            }
        });
        return new { document = doc.Title, transaction = "committed", families = results };
    }

    private sealed class FamilyLoadOptions : IFamilyLoadOptions
    {
        private readonly bool _overwrite;

        /// <summary>Controls replacement of loaded families and their type parameter values.</summary>
        public FamilyLoadOptions(bool overwrite) => _overwrite = overwrite;

        /// <summary>Rejects replacing an existing family unless the request explicitly allows it.</summary>
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = _overwrite;
            return _overwrite;
        }

        /// <summary>Keeps shared families from the project unless overwrite was explicitly requested.</summary>
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = _overwrite ? FamilySource.Family : FamilySource.Project;
            overwriteParameterValues = _overwrite;
            return true;
        }
    }
}
