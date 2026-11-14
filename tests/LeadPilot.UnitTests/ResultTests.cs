using LeadPilot.Application.Common;
using LeadPilot.Shared.Results;

namespace LeadPilot.UnitTests;

public class ResultTests
{
    [Fact]
    public void Success_has_no_error_and_exposes_value()
    {
        var r = Result.Success(42);
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Value);
        Assert.Equal(Error.None, r.Error);
    }

    [Fact]
    public void Failure_carries_error_and_value_access_throws()
    {
        var r = Result.Failure<int>(Error.NotFound("Lead"));
        Assert.True(r.IsFailure);
        Assert.Equal("not_found", r.Error.Code);
        Assert.Throws<InvalidOperationException>(() => r.Value);
    }

    [Fact]
    public void Failure_without_error_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));

    [Theory]
    [InlineData(0, 25, 0)]
    [InlineData(1, 25, 1)]
    [InlineData(26, 25, 2)]
    [InlineData(50, 25, 2)]
    public void PagedResult_calculates_total_pages(long total, int size, int pages) =>
        Assert.Equal(pages, new PagedResult<int>([], 1, size, total).TotalPages);
}
