using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class StormCircleResolverTests
{
    [Test]
    public void Resolve_UsesLatestPriorPhaseAndTreatsEqualTimeAsNewPhase()
    {
        var resolver = new StormCircleResolver([
            new(200, 2, 13, 7),
            new(100, 1, 13, 7),
            new(300, 3, 13, 7)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(resolver.Resolve(99), Is.EqualTo(StormCircleResolution.Unknown));
            Assert.That(resolver.Resolve(100), Is.EqualTo(StormCircleResolution.ForRecordedPhase(1)));
            Assert.That(resolver.Resolve(199.999), Is.EqualTo(StormCircleResolution.ForRecordedPhase(1)));
            Assert.That(resolver.Resolve(200), Is.EqualTo(StormCircleResolution.ForRecordedPhase(2)));
        });
    }

    [Test]
    public void Resolve_OnlyCallsExplicitPhaseZeroBeforeFirstCircle()
    {
        var resolver = new StormCircleResolver([
            new(50, 0, 13, 7),
            new(100, 1, 13, 7)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(resolver.Resolve(49).Status, Is.EqualTo(StormCircleStatus.Unknown));
            Assert.That(resolver.Resolve(50), Is.EqualTo(StormCircleResolution.BeforeFirstCircle));
            Assert.That(resolver.Resolve(99).Status, Is.EqualTo(StormCircleStatus.BeforeFirstCircle));
        });
    }

    [Test]
    public void Resolve_SameChannelTieWithDisagreementIsUnknown()
    {
        var resolver = new StormCircleResolver([
            new(100, 1, 13, 7),
            new(100, 2, 13, 7)
        ]);

        Assert.That(resolver.Resolve(100), Is.EqualTo(StormCircleResolution.Unknown));
    }

    [Test]
    public void Resolve_DifferentChannelsAreUnknownEvenWhenTiedValuesAgree()
    {
        var conflict = new StormCircleResolver([
            new(100, 1, 13, 7),
            new(100, 2, 13, 8)
        ]);
        var duplicate = new StormCircleResolver([
            new(100, 2, 13, 7),
            new(100, 2, 13, 8)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(conflict.Resolve(100), Is.EqualTo(StormCircleResolution.Unknown));
            Assert.That(duplicate.Resolve(100), Is.EqualTo(StormCircleResolution.Unknown));
        });
    }

    [Test]
    public void Resolve_InterleavedDifferentActorsIsConservativelyUnknown()
    {
        var resolver = new StormCircleResolver([
            new(100, 1, 13, 7, 101),
            new(150, 1, 13, 8, 202),
            new(200, 2, 13, 7, 101),
            new(250, 2, 13, 8, 202)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(resolver.Resolve(100), Is.EqualTo(StormCircleResolution.Unknown));
            Assert.That(resolver.Resolve(225), Is.EqualTo(StormCircleResolution.Unknown));
        });
    }

    [Test]
    public void Resolve_OneActorAcrossChangedChannelsRemainsAuthoritative()
    {
        var resolver = new StormCircleResolver([
            new(100, 1, 13, 7, 101),
            new(200, 2, 13, 8, 101)
        ]);

        Assert.That(resolver.Resolve(200), Is.EqualTo(StormCircleResolution.ForRecordedPhase(2)));
    }

    [Test]
    public void Resolve_InvalidLatestPhaseInvalidatesUntilNextValidObservation()
    {
        var resolver = new StormCircleResolver([
            new(100, 1, 13, 7),
            new(200, 14, 13, 7),
            new(300, null, 13, 7),
            new(400, 4, 13, 7)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(resolver.Resolve(199), Is.EqualTo(StormCircleResolution.ForRecordedPhase(1)));
            Assert.That(resolver.Resolve(200), Is.EqualTo(StormCircleResolution.Unknown));
            Assert.That(resolver.Resolve(350), Is.EqualTo(StormCircleResolution.Unknown));
            Assert.That(resolver.Resolve(400), Is.EqualTo(StormCircleResolution.ForRecordedPhase(4)));
        });
    }

    [TestCase(double.NaN)]
    [TestCase(double.NegativeInfinity)]
    [TestCase(-1d)]
    public void Resolve_InvalidEventTimeIsUnknown(double eventTime)
    {
        var resolver = new StormCircleResolver([new(100, 1, 13, 7)]);

        Assert.That(resolver.Resolve(eventTime), Is.EqualTo(StormCircleResolution.Unknown));
    }
}
