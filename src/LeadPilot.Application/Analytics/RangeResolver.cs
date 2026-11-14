using LeadPilot.Application.Conversion;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Analytics;

/// <summary>Turns a preset or custom range into workspace-local day boundaries and UTC instants (see <see cref="ResolvedRange"/> for the semantics).</summary>
public static class RangeResolver
{
    public static Result<ResolvedRange> Resolve(RangeRequest request, string timeZoneId, DateTime nowUtc, int maxDays)
    {
        TimeZoneInfo tz;
        try { tz = SlotCalculator.FindZone(timeZoneId); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return Result.Failure<ResolvedRange>(Error.Validation("The workspace time zone is not valid. Check Demo & trial settings.")); }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz));
        DateOnly from, to; string label;
        switch (request.Preset)
        {
            case RangePreset.Today: from = to = today; label = "Today"; break;
            case RangePreset.Yesterday: from = to = today.AddDays(-1); label = "Yesterday"; break;
            case RangePreset.Last7Days: from = today.AddDays(-6); to = today; label = "Last 7 days"; break;
            case RangePreset.Last30Days: from = today.AddDays(-29); to = today; label = "Last 30 days"; break;
            case RangePreset.Last90Days: from = today.AddDays(-89); to = today; label = "Last 90 days"; break;
            case RangePreset.ThisMonth: from = new DateOnly(today.Year, today.Month, 1); to = today; label = "This month"; break;
            case RangePreset.LastMonth: { var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1); from = first; to = first.AddMonths(1).AddDays(-1); label = "Last month"; break; }
            case RangePreset.ThisYear: from = new DateOnly(today.Year, 1, 1); to = today; label = "This year"; break;
            case RangePreset.Custom:
                if (request.From is null || request.To is null) return Result.Failure<ResolvedRange>(Error.Validation("A custom range needs both a start and an end date."));
                from = request.From.Value; to = request.To.Value; label = "Custom range";
                break;
            default: return Result.Failure<ResolvedRange>(Error.Validation("Unknown date range preset."));
        }
        if (to < from) return Result.Failure<ResolvedRange>(Error.Validation("The end date must not be before the start date."));
        var days = to.DayNumber - from.DayNumber + 1;
        if (days > maxDays) return Result.Failure<ResolvedRange>(Error.Validation($"The range may cover at most {maxDays} days."));
        if (from.Year < 2000 || to.Year > 2100) return Result.Failure<ResolvedRange>(Error.Validation("The dates are outside the supported range."));

        var prevTo = from.AddDays(-1); var prevFrom = from.AddDays(-days);
        return Result.Success(new ResolvedRange(timeZoneId, from, to, DayStart(tz, from), DayStart(tz, to.AddDays(1)), prevFrom, prevTo, DayStart(tz, prevFrom), DayStart(tz, from), label, request.Compare));
    }

    /// <summary>UTC instant of local midnight of <paramref name="day"/> (a midnight inside a DST gap becomes the first valid instant after it).</summary>
    public static DateTime DayStart(TimeZoneInfo tz, DateOnly day) => SlotCalculator.ToUtc(tz, day.ToDateTime(TimeOnly.MinValue));

    public static DateOnly LocalDay(TimeZoneInfo tz, DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));

    public static RangeDto ToDto(ResolvedRange r) => new(r.TimeZoneId, r.FromLocal, r.ToLocal, r.FromUtc, r.ToUtcExclusive, r.PrevFromLocal, r.PrevToLocal, r.Label, r.Compare);

    /// <summary>Percentage with one decimal and the documented guard: no denominator, no rate (null — never 0 %).</summary>
    public static decimal? Percent(decimal numerator, decimal denominator) => denominator <= 0 ? null : decimal.Round(numerator * 100m / denominator, 1, MidpointRounding.AwayFromZero);

    public static decimal? Change(decimal current, decimal? previous) => previous is null or 0 ? null : decimal.Round((current - previous.Value) * 100m / previous.Value, 1, MidpointRounding.AwayFromZero);
}
