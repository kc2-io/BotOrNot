using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class Issue57LifecycleTraceTests
{
    [Test]
    public void AnonymizedMeasuredTrace_ExplicitRecoveryPreventsThirdOwnerCredit()
    {
        const string owner = "P1";
        var eliminations = new[]
        {
            Elim(128, 630.110, "P21", owner, true, 5),
            Elim(129, 633.210, "P21", owner, false, 5),
            Elim(130, 650.810, "P13", owner, true, 5),
            Elim(132, 657.760, "P13", "P94", false, 5),
            Elim(162, 1089.590, "P6", owner, true, 3),
            Elim(166, 1124.290, "P6", "P94", false, 50)
        };
        var states = new[]
        {
            State(149, 631.001, "P21", owner, true, 5),
            State(150, 634.002, "P21", null, false, null),
            State(151, 651.010, "P13", owner, true, 5),
            State(153, 658.010, "P13", "P94", false, null),
            State(198, 1089.996, "P6", owner, true, 3),
            // This raw nullable false is an unmatched recovery observation. Cause 50 alone
            // is not used as recovery evidence.
            State(199, 1096.994, "P6", null, false, 50),
            State(203, 1124.991, "P6", "P94", null, null)
        };

        var correlation = ReplayEventMatcher.Correlate(eliminations, states);
        var timeline = eliminations.Select(elimination =>
        {
            correlation.Matches.TryGetValue(elimination.Sequence, out var state);
            return new CombatLifecycleEvent(
                elimination.Sequence,
                elimination.ReplayTimeSeconds,
                elimination.IsKnock ? CombatLifecycleEventKind.Knock : CombatLifecycleEventKind.Finish,
                elimination.VictimId,
                elimination.ActorId,
                DbnoTrueObserved: state?.IsDbno == true,
                DeathCause: DeathCauseHelper.ResolveEvent(
                    elimination.RawCode, state?.RawDeathCause, state?.DeathTags));
        }).ToList();
        timeline.AddRange(states
            .Where(state => !correlation.RelatedObservationSequences.Contains(state.Sequence))
            .Select(state => new CombatLifecycleEvent(
                1_000_000 + state.Sequence,
                state.ReplayTimeSeconds,
                CombatLifecycleEventKind.PlayerState,
                state.VictimId,
                state.ActorId,
                state.IsDbno,
                state.RebootCounter)));

        var decisions = OwnerEliminationResolver.Resolve(owner, timeline);

        Assert.Multiple(() =>
        {
            Assert.That(correlation.Matches, Has.Count.EqualTo(6));
            Assert.That(decisions.Where(item => item.Status == OwnerCreditStatus.Credited)
                .Select(item => item.EventSequence), Is.EqualTo(new[] { 129, 132 }));
            Assert.That(decisions.Single(item => item.EventSequence == 166).Status,
                Is.EqualTo(OwnerCreditStatus.NotCredited));
        });
    }

    private static EliminationEventEvidence Elim(
        int sequence, double time, string victim, string actor, bool knock, int code) =>
        new(sequence, time, victim, actor, knock, code);

    private static PlayerStateEventEvidence State(
        int sequence, double time, string victim, string? actor, bool? dbno, int? cause) =>
        new(sequence, time, victim, actor, dbno, null, cause, Array.Empty<string>());
}
