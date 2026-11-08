using Autodesk.Revit.DB;
using Nice3point.Revit.Extensions;
using System.Text.Json;

namespace OaAecMcpPlugin.Commands;

/// <summary>
///     Read-only. Returns every level and grid in the active model, plus the Project Base Point,
///     Survey Point and true-north angle — all lengths in millimetres.
///
///     It reports raw facts only and does NO comparison: checking the model against a spec is done
///     by the caller, so the checker stays independent of <see cref="ApplyDatumsCommand"/>.
///
///     Level fields:
///       internal_elevation_mm — Level.ProjectElevation (relative to the internal origin)
///       elevation_mm          — Level.Elevation (relative to the level type's elevation base, normally the PBP)
///
///     Threading: Execute is called exclusively from
///     <see cref="OaAecMcpPlugin.Dispatch.CommandExternalEventHandler.Execute"/> on Revit's main thread.
/// </summary>
public class GetDatumsCommand : ICommand
{
    public string Name => "get_datums";

    private static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    private static double[] Mm(XYZ p) => new[] { Mm(p.X), Mm(p.Y), Mm(p.Z) };

    public object Execute(Document? doc, JsonElement parameters)
    {
        if (doc == null)
            throw new InvalidOperationException("No active Revit document");
        if (doc.IsFamilyDocument)
            throw new ArgumentException("get_datums needs a project document, not a family document");

        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.ProjectElevation)
            .Select(l => new
            {
                name = l.Name,
                id = l.Id.ToLong(),
                internal_elevation_mm = Mm(l.ProjectElevation),
                elevation_mm = Mm(l.Elevation),
            })
            .ToList();

        var grids = new FilteredElementCollector(doc)
            .OfClass(typeof(Grid))
            .Cast<Grid>()
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .Select(g =>
            {
                var curve = g.Curve;
                return new
                {
                    name = g.Name,
                    id = g.Id.ToLong(),
                    is_curved = g.IsCurved,
                    start_mm = Mm(curve.GetEndPoint(0)),
                    end_mm = Mm(curve.GetEndPoint(1)),
                };
            })
            .ToList();

        var pbp = BasePoint.GetProjectBasePoint(doc);
        var survey = BasePoint.GetSurveyPoint(doc);
        var originShared = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);

        return new
        {
            document = doc.Title,
            path = doc.PathName,
            units = "mm",
            levels,
            grids,
            project_base_point = new { internal_mm = Mm(pbp.Position), shared_mm = Mm(pbp.SharedPosition) },
            survey_point = new { internal_mm = Mm(survey.Position), shared_mm = Mm(survey.SharedPosition) },
            internal_origin_shared = new
            {
                east_west_mm = Mm(originShared.EastWest),
                north_south_mm = Mm(originShared.NorthSouth),
                elevation_mm = Mm(originShared.Elevation),
                angle_to_true_north_deg = originShared.Angle * 180.0 / Math.PI,
            },
            level_count = levels.Count,
            grid_count = grids.Count,
        };
    }
}
