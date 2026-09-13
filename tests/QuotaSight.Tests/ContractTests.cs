using QuotaSight.Infrastructure;

namespace QuotaSight.Tests;

public sealed class ContractTests
{
    [Fact]
    public void History_has_no_synchronous_read_api()
    {
        Assert.Null(typeof(JsonlQuotaHistory).GetMethod("ReadEventsSync"));
    }
}
