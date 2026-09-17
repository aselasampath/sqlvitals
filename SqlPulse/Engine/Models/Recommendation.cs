namespace SqlPulse.Engine.Models;

public record Recommendation(
    int RecommendationId,
    string Category,
    string Severity,
    string Title,
    string Description,
    string Action,
    decimal MetricValue,
    decimal Threshold,
    DateTime CaptureTime,
    string SeverityColor,
    string SeverityIcon,
    decimal MetricVsThreshold
);
