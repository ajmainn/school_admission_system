namespace SchoolAdmission.Api.Domain;

public sealed class AdmissionHold
{
    public string Id { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public AdmissionClass AdmissionClass { get; set; } = null!;
    public string StudentId { get; set; } = string.Empty; // Exactly 17 digits; no real IDs in fixtures
    public HoldStatus Status { get; set; } = HoldStatus.HELD;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
}
