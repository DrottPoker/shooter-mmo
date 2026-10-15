using Mmorpg.SharedTests;

namespace Mmorpg.Shared.Tests;

// The Unity EditMode tests run the same golden cases, so both runtimes must produce and
// accept exactly the pinned protocol bytes.
public sealed class RealtimeProtocolGoldenTests
{
    [Fact]
    public void EveryMessageEncodesToItsPinnedBytes()
    {
        AssertNoFailures(RealtimeProtocolGolden.VerifyVectors());
    }

    [Fact]
    public void TruncatedExtendedOrMisaddressedPacketsAreRejected()
    {
        AssertNoFailures(RealtimeProtocolGolden.VerifyStructuralRejections());
    }

    [Fact]
    public void FieldRulesAcceptAndRejectExactlyAsPinned()
    {
        AssertNoFailures(RealtimeProtocolGolden.VerifyDecodeCases());
    }

    [Fact]
    public void InvalidValuesFailBeforeEncoding()
    {
        AssertNoFailures(RealtimeProtocolGolden.VerifyEncodeRejections());
    }

    [Fact]
    public void ProtocolConstantsAndEnumValuesArePinned()
    {
        AssertNoFailures(RealtimeProtocolGolden.VerifyConstants());
    }

    private static void AssertNoFailures(IReadOnlyList<string> failures)
    {
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
