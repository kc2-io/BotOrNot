using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class OwnerEliminationResolverTests
{
    private const string Owner = "owner";

    [Test]
    public void Resolve_OwnerKnockRemainsActiveWithoutArbitraryTimeout()
    {
        var result = Resolve(
            Knock(1, 10, "victim", Owner, observedDbno: true),
            Finish(2, 310, "victim", "teammate"));

        AssertDecision(result.Single(), OwnerCreditStatus.Credited, OwnerCreditSource.OwnerKnock);
    }

    [Test]
    public void Resolve_ExplicitDbnoRecoveryAfterObservedDownClearsOwnerKnock()
    {
        var result = Resolve(
            Knock(1, 10, "victim", Owner, observedDbno: true),
            State(2, 20, "victim", isDbno: false),
            Finish(3, 30, "victim", "other"));

        AssertDecision(result.Single(), OwnerCreditStatus.NotCredited, OwnerCreditSource.OtherPlayerFinish);
    }

    [Test]
    public void Resolve_FalseWithoutPriorRawTrueDoesNotInventRecovery()
    {
        var result = Resolve(
            Knock(1, 10, "victim", Owner, observedDbno: false),
            State(2, 20, "victim", isDbno: false),
            Finish(3, 30, "victim", "other"));

        AssertDecision(result.Single(), OwnerCreditStatus.Credited, OwnerCreditSource.OwnerKnock);
    }

    [Test]
    public void Resolve_NewKnockReplacesEarlierOwnerKnock()
    {
        var result = Resolve(
            Knock(1, 10, "victim", Owner, true),
            Knock(2, 20, "victim", "other", true),
            Finish(3, 30, "victim", Owner));

        AssertDecision(result.Single(), OwnerCreditStatus.NotCredited, OwnerCreditSource.OtherPlayerKnock);
    }

    [Test]
    public void Resolve_OwnerFinishingAnotherPlayersActiveKnockIsNotCredited()
    {
        var result = Resolve(
            Knock(1, 10, "victim", "other", true),
            Finish(2, 20, "victim", Owner));

        AssertDecision(result.Single(), OwnerCreditStatus.NotCredited, OwnerCreditSource.OtherPlayerKnock);
    }

    [Test]
    public void Resolve_DirectOwnerFinishIsCredited()
    {
        var result = Resolve(Finish(1, 10, "victim", Owner));

        AssertDecision(result.Single(), OwnerCreditStatus.Credited, OwnerCreditSource.DirectFinish);
    }

    [Test]
    public void Resolve_InitialRebootCounterIsBaselineAndLaterIncreaseResetsLife()
    {
        var firstLife = Resolve(
            State(1, 1, "victim", counter: 4),
            Knock(2, 10, "victim", Owner, true),
            Finish(3, 20, "victim", "other"));
        var resetLife = Resolve(
            State(1, 1, "victim", counter: 4),
            Knock(2, 10, "victim", Owner, true),
            State(3, 15, "victim", counter: 5),
            Finish(4, 20, "victim", "other"));

        AssertDecision(firstLife.Single(), OwnerCreditStatus.Credited, OwnerCreditSource.OwnerKnock);
        AssertDecision(resetLife.Single(), OwnerCreditStatus.NotCredited, OwnerCreditSource.OtherPlayerFinish);
    }

    [Test]
    public void Resolve_StateWithoutTimestampCannotResetOrderedLifecycle()
    {
        var result = Resolve(
            Knock(1, 10, "victim", Owner, true),
            State(2, null, "victim", isDbno: false),
            Finish(3, 20, "victim", "other"));

        AssertDecision(result.Single(), OwnerCreditStatus.Credited, OwnerCreditSource.OwnerKnock);
    }

    [Test]
    public void Resolve_SameTimestampRecoveryAndFinishStaysUncertain()
    {
        var result = Resolve(
            Knock(1, 10, "victim", Owner, true),
            State(2, 20, "victim", isDbno: false),
            Finish(3, 20, "victim", "other"));

        AssertDecision(result.Single(), OwnerCreditStatus.Uncertain, OwnerCreditSource.AmbiguousLifecycle);
    }

    [Test]
    public void Resolve_RepeatedLivesRemainDistinct()
    {
        var result = Resolve(
            Finish(1, 10, "victim", Owner),
            Finish(2, 20, "victim", Owner));

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(item => item.Status == OwnerCreditStatus.Credited), Is.True);
    }

    [Test]
    public void Resolve_SelfEliminationIsNeverOwnerCredit()
    {
        var result = Resolve(Finish(1, 10, Owner, Owner));

        AssertDecision(result.Single(), OwnerCreditStatus.NotCredited, OwnerCreditSource.SelfElimination);
    }

    private static IReadOnlyList<OwnerEliminationDecision> Resolve(params CombatLifecycleEvent[] events) =>
        OwnerEliminationResolver.Resolve(Owner, events);

    private static CombatLifecycleEvent Knock(
        int sequence, double time, string victim, string actor, bool observedDbno) =>
        new(sequence, time, CombatLifecycleEventKind.Knock, victim, actor,
            DbnoTrueObserved: observedDbno);

    private static CombatLifecycleEvent Finish(int sequence, double time, string victim, string actor) =>
        new(sequence, time, CombatLifecycleEventKind.Finish, victim, actor,
            DeathCause: DeathCauseHelper.ResolveEvent(3, null, null));

    private static CombatLifecycleEvent State(
        int sequence, double? time, string victim, bool? isDbno = null, int? counter = null) =>
        new(sequence, time, CombatLifecycleEventKind.PlayerState, victim,
            IsDbno: isDbno, RebootCounter: counter);

    private static void AssertDecision(
        OwnerEliminationDecision decision, OwnerCreditStatus status, OwnerCreditSource source)
    {
        Assert.Multiple(() =>
        {
            Assert.That(decision.Status, Is.EqualTo(status));
            Assert.That(decision.Source, Is.EqualTo(source));
        });
    }
}
