namespace TEHhub.OffsetDoctor.Evidence;

public enum Confidence
{
    LOW,
    MEDIUM,
    HIGH
}

public static class ConfidenceCalculator
{
    public static Confidence Calculate(int score, int independentValidatorsCount)
    {
        if (score >= 90 && independentValidatorsCount >= 2)
        {
            return Confidence.HIGH;
        }

        if (score >= 60)
        {
            return Confidence.MEDIUM;
        }

        return Confidence.LOW;
    }
}
