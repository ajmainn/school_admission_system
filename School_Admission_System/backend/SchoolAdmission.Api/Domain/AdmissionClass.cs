namespace SchoolAdmission.Api.Domain;

public sealed class AdmissionClass
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TotalSeats { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<AdmissionHold> Holds { get; set; } = new();
}
