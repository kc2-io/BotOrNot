using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class ReplayEventMatcherTests
{
    [Test]
    public void Correlate_AcceptsMeasuredFrameOffsetAndExactActor()
    {
        var elimination = new EliminationEventEvidence(1, 100, "victim", "actor", false, 3);
        var observation = State(2, 101.004, "victim", "actor", false);

        var result = ReplayEventMatcher.Correlate([elimination], [observation]);

        Assert.That(result.Matches[1], Is.SameAs(observation));
    }

    [Test]
    public void Correlate_RejectsOutsideMeasuredTolerance()
    {
        var result = ReplayEventMatcher.Correlate(
            [new EliminationEventEvidence(1, 100, "victim", "actor", false, 3)],
            [State(2, 101.101, "victim", "actor", false)]);

        Assert.That(result.Matches, Is.Empty);
    }

    [Test]
    public void Correlate_PrefersExactActorOverUnresolvedActor()
    {
        var exact = State(2, 100.8, "victim", "actor", false);
        var unresolved = State(3, 100.1, "victim", null, false);

        var result = ReplayEventMatcher.Correlate(
            [new EliminationEventEvidence(1, 100, "victim", "actor", false, 3)],
            [unresolved, exact]);

        Assert.That(result.Matches[1], Is.SameAs(exact));
    }

    [Test]
    public void Correlate_AmbiguousCandidatesStayUnmatchedAndRelated()
    {
        var result = ReplayEventMatcher.Correlate(
            [new EliminationEventEvidence(1, 100, "victim", "actor", false, 3)],
            [State(2, 100.2, "victim", null, false), State(3, 100.3, "victim", null, false)]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.RelatedObservationSequences, Is.EquivalentTo(new[] { 2, 3 }));
        });
    }

    [Test]
    public void Correlate_TwoCloseFinishesForSameVictimRemainUnmatched()
    {
        var result = ReplayEventMatcher.Correlate(
            [
                new EliminationEventEvidence(1, 100, "victim", "actor", false, 3),
                new EliminationEventEvidence(2, 100.5, "victim", "actor", false, 3)
            ],
            [
                State(3, 100.2, "victim", "actor", false),
                State(4, 100.7, "victim", "actor", false)
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Matches, Is.Empty);
            Assert.That(result.RelatedObservationSequences, Is.EquivalentTo(new[] { 3, 4 }));
        });
    }

    [Test]
    public void Correlate_RawNullableDbnoDistinguishesKnockFromFinish()
    {
        var knock = new EliminationEventEvidence(1, 100, "victim", "actor", true, 3);
        var finish = new EliminationEventEvidence(2, 102, "victim", "actor", false, 3);
        var down = State(3, 100.1, "victim", "actor", true);
        var up = State(4, 102.1, "victim", "actor", false);

        var result = ReplayEventMatcher.Correlate([knock, finish], [down, up]);

        Assert.That(result.Matches[1], Is.SameAs(down));
        Assert.That(result.Matches[2], Is.SameAs(up));
    }

    private static PlayerStateEventEvidence State(
        int sequence, double time, string victim, string? actor, bool? isDbno) =>
        new(sequence, time, victim, actor, isDbno, null, null, Array.Empty<string>());
}
