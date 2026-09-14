using System.Reflection;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class ReflectionUtilsTests
{
    private static readonly string[] Accessors =
    {
        nameof(ReflectionUtils.GetDouble),
        nameof(ReflectionUtils.GetInt),
        nameof(ReflectionUtils.GetUInt),
        nameof(ReflectionUtils.GetBool),
        nameof(ReflectionUtils.GetNullableBool)
    };

    [Test]
    public void MissingValuesRetainEachAccessorsDefault([ValueSource(nameof(Accessors))] string accessor)
    {
        object? expected = accessor == nameof(ReflectionUtils.GetBool) ? false : null;

        Assert.Multiple(() =>
        {
            Assert.That(Read(accessor, null, "Value"), Is.EqualTo(expected));
            Assert.That(Read(accessor, null, null!), Is.EqualTo(expected));
            Assert.That(Read(accessor, new { Other = 42 }, "Value"), Is.EqualTo(expected));
            Assert.That(Read(accessor, new { Value = (object?)null }, "Value"), Is.EqualTo(expected));
        });
    }

    [Test]
    public void LookupIgnoresCaseAndReadsFreshValueOncePerCall(
        [ValueSource(nameof(Accessors))] string accessor)
    {
        object supportedValue = accessor is nameof(ReflectionUtils.GetBool) or nameof(ReflectionUtils.GetNullableBool)
            ? true
            : 42;
        var source = new CountedProperty { CurrentValue = supportedValue };

        Assert.That(Read(accessor, source, "vAlUe"), Is.EqualTo(supportedValue));
        Assert.That(source.Reads, Is.EqualTo(1));

        source.CurrentValue = null;
        object? expected = accessor == nameof(ReflectionUtils.GetBool) ? false : null;
        Assert.That(Read(accessor, source, "vAlUe"), Is.EqualTo(expected));
        Assert.That(source.Reads, Is.EqualTo(2));
    }

    [Test]
    public void GetterExceptionsKeepReflectionWrapperAndOriginalCause(
        [ValueSource(nameof(Accessors))] string accessor)
    {
        var source = new ThrowingProperty();

        var exception = Assert.Throws<TargetInvocationException>(() => Read(accessor, source, "Value"));

        Assert.That(exception!.InnerException, Is.SameAs(source.Failure));
    }

    [TestCase(1.25d, 1.25d)]
    [TestCase(1.25f, 1.25d)]
    [TestCase("1,234.5", 1234.5d)]
    [TestCase("1.25e2", 125d)]
    [TestCase("invalid", null)]
    [SetCulture("fr-FR")]
    public void DoubleConversionRetainsFastPathsAndInvariantParsing(object value, double? expected)
    {
        Assert.That(ReflectionUtils.GetDouble(new { Value = value }, "Value"), Is.EqualTo(expected));
    }

    [TestCase(int.MinValue, int.MinValue)]
    [TestCase(int.MaxValue, int.MaxValue)]
    [TestCase((byte)255, 255)]
    [TestCase((short)-123, -123)]
    [TestCase((uint)int.MaxValue, int.MaxValue)]
    [TestCase(2147483648u, null)]
    [TestCase(uint.MaxValue, null)]
    [TestCase(" +42 ", 42)]
    [TestCase("1,234", null)]
    [TestCase("2147483648", null)]
    [TestCase("invalid", null)]
    [SetCulture("fr-FR")]
    public void IntConversionRetainsRangeChecksAndIntegerParsing(object value, int? expected)
    {
        Assert.That(ReflectionUtils.GetInt(new { Value = value }, "Value"), Is.EqualTo(expected));
    }

    [TestCase(uint.MaxValue, uint.MaxValue)]
    [TestCase(0, 0u)]
    [TestCase(int.MaxValue, (uint)int.MaxValue)]
    [TestCase(-1, null)]
    [TestCase(" +42 ", 42u)]
    [TestCase("4294967295", uint.MaxValue)]
    [TestCase("4294967296", null)]
    [TestCase("-1", null)]
    [TestCase("1,234", null)]
    [TestCase("invalid", null)]
    [SetCulture("fr-FR")]
    public void UIntConversionRetainsRangeChecksAndIntegerParsing(object value, uint? expected)
    {
        Assert.That(ReflectionUtils.GetUInt(new { Value = value }, "Value"), Is.EqualTo(expected));
    }

    [TestCase(true, true, true)]
    [TestCase(false, false, false)]
    [TestCase("TrUe", true, true)]
    [TestCase("FaLsE", false, false)]
    [TestCase(" true ", false, true)]
    [TestCase("invalid", false, null)]
    [TestCase(1, false, null)]
    public void BooleanAccessorsRetainDistinctConversionPolicies(object value, bool expectedBool, bool? expectedNullableBool)
    {
        var source = new { Value = value };

        Assert.Multiple(() =>
        {
            Assert.That(ReflectionUtils.GetBool(source, "Value"), Is.EqualTo(expectedBool));
            Assert.That(ReflectionUtils.GetNullableBool(source, "Value"), Is.EqualTo(expectedNullableBool));
        });
    }

    [Test]
    public void OnlyNullableBoolParsesNonStringValuesThroughToString()
    {
        var source = new { Value = new TrueText() };

        Assert.Multiple(() =>
        {
            Assert.That(ReflectionUtils.GetBool(source, "Value"), Is.False);
            Assert.That(ReflectionUtils.GetNullableBool(source, "Value"), Is.True);
        });
    }

    private static object? Read(string accessor, object? source, string propertyName) => accessor switch
    {
        nameof(ReflectionUtils.GetDouble) => ReflectionUtils.GetDouble(source, propertyName),
        nameof(ReflectionUtils.GetInt) => ReflectionUtils.GetInt(source, propertyName),
        nameof(ReflectionUtils.GetUInt) => ReflectionUtils.GetUInt(source, propertyName),
        nameof(ReflectionUtils.GetBool) => ReflectionUtils.GetBool(source, propertyName),
        nameof(ReflectionUtils.GetNullableBool) => ReflectionUtils.GetNullableBool(source, propertyName),
        _ => throw new ArgumentOutOfRangeException(nameof(accessor))
    };

    private sealed class CountedProperty
    {
        public object? CurrentValue { get; set; }
        public int Reads { get; private set; }
        public object? Value
        {
            get
            {
                Reads++;
                return CurrentValue;
            }
        }
    }

    private sealed class ThrowingProperty
    {
        public InvalidOperationException Failure { get; } = new("Test getter failure");
        public object Value => throw Failure;
    }

    private sealed class TrueText
    {
        public override string ToString() => "true";
    }
}
