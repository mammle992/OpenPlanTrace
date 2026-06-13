namespace OpenPlanTrace;

public readonly record struct PlanVector(double X, double Y)
{
    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public PlanVector Normalize()
    {
        var length = Length;
        return length <= double.Epsilon ? new PlanVector(0, 0) : new PlanVector(X / length, Y / length);
    }

    public double Dot(PlanVector other) => (X * other.X) + (Y * other.Y);

    public double Cross(PlanVector other) => (X * other.Y) - (Y * other.X);

    public static PlanVector operator *(PlanVector vector, double scalar) =>
        new(vector.X * scalar, vector.Y * scalar);
}
