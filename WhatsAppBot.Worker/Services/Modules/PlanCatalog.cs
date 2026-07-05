namespace WhatsAppBot.Worker.Services.Modules;

public static class PlanCatalog
{
    public const string Starter = "Starter";
    public const string Professional = "Professional";
    public const string Premium = "Premium";
    public const string Enterprise = "Enterprise";

    private static readonly string[] PlanOrder = [Starter, Professional, Premium, Enterprise];

    public static string Normalize(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return Professional;

        return plan.Trim().ToLowerInvariant() switch
        {
            "free" or "local" or "starter" => Starter,
            "standard" or "pro" or "professional" => Professional,
            "premium" => Premium,
            "enterprise" => Enterprise,
            _ => Professional
        };
    }

    public static bool Allows(string? plan, string? minimumPlan)
        => Rank(plan) >= Rank(minimumPlan);

    public static decimal MonthlyPrice(string? plan)
        => Normalize(plan) switch
        {
            Starter => 59m,
            Professional => 299m,
            Premium => 499m,
            Enterprise => 899m,
            _ => 299m
        };

    private static int Rank(string? plan)
    {
        var normalized = Normalize(plan);
        var index = Array.FindIndex(PlanOrder, p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : Array.FindIndex(PlanOrder, p => p == Professional);
    }
}
