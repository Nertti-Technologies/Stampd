using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace Stampd.UI.Components.Admin;

/// <summary>
/// v2.2 #223 — delta-chip rendering helpers shared by the admin analytics widgets
/// (funnel, identity verification, time-to-sign). Components pull these in with
/// <c>@using static</c> so the markup calls read the same as before the split.
/// </summary>
internal static class DeltaChips
{
    // `delta` is a percent (e.g. 12.3 for +12.3%). `lowerIsBetter` flips the color
    // semantics — for time-to-sign and lockout count, a negative delta is GOOD.
    // We return a MarkupString so the caller can drop it inside another element
    // without Blazor re-escaping the arrow glyphs.
    public static MarkupString RenderDeltaPercent(double delta, bool lowerIsBetter)
        => RenderDelta(delta, suffix: "%", lowerIsBetter);

    public static MarkupString RenderDeltaPoints(double delta, bool lowerIsBetter)
        => RenderDelta(delta, suffix: "pp", lowerIsBetter);

    private static MarkupString RenderDelta(double delta, string suffix, bool lowerIsBetter)
    {
        if (delta == 0)
        {
            return new MarkupString("<span style=\"color: var(--stampd-muted); margin-left:0.35rem;\">—</span>");
        }

        var isImprovement = lowerIsBetter ? delta < 0 : delta > 0;
        var color = isImprovement ? "#16a34a" : "#dc2626";
        var arrow = delta > 0 ? "▲" : "▼";
        var formatted = Math.Abs(delta).ToString("0.#", CultureInfo.InvariantCulture);
        return new MarkupString(
            $"<span style=\"color:{color}; margin-left:0.35rem; font-weight:600;\">{arrow} {formatted}{suffix}</span>");
    }
}
