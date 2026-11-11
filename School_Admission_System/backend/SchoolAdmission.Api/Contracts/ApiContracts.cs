using System.ComponentModel.DataAnnotations;

namespace SchoolAdmission.Api.Contracts;

// Success JSON field names must exactly match the assignment (ASP.NET camelCase serialization).
public sealed record CreateClassRequest(
    [Required, MinLength(1)] string Name,
    [Range(1, int.MaxValue)] int TotalSeats);

public sealed record CreatedClassResponse(string Id, string Name, int TotalSeats);
public sealed record ClassSeatsResponse(
    string Id, string Name, int TotalSeats, int HeldSeats,
    int ConfirmedSeats, int AvailableSeats);

public sealed record CreatedHoldResponse(
    string HoldId, string ClassId, string StudentId, string Status, DateTime ExpiresAt);

public sealed record HoldDetailsResponse(
    string HoldId, string ClassId, string Status, DateTime ExpiresAt);

public sealed record ConfirmHoldResponse(string HoldId, string Status);
public sealed record StudentHoldResponse(
    string HoldId, string ClassId, string ClassName, string Status, DateTime ExpiresAt);

public sealed record ErrorResponse(string Error);
