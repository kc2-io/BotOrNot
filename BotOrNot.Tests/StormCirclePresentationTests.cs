using BotOrNot.Core.Models;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class StormCirclePresentationTests
{
    [Test]
    public void SetStormCircle_RepeatedUnknownFinishClearsEarlierRecordedPhase()
    {
        var row = new PlayerRow();
        row.SetStormCircle(StormCircleResolution.ForRecordedPhase(4));

        row.SetStormCircle(StormCircleResolution.Unknown);

        Assert.Multiple(() =>
        {
            Assert.That(row.CircleNumber, Is.Null);
            Assert.That(row.CircleStatus, Is.EqualTo(StormCircleStatus.Unknown));
            Assert.That(row.StormPhaseDisplay, Is.EqualTo("Unknown"));
            Assert.That(row.StormPhaseSortValue, Is.Null);
        });
    }

    [Test]
    public void Presentation_SeparatesExplicitPreFirstFromUnknown()
    {
        var preFirst = new PlayerRow { CircleStatus = StormCircleStatus.BeforeFirstCircle };
        var unknown = new PlayerRow { CircleStatus = StormCircleStatus.Unknown };

        Assert.Multiple(() =>
        {
            Assert.That(preFirst.StormPhaseDisplay, Is.EqualTo("Before phase 1"));
            Assert.That(preFirst.StormPhaseSortValue, Is.EqualTo("0"));
            Assert.That(preFirst.StormPhaseTooltip, Does.Contain("explicitly recorded phase 0"));
            Assert.That(unknown.StormPhaseDisplay, Is.EqualTo("Unknown"));
            Assert.That(unknown.StormPhaseSortValue, Is.Null);
            Assert.That(unknown.StormPhaseTooltip, Does.Contain("No trustworthy storm phase"));
        });
    }
}
