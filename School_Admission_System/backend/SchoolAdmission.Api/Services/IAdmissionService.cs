using SchoolAdmission.Api.Contracts;

namespace SchoolAdmission.Api.Services;

// Concurrency and lifecycle decisions live in the service, not controllers.
public interface IAdmissionService
{
    Task<CreatedClassResponse> CreateClassAsync(CreateClassRequest request, CancellationToken ct);
    Task<IReadOnlyList<ClassSeatsResponse>> GetClassesAsync(CancellationToken ct);
    Task<ClassSeatsResponse?> GetClassAsync(string classId, CancellationToken ct);
    Task<CreatedHoldResponse> CreateHoldAsync(string classId, string studentId, CancellationToken ct);
    Task<HoldDetailsResponse?> GetHoldAsync(string holdId, CancellationToken ct);
    Task<ConfirmHoldResponse> ConfirmAsync(string holdId, string studentId, CancellationToken ct);
    Task CancelAsync(string holdId, string studentId, CancellationToken ct);
    Task<IReadOnlyList<StudentHoldResponse>> GetMyHoldsAsync(string studentId, CancellationToken ct);
    Task ExpireHoldsAsync(CancellationToken ct);
}
