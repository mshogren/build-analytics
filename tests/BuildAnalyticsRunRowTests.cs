using Xunit;

[Collection("sequential")]
public sealed class BuildAnalyticsRunRowTests
{
    [Fact]
    public void ToCells_ReturnsSameCountAsHeaders()
    {
        var row = new BuildAnalyticsRunRow(
            42, 7, "Pipe", "2024.01.01.1", "completed", "succeeded", "manual",
            "2024-01-01T00:00:00Z", "2024-01-01T00:05:00Z", "2024-01-01T00:10:00Z",
            300, 300, 600, "refs/heads/main", "abc", "Requester", "Builder",
            "Azure Pipelines", 9, "Azure Pipelines", false, "tag1;tag2",
            "vstfs:///Build/Build/42", "https://example/build/42", "/tmp/run");

        var cells = row.ToCells();

        Assert.Equal(BuildAnalyticsRunRow.Headers.Length, cells.Length);
        Assert.Equal(42, cells[0]);
        Assert.Equal("Pipe", cells[2]);
        Assert.Equal(9, cells[18]);
    }
}
