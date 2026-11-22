using System.Text;
using QuotaSight.Collector;

namespace QuotaSight.Collector.Tests;

public sealed class TokscaleUsageQuotaContractTests
{
    [Fact]
    public void ReadsOnlyProviderSpecificContractWindowsAndOmitsPrivateFields()
    {
        const string json = """
            [
              {"provider":"Claude","account":{"id":"synthetic-a","label":"private label","is_active":true},"email":"private@example.test","plan":"private plan","credential_source":"private source","freshness":{"fetched_at":1760000000,"stale":false},"metrics":[
                {"label":"Session","remaining_percent":0,"remaining_label":"private free text","resets_at":"2026-11-23T00:00:00Z","used_percent":100},
                {"label":"Weekly","remaining_percent":100,"resets_at":null},
                {"label":"Fable","remaining_percent":13.5,"resets_at":null}
              ]},
              {"provider":"Codex","account":null,"freshness":null,"metrics":[
                {"label":"5h","remaining_percent":0,"resets_at":"2026-11-23T00:00:00Z"},
                {"label":"Weekly","remaining_percent":100,"resets_at":null},
                {"label":"OpenAI API","remaining_percent":42,"resets_at":null}
              ]},
              {"provider":"OpenCode Go","account":null,"plan":"Go","metrics":[
                {"label":"Rolling","remaining_percent":57.5,"remaining_label":"rate-limited","resets_at":null},
                {"label":"Weekly","remaining_percent":50,"remaining_label":"free-text must not escape","resets_at":"2026-11-24T00:00:00Z"},
                {"label":"Monthly","remaining_percent":100,"resets_at":null}
              ]},
              {"provider":"Antigravity","metrics":[{"label":"Rolling","remaining_percent":99,"resets_at":null}]}
            ]
            """;

        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        Assert.Collection(result.Providers,
            claude =>
            {
                Assert.Equal("Claude", claude.Provider);
                Assert.Equal(TokscaleUsageQuotaFreshness.Fresh, claude.Freshness);
                Assert.Collection(claude.Windows,
                    session => AssertWindow(session, "Session", 0, "2026-11-23T00:00:00Z"),
                    weekly => AssertWindow(weekly, "Weekly", 100, null));
            },
            codex =>
            {
                Assert.Equal("Codex", codex.Provider);
                Assert.Equal(TokscaleUsageQuotaFreshness.Unknown, codex.Freshness);
                Assert.Collection(codex.Windows,
                    fiveHours => AssertWindow(fiveHours, "5h", 0, "2026-11-23T00:00:00Z"),
                    weekly => AssertWindow(weekly, "Weekly", 100, null));
            },
            go =>
            {
                Assert.Equal("OpenCode Go", go.Provider);
                Assert.Collection(go.Windows,
                    rolling => AssertWindow(rolling, "Rolling", 57.5, null),
                    weekly => AssertWindow(weekly, "Weekly", 50, "2026-11-24T00:00:00Z"),
                    monthly => AssertWindow(monthly, "Monthly", 100, null));
            });
        var projection = string.Join('|', result.Providers.SelectMany(p => p.Windows.SelectMany(w => new[] { p.Provider, w.Label, w.ResetAt ?? "" })));
        Assert.DoesNotContain("private", projection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", projection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fable", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI API", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("Antigravity", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesStaleZeroAndHundredAsDistinctFromUnknown()
    {
        const string json = """
            [
              {"provider":"Claude","freshness":{"fetched_at":1760000000,"stale":true},"metrics":[{"label":"Session","remaining_percent":0,"resets_at":"not a date"},{"label":"Weekly","remaining_percent":100,"resets_at":null}]},
              {"provider":"Codex","metrics":[{"label":"Weekly","remaining_percent":null,"resets_at":null}]}
            ]
            """;

        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        Assert.Equal(TokscaleUsageQuotaFreshness.Stale, result.Providers[0].Freshness);
        Assert.Equal(0, result.Providers[0].Windows[0].RemainingPercent);
        Assert.Null(result.Providers[0].Windows[0].ResetAt);
        Assert.Equal(100, result.Providers[0].Windows[1].RemainingPercent);
        Assert.Equal(TokscaleUsageQuotaValueStatus.Unknown, result.Providers[1].Windows[0].ValueStatus);
        Assert.Null(result.Providers[1].Windows[0].RemainingPercent);
    }

    [Fact]
    public void EmptyArrayIsAValidEmptySuccess()
    {
        var result = TokscaleUsageQuotaContract.Parse("[]"u8);

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        Assert.Empty(result.Providers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[{")]
    [InlineData("null")]
    public void MalformedOrWrongRootIsRejected(string json)
    {
        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Malformed, result.Status);
        Assert.Empty(result.Providers);
    }

    [Fact]
    public void MultipleAccountsRequireExactlyOneExplicitActiveAccount()
    {
        const string json = """
            [
              {"provider":"Codex","account":{"id":"one","is_active":false},"metrics":[{"label":"Weekly","remaining_percent":10}]},
              {"provider":"Codex","account":{"id":"two","is_active":true},"metrics":[{"label":"Weekly","remaining_percent":20}]}
            ]
            """;

        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        var provider = Assert.Single(result.Providers);
        Assert.Equal("Codex", provider.Provider);
        Assert.Equal(20, Assert.Single(provider.Windows).RemainingPercent);
    }

    [Fact]
    public void AmbiguousAccountsAreUnavailableInsteadOfSummedOrGuessed()
    {
        const string json = """
            [
              {"provider":"Codex","account":{"id":"one","is_active":false},"metrics":[{"label":"Weekly","remaining_percent":10}]},
              {"provider":"Codex","account":{"id":"two","is_active":false},"metrics":[{"label":"Weekly","remaining_percent":20}]}
            ]
            """;

        var result = TokscaleUsageQuotaContract.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(TokscaleUsageQuotaParseStatus.Success, result.Status);
        var provider = Assert.Single(result.Providers);
        Assert.Equal(TokscaleUsageQuotaProviderStatus.Unavailable, provider.Status);
        Assert.Empty(provider.Windows);
    }

    [Fact]
    public void RejectsOutOfRangePercentAndOversizedBody()
    {
        var outOfRange = TokscaleUsageQuotaContract.Parse("""[{"provider":"Codex","metrics":[{"label":"Weekly","remaining_percent":100.1}]}]"""u8);
        var oversized = TokscaleUsageQuotaContract.Parse(new byte[TokscaleUsageQuotaContract.MaxInputBytes + 1]);

        Assert.Equal(TokscaleUsageQuotaParseStatus.Malformed, outOfRange.Status);
        Assert.Equal(TokscaleUsageQuotaParseStatus.Malformed, oversized.Status);
    }

    private static void AssertWindow(TokscaleUsageQuotaWindow window, string label, double? remaining, string? reset)
    {
        Assert.Equal(label, window.Label);
        Assert.Equal(remaining, window.RemainingPercent);
        Assert.Equal(remaining is null ? TokscaleUsageQuotaValueStatus.Unknown : TokscaleUsageQuotaValueStatus.Known, window.ValueStatus);
        Assert.Equal(reset, window.ResetAt);
    }
}
