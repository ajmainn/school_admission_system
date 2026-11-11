namespace SchoolAdmission.Api.Services;

public sealed class AdmissionConflictException(string message) : Exception(message);
