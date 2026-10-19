using Autodesk.Revit.DB;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     READ-ONLY. Returns the contents of a Revit schedule exactly as the schedule view
///     displays them (after Revit's own filters, sorting, grouping and unit formatting).
///     No transaction is opened; nothing in the model is changed.
///
///     Parameters (all optional):
///     - schedule_name (string): schedule view name (case-insensitive exact match).
///       When omitted, the active view is used and must be a schedule.
///     - filter_column (string): visible column heading to filter on (case-insensitive).
///     - filter_contains (string): keep only body rows whose filter_column cell contains
///       this text (case-insensitive). Both filter params must be given together.
///     - max_rows (int): cap on returned rows (default 5000, clamped to [1, 20000]).
///
///     Returns: schedule_name, is_itemized, columns (visible field headings, in display
///     order), total_body_rows, returned_rows, truncated, rows (string[][]; empty rows
///     are skipped; when no filter is given, heading rows inside the body are included).
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on
///     Revit's main thread — all Revit API calls here are therefore safe.
/// </summary>
public class GetScheduleDataCommand : ICommand
{
    public string Name => "get_schedule_data";

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null)
            throw new InvalidOperationException("No active Revit document");

        string? scheduleName = null;
        string? filterColumn = null;
        string? filterContains = null;
        var maxRows = 5000;

        if (parameters.ValueKind == JsonValueKind.Object)
        {
            if (parameters.TryGetProperty("schedule_name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
                scheduleName = nameProp.GetString();

            if (parameters.TryGetProperty("filter_column", out var colProp) &&
                colProp.ValueKind == JsonValueKind.String)
                filterColumn = colProp.GetString();

            if (parameters.TryGetProperty("filter_contains", out var containsProp) &&
                containsProp.ValueKind == JsonValueKind.String)
                filterContains = containsProp.GetString();

            if (parameters.TryGetProperty("max_rows", out var maxProp) &&
                maxProp.ValueKind == JsonValueKind.Number &&
                maxProp.TryGetInt32(out var maxVal))
                maxRows = Math.Max(1, Math.Min(maxVal, 20000)); // Math.Clamp is unavailable on net48
        }

        if (string.IsNullOrEmpty(filterColumn) != string.IsNullOrEmpty(filterContains))
            throw new ArgumentException("filter_column and filter_contains must be provided together");

        var schedule = ResolveSchedule(doc, scheduleName);
        var definition = schedule.Definition;

        // Visible fields map 1:1, in order, to the body section's columns.
        var columns = new List<string>();
        for (var i = 0; i < definition.GetFieldCount(); i++)
        {
            var field = definition.GetField(i);
            if (!field.IsHidden)
                columns.Add(field.ColumnHeading);
        }

        var filterIndex = -1;
        if (!string.IsNullOrEmpty(filterColumn))
        {
            filterIndex = columns.FindIndex(c => string.Equals(c, filterColumn, StringComparison.OrdinalIgnoreCase));
            if (filterIndex < 0)
                throw new ArgumentException(
                    $"Column '{filterColumn}' not found in schedule '{schedule.Name}'. Visible columns: {string.Join(", ", columns)}");
        }

        var body = schedule.GetTableData().GetSectionData(SectionType.Body);
        var firstCol = body.FirstColumnNumber;
        var lastCol = body.LastColumnNumber;

        var rows = new List<string[]>();
        var totalBodyRows = 0;
        var matchedRows = 0;

        for (var r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
        {
            totalBodyRows++;
            var cells = new string[lastCol - firstCol + 1];
            var isEmpty = true;
            for (var c = firstCol; c <= lastCol; c++)
            {
                var text = schedule.GetCellText(SectionType.Body, r, c) ?? "";
                cells[c - firstCol] = text;
                if (text.Length > 0) isEmpty = false;
            }

            if (isEmpty) continue;

            if (filterIndex >= 0 &&
                (filterIndex >= cells.Length ||
                 cells[filterIndex].IndexOf(filterContains!, StringComparison.OrdinalIgnoreCase) < 0))
                continue;

            matchedRows++;
            if (rows.Count < maxRows)
                rows.Add(cells);
        }

        return new
        {
            schedule_name   = schedule.Name,
            is_itemized     = definition.IsItemized,
            columns,
            total_body_rows = totalBodyRows,
            matched_rows    = matchedRows,
            returned_rows   = rows.Count,
            truncated       = matchedRows > rows.Count,
            rows
        };
    }

    private static ViewSchedule ResolveSchedule(Document doc, string? scheduleName)
    {
        if (string.IsNullOrWhiteSpace(scheduleName))
        {
            if (doc.ActiveView is ViewSchedule active && !active.IsTitleblockRevisionSchedule)
                return active;

            throw new ArgumentException(
                $"Active view '{doc.ActiveView?.Name}' is not a schedule. Open the schedule in Revit or pass schedule_name. " +
                $"Available schedules: {AvailableScheduleNames(doc)}");
        }

        var match = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .FirstOrDefault(s => !s.IsTemplate &&
                                 !s.IsTitleblockRevisionSchedule &&
                                 string.Equals(s.Name, scheduleName, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new ArgumentException(
            $"Schedule '{scheduleName}' not found. Available schedules: {AvailableScheduleNames(doc)}");
    }

    private static string AvailableScheduleNames(Document doc)
    {
        var names = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule)
            .Select(s => s.Name)
            .OrderBy(n => n)
            .ToList();

        const int limit = 40;
        var shown = string.Join("; ", names.Take(limit));
        return names.Count > limit ? $"{shown}; … (+{names.Count - limit} more)" : shown;
    }
}
