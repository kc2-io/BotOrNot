using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class DeathCauseHelperTests
{
    [Test]
    public void ResolveEvent_ZeroIsMeaningfulStormCode()
    {
        var result = DeathCauseHelper.ResolveEvent(0, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.DisplayName, Is.EqualTo("Storm (0)"));
            Assert.That(result.Category, Is.EqualTo(DeathCauseCategory.Environment));
            Assert.That(result.RawEventCode, Is.Zero);
            Assert.That(result.Source, Is.EqualTo(DeathCauseSource.EliminationEventCode));
            Assert.That(result.ResolutionStatus, Is.EqualTo(DeathCauseResolutionStatus.Resolved));
        });
    }

    [Test]
    public void ResolveEvent_KillFeedCodeFillsUnspecifiedEventCode()
    {
        var result = DeathCauseHelper.ResolveEvent(50, 4, ["Gameplay.Damage"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.DisplayName, Is.EqualTo("Rifle (4)"));
            Assert.That(result.RawEventCode, Is.EqualTo(50));
            Assert.That(result.RawKillFeedCode, Is.EqualTo(4));
            Assert.That(result.Source, Is.EqualTo(DeathCauseSource.KillFeedCode));
        });
    }

    [Test]
    public void ResolveEvent_SpecificVerifiedTagRefinesBroadCodeAndPreservesRawEvidence()
    {
        var result = DeathCauseHelper.ResolveEvent(4, 4,
            ["Item.Weapon.Ranged.Assault.Area51Gun", "Gameplay.Damage", "gameplay.damage"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.DisplayName, Is.EqualTo("Arc Gun"));
            Assert.That(result.Category, Is.EqualTo(DeathCauseCategory.PlayerWeapon));
            Assert.That(result.Source, Is.EqualTo(DeathCauseSource.SpecificEventTag));
            Assert.That(result.ResolutionStatus, Is.EqualTo(DeathCauseResolutionStatus.Resolved));
            Assert.That(result.RawTags, Has.Count.EqualTo(2));
            Assert.That(result.TagRole, Is.EqualTo(DeathCauseTagRole.KillFeedDeathContext));
        });
    }

    [Test]
    public void ResolveEvent_ConflictingSpecificTagKeepsTagPrecedenceAndConflictStatus()
    {
        var result = DeathCauseHelper.ResolveEvent(0, null, ["Item.Weapon.Area51Gun"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.DisplayName, Is.EqualTo("Arc Gun"));
            Assert.That(result.RawEventCode, Is.Zero);
            Assert.That(result.ResolutionStatus, Is.EqualTo(DeathCauseResolutionStatus.Conflicting));
        });
    }

    [Test]
    public void ResolveEvent_DeployableTurretTagResolvesUnspecifiedEventCode()
    {
        var result = DeathCauseHelper.ResolveEvent(50, null,
            ["Gameplay.Damage.DeployableTurret.Shot"]);

        Assert.That(result.DisplayName, Is.EqualTo("Turret"));
        Assert.That(result.Source, Is.EqualTo(DeathCauseSource.SpecificEventTag));
    }

    [Test]
    public void ResolveEvent_UnknownCodeRemainsReviewable()
    {
        var result = DeathCauseHelper.ResolveEvent(222, null, ["Gameplay.Damage.Future"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.DisplayName, Is.EqualTo("Unknown (222)"));
            Assert.That(result.RawEventCode, Is.EqualTo(222));
            Assert.That(result.RawTags, Does.Contain("Gameplay.Damage.Future"));
            Assert.That(result.ResolutionStatus, Is.EqualTo(DeathCauseResolutionStatus.Unknown));
        });
    }

    [Test]
    public void ResolveLegacy_PreservesDisplayCompatibilityAtLowerConfidence()
    {
        var result = DeathCauseHelper.ResolveLegacy("5", null);

        Assert.Multiple(() =>
        {
            Assert.That(result.DisplayName, Is.EqualTo("SMG (5)"));
            Assert.That(result.Source, Is.EqualTo(DeathCauseSource.LegacyPlayerSnapshot));
            Assert.That(result.Confidence, Is.EqualTo(EvidenceConfidence.Low));
            Assert.That(DeathCauseHelper.GetDisplayName("5"), Is.EqualTo("SMG (5)"));
        });
    }
}
