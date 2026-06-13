namespace OpenPlanTrace;

public readonly record struct Confidence
{
    public Confidence(double value)
    {
        Value = Math.Clamp(value, 0, 1);
    }

    public double Value { get; }

    public static Confidence None => new(0);

    public static Confidence Low => new(0.35);

    public static Confidence Medium => new(0.65);

    public static Confidence High => new(0.9);

    public override string ToString() => Value.ToString("0.00");

    public static implicit operator double(Confidence confidence) => confidence.Value;
}
